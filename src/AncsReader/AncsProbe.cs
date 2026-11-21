using System.Buffers.Binary;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading.Channels;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

// Minimal, bounded diagnostic reader using Apple's ANCS specification.
// Prepare the data channel before subscribing to incoming notifications.
internal static class AncsProbe
{
    static readonly Guid ServiceId = new("7905F431-B5CE-4E99-A40F-4B1E122D00D0");
    static readonly Guid NotificationId = new("9FBF120D-6301-42D9-8C58-25E699A21DBD");
    static readonly Guid ControlId = new("69D1D8F3-45E1-49A8-9821-9BBDFDAAD9D9");
    static readonly Guid DataId = new("22EAC6E9-24D6-4BB5-BE44-B36ACE7C7BFB");
    static byte[] Bytes(IBuffer buffer) { var bytes = new byte[buffer.Length]; DataReader.FromBuffer(buffer).ReadBytes(bytes); return bytes; }
    public static async Task<int> Run(string[] args, Action<object> emit)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        var ct = deadline.Token;
        try
        {
            using var parent = int.TryParse(Environment.GetEnvironmentVariable("ANCS_PARENT_PID"), out var parentId)
                ? System.Diagnostics.Process.GetProcessById(parentId) : null;
            using var parentWatch = new Timer(_ => { if (parent?.HasExited == true) Environment.Exit(0); }, null, 1000, 1000);
            if (args[0] == "ancs-selftest") { SelfTest(); emit(new { kind = "selftest", passed = true }); return 0; }
            if (args[0] == "ancs-devices")
            {
                var devices = await DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true),
                    new[] { "System.Devices.Aep.IsConnected" }, DeviceInformationKind.AssociationEndpoint).AsTask(ct);
                foreach (var d in devices) emit(new { kind = "ble-device", id = d.Id, name = d.Name, paired = d.Pairing.IsPaired });
                return 0;
            }
            if (args[0] != "ancs" || args.Length < 2) throw new ArgumentException("ancs-devices | ancs <paired BLE id> [seconds=45]");
            int seconds = args.Length > 2 ? int.Parse(args[2]) : 45;
            if (seconds < 0 || seconds > 120) throw new ArgumentException("Duration must be 0 (continuous) or 1..120 seconds.");
            using var device = await BluetoothLEDevice.FromIdAsync(args[1]).AsTask(ct)
                ?? throw new InvalidOperationException("BLE device unavailable.");
            using var session = await GattSession.FromDeviceIdAsync(device.BluetoothDeviceId).AsTask(ct);
            session.MaintainConnection = true;
            emit(new { kind = "status", stage = "gatt-connect", device = device.Name });
            var services = await device.GetGattServicesForUuidAsync(ServiceId, BluetoothCacheMode.Uncached).AsTask(ct);
            if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0)
                throw new InvalidOperationException($"ANCS service: {services.Status}, count={services.Services.Count}");
            using var service = services.Services[0];
            var access = await service.RequestAccessAsync().AsTask(ct);
            if (access != DeviceAccessStatus.Allowed) throw new InvalidOperationException("ANCS access: " + access);
            var opened = await service.OpenAsync(GattSharingMode.SharedReadAndWrite).AsTask(ct);
            if (opened is not (GattOpenStatus.Success or GattOpenStatus.AlreadyOpened)) throw new InvalidOperationException("ANCS open: " + opened);
            async Task<GattCharacteristic> Bind(Guid id)
            {
                var r = await service.GetCharacteristicsForUuidAsync(id, BluetoothCacheMode.Uncached).AsTask(ct);
                if (r.Status != GattCommunicationStatus.Success || r.Characteristics.Count == 0) throw new InvalidOperationException("Characteristic: " + r.Status);
                var c = r.Characteristics[0];
                c.ProtectionLevel = GattProtectionLevel.EncryptionAndAuthenticationRequired;
                return c;
            }
            var data = await Bind(DataId); var control = await Bind(ControlId); var notifications = await Bind(NotificationId);
            var events = Channel.CreateBounded<byte[]>(1024);
            var history = Channel.CreateBounded<byte[]>(2048);
            using var eventSignal = new SemaphoreSlim(0);
            var chunks = Channel.CreateBounded<byte[]>(1024);
            int dropped = 0, skippedHistory = 0;
            bool includeHistory = Environment.GetEnvironmentVariable("ANCS_PROBE_HISTORY") == "1";
            void OnData(GattCharacteristic _, GattValueChangedEventArgs e) { if (!chunks.Writer.TryWrite(Bytes(e.CharacteristicValue))) Interlocked.Increment(ref dropped); }
            void OnNotification(GattCharacteristic _, GattValueChangedEventArgs e)
            {
                var bytes = Bytes(e.CharacteristicValue);
                if (bytes.Length == 8 && (bytes[1] & 4) != 0 && !includeHistory) { Interlocked.Increment(ref skippedHistory); return; }
                var queue = bytes.Length == 8 && (bytes[1] & 4) != 0 ? history : events;
                if (!queue.Writer.TryWrite(bytes)) Interlocked.Increment(ref dropped);
                else eventSignal.Release();
            }
            data.ValueChanged += OnData; notifications.ValueChanged += OnNotification;
            try
            {
                foreach (var c in new[] { data, notifications })
                {
                    var r = await c.WriteClientCharacteristicConfigurationDescriptorWithResultAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask(ct);
                    if (r.Status != GattCommunicationStatus.Success) throw new InvalidOperationException($"Subscribe: {r.Status}, protocol={r.ProtocolError}");
                }
                emit(new { kind = "status", stage = "ancs-ready", seconds });
                if (seconds == 0) deadline.CancelAfter(Timeout.InfiniteTimeSpan);
                using var heartbeat = new Timer(_ => emit(new { kind = "heartbeat", connected = device.ConnectionStatus == BluetoothConnectionStatus.Connected, dropped }), null, 0, 5000);
                using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
                if (seconds > 0) window.CancelAfter(TimeSpan.FromSeconds(seconds));
                void Disconnected(BluetoothLEDevice d, object _) { if (d.ConnectionStatus == BluetoothConnectionStatus.Disconnected) deadline.Cancel(); }
                void ServicesChanged(BluetoothLEDevice d, object _) { deadline.Cancel(); }
                device.ConnectionStatusChanged += Disconnected;
                device.GattServicesChanged += ServicesChanged;
                int count = 0, sms = 0;
                try
                {
                    while (true)
                    {
                        await eventSignal.WaitAsync(window.Token);
                        if (!events.Reader.TryRead(out var ev) && !history.Reader.TryRead(out ev)) continue;
                        if (Volatile.Read(ref dropped) != 0) throw new IOException("ANCS queue overflow; reconnect for recovery.");
                        if (ev.Length != 8) throw new InvalidDataException("ANCS event length");
                        uint uid = BinaryPrimitives.ReadUInt32LittleEndian(ev.AsSpan(4));
                        emit(new { kind = "ancs-event", uid, eventId = ev[0], flags = ev[1], category = ev[2] });
                        if (ev[0] == 2) continue;
                        // Read-only GetNotificationAttributes; no notification action commands.
                        byte[] request = new byte[] { 0, 0, 0, 0, 0, 0, 1, 0, 1, 3, 0, 8, 4, 5 };
                        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(1, 4), uid);
                        var result = await control.WriteValueWithResultAsync(request.AsBuffer(), GattWriteOption.WriteWithResponse).AsTask(window.Token);
                        if (result.Status != GattCommunicationStatus.Success)
                        {
                            if (result.ProtocolError == 0xA2) continue; // Notification removed before retrieval.
                            throw new IOException($"Attribute request failed: {result.Status}/{result.ProtocolError}");
                        }
                        using var fetch = CancellationTokenSource.CreateLinkedTokenSource(window.Token);
                        fetch.CancelAfter(TimeSpan.FromSeconds(10));
                        var response = new List<byte>();
                        Dictionary<byte, string>? attributes;
                        do
                        {
                            response.AddRange(await chunks.Reader.ReadAsync(fetch.Token));
                            if (response.Count > 16384) throw new InvalidDataException("ANCS response exceeds bound.");
                        } while (!TryParse(response.ToArray(), uid, out attributes));
                        count++;
                        string app = attributes![0];
                        if (app == "com.apple.MobileSMS")
                        {
                            sms++;
                            emit(new { kind = "ancs-message", uid, app, title = attributes[1], body = attributes[3], messageSize = attributes[4], date = attributes[5], preExisting = (ev[1] & 4) != 0 });
                        }
                        else emit(new { kind = "ancs-other", uid, app });
                    }
                }
                catch (OperationCanceledException) when (window.IsCancellationRequested && !ct.IsCancellationRequested) { }
                finally { device.ConnectionStatusChanged -= Disconnected; device.GattServicesChanged -= ServicesChanged; }
                emit(new { kind = "complete", attributesRead = count, sms, dropped, skippedHistory });
                return dropped == 0 ? 0 : 3;
            }
            finally
            {
                data.ValueChanged -= OnData; notifications.ValueChanged -= OnNotification;
                // Dispose our service/session; do not disable another Windows client's CCCD.
                session.MaintainConnection = false;
            }
        }
        catch (Exception e) { emit(new { kind = "error", error = e.GetType().Name, message = e.Message }); return 1; }
    }
    internal static bool TryParse(byte[] data, uint uid, out Dictionary<byte, string>? attributes)
    {
        attributes = null;
        if (data.Length < 5) return false;
        if (data[0] != 0 || BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(1, 4)) != uid)
            throw new InvalidDataException("Unexpected ANCS response; abort to avoid mixing notifications.");
        var values = new Dictionary<byte, string>();
        for (int pos = 5; pos < data.Length;)
        {
            if (data.Length - pos < 3) return false;
            byte id = data[pos]; int length = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos + 1, 2)); pos += 3;
            if (data.Length - pos < length) return false;
            values[id] = Encoding.UTF8.GetString(data, pos, length); pos += length;
            if (new byte[] { 0, 1, 3, 4, 5 }.All(values.ContainsKey)) { attributes = values; return true; }
        }
        return false;
    }
    static void SelfTest()
    {
        using var stream = new MemoryStream();
        stream.Write(new byte[] { 0, 42, 0, 0, 0 });
        foreach (var (id, text) in new (byte, string)[] { (0, "com.apple.MobileSMS"), (1, "测试"), (3, "验证码：123456"), (4, "20"), (5, "20261013T190000") })
        {
            var encoded = Encoding.UTF8.GetBytes(text);
            stream.WriteByte(id); stream.WriteByte((byte)encoded.Length); stream.WriteByte((byte)(encoded.Length >> 8)); stream.Write(encoded);
        }
        var bytes = stream.ToArray();
        for (int i = 0; i < bytes.Length; i++)
            if (TryParse(bytes[..i], 42, out _)) throw new Exception("Incomplete response accepted at " + i);
        if (!TryParse(bytes, 42, out var result) || result![3] != "验证码：123456") throw new Exception("UTF8 body did not round-trip.");
        try { TryParse(bytes, 43, out _); throw new Exception("Wrong UID accepted."); }
        catch (InvalidDataException) { }
        bytes[0] = 1;
        try { TryParse(bytes, 42, out _); throw new Exception("Wrong command accepted."); }
        catch (InvalidDataException) { }
    }
}
