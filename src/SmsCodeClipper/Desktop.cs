using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.VisualBasic.FileIO;

namespace SmsCodeClipper;

internal sealed class Desktop(AppState state) : IDesktop, IDisposable
{
    private Control? dispatcher;
    private NotifyIcon? tray;
    private HotkeyWindow? hotkey;
    private System.Windows.Forms.Timer? timer;
    private Icon? currentIcon;
    private string iconStatus = "";
    private readonly object soundGate = new();
    internal void Start()
    {
        dispatcher = new Control();
        dispatcher.CreateControl();
        _ = dispatcher.Handle;
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开面板", null, (_, _) => SafeShow());
        void Toggle(string key, bool value)
        {
            if (!state.Update(new JsonObject { [key] = value }))
                tray?.ShowBalloonTip(3000, "设置未保存", "请检查数据目录的写入权限和可用空间。", ToolTipIcon.Warning);
        }
        menu.Items.Add("暂停自动复制", null, (_, _) => Toggle("paused", !Json.Bool(state.Snapshot(), "paused")));
        menu.Items.Add("命中时响铃", null, (_, _) => Toggle("sound", !Json.Bool(state.GetSettings(), "sound", true)));
        menu.Items.Add("自动填入验证码", null, (_, _) => Toggle("autotype", !Json.Bool(state.GetSettings(), "autotype")));
        menu.Items.Add("复制最近验证码（暂无）", null, (_, _) => { string code = Json.Text(state.Snapshot()["last_code"], "code"); if (code.Length > 0) Copy(code); });
        menu.Items.Add("打开日志", null, (_, _) => {
            try { Process.Start(new ProcessStartInfo(state.Store.Paths.File("log.txt")) { UseShellExecute = true }); }
            catch (Exception e) { state.Store.Log("[日志] " + e.Message); }
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => state.Shutdown());
        menu.Opening += (_, _) => Refresh();
        tray = new NotifyIcon { ContextMenuStrip = menu, Text = "SMS Code Clipper" };
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) SafeShow(); };
        Refresh(); tray.Visible = true;
        hotkey = new HotkeyWindow(() => {
            IntPtr target = Input.ForegroundWindow();
            if (target == IntPtr.Zero) return;
            _ = Task.Run(() => {
            string phone = Json.Text(state.GetSettings(), "phone");
            if (phone.Length == 0) { state.Store.Log("[Insert] 未设置手机号"); return; }
            try
            {
                var wait = Stopwatch.StartNew();
                while (Input.ModifiersDown() || Input.KeyDown(0x2D))
                { if (wait.Elapsed.TotalSeconds > 1.5 || state.Stop.IsCancellationRequested) return; Thread.Sleep(20); }
                if (state.Stop.IsCancellationRequested) return;
                Input.TypeText(phone, false, target); state.Store.Log("[Insert] 已输入手机号");
            }
            catch (Exception e) { state.Store.Log("[Insert] " + e.Message); }
            });
        });
        if (!hotkey.Registered) state.Store.Log("[Insert] 热键被其他程序占用，其他功能不受影响");
        state.Changed = QueueRefresh;
        timer = new System.Windows.Forms.Timer { Interval = 1000 };
        timer.Tick += (_, _) => { if (state.Stop.IsCancellationRequested) Application.ExitThread(); };
        timer.Start();
    }
    internal void QueueRefresh()
    {
        try { if (dispatcher is { IsDisposed: false, IsHandleCreated: true }) dispatcher.BeginInvoke((Action)Refresh); }
        catch (InvalidOperationException) { }
    }
    private void SafeShow() { try { ShowWindow(); } catch (Exception e) { state.Store.Log("[窗口] " + e.Message); } }
    private void Refresh()
    {
        if (tray is null) return;
        var snapshot = state.Snapshot();
        var settings = snapshot["settings"];
        bool paused = Json.Bool(snapshot, "paused");
        string status = paused ? "paused" : Json.Text(snapshot, "source_status") == "connected" ? "ok" : "error";
        if (status != iconStatus)
        {
            using var sourceIcon = new Icon(Path.Combine(state.Store.Paths.Assets, "icon.ico"), new Size(64, 64));
            using var source = sourceIcon.ToBitmap();
            using var bitmap = new Bitmap(64, 64);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(source, 0, 0, 64, 64);
                if (status != "ok")
                {
                    using var brush = new SolidBrush(status == "paused" ? Color.FromArgb(140, 140, 140) : Color.FromArgb(210, 70, 60));
                    graphics.FillEllipse(brush, 42, 42, 20, 20);
                    using var pen = new Pen(Color.White, 2); graphics.DrawEllipse(pen, 42, 42, 20, 20);
                }
            }
            IntPtr handle = bitmap.GetHicon();
            Icon next;
            try { using var raw = Icon.FromHandle(handle); next = (Icon)raw.Clone(); }
            finally { DestroyIcon(handle); }
            tray.Icon = next; currentIcon?.Dispose(); currentIcon = next; iconStatus = status;
        }
        string code = Json.Text(snapshot["last_code"], "code");
        string tip = "SMS Code Clipper · " + (paused ? "已暂停" : status == "ok" ? "监听中" : "数据源异常");
        if (code.Length > 0) tip += "\n最近: " + code;
        tray.Text = tip.Length > 63 ? tip[..63] : tip;
        var items = tray.ContextMenuStrip!.Items;
        ((ToolStripMenuItem)items[1]).Checked = paused;
        ((ToolStripMenuItem)items[2]).Checked = Json.Bool(settings, "sound", true);
        ((ToolStripMenuItem)items[3]).Checked = Json.Bool(settings, "autotype");
        items[4].Text = code.Length > 0 ? "复制最近验证码 " + code : "复制最近验证码（暂无）";
    }
    public bool Copy(string text)
    {
        if (dispatcher is not null && dispatcher.InvokeRequired)
        {
            if (state.Stop.IsCancellationRequested) return false;
            try
            {
                return CopyOnDispatcher(action => dispatcher.BeginInvoke(action),
                    () => !state.Stop.IsCancellationRequested && Copy(text), TimeSpan.FromSeconds(2));
            }
            catch (InvalidOperationException) { return false; }
        }
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
        {
            bool result = false;
            var thread = new Thread(() => result = Copy(text)); thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join(); return result;
        }
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try { Clipboard.SetText(text); if (Clipboard.GetText() == text) return true; }
            catch (ExternalException) { }
            Thread.Sleep(100);
        }
        return false;
    }
    internal static bool CopyOnDispatcher(Action<Action> enqueue, Func<bool> copy, TimeSpan queueTimeout)
    {
        var elapsed = Stopwatch.StartNew();
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        enqueue(() => {
            lock (result)
            {
                if (result.Task.IsCompleted) return;
                try { result.SetResult(elapsed.Elapsed < queueTimeout && copy()); }
                catch (Exception e) { result.SetException(e); }
            }
        });
        if (Task.WaitAny([result.Task], queueTimeout) < 0)
        {
            // Cancel a queued write, or wait for a write already in progress to finish.
            lock (result) result.TrySetResult(false);
        }
        return result.Task.GetAwaiter().GetResult();
    }
    public void ShowWindow()
    {
        if (!File.Exists(state.Store.Paths.Window)) throw new FileNotFoundException("小票窗口缺失，请运行 scripts/build.ps1 build");
        AllowSetForegroundWindow(uint.MaxValue);
        var start = new ProcessStartInfo(state.Store.Paths.Window) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--data-dir"); start.ArgumentList.Add(state.Store.Paths.Data);
        start.Environment["SMS_CODE_CLIPPER_DATA_DIR"] = state.Store.Paths.Data;
        Process.Start(start)?.Dispose();
    }
    public void Chime()
    {
        lock (soundGate)
        {
            string path = state.Store.Paths.File("chime.wav");
            try
            {
                if (!File.Exists(path)) File.WriteAllBytes(path, MakeChime());
                if (!PlaySound(path, IntPtr.Zero, 0x20000 | 1 | 2)) System.Media.SystemSounds.Asterisk.Play();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { System.Media.SystemSounds.Asterisk.Play(); }
        }
    }
    internal static byte[] MakeChime()
    {
        const int rate = 44100;
        static double[] Note(double hz, double seconds, double decay) => Enumerable.Range(0, (int)(44100 * seconds)).Select(i => {
            double t = i / 44100.0, envelope = Math.Exp(-t * decay) * (i / (44100 * .008) < 1 ? i / (44100 * .008) : 1);
            return envelope * (Math.Sin(2 * Math.PI * hz * t) + .18 * Math.Sin(4 * Math.PI * hz * t)) / 1.18;
        }).ToArray();
        double[] a = Note(1046.5, .16, 14), b = Note(784, .32, 9);
        int overlap = (int)(rate * .03);
        double[] samples = a.Take(a.Length - overlap).Concat(a.TakeLast(overlap).Zip(b.Take(overlap), (x, y) => x + y)).Concat(b.Skip(overlap)).ToArray();
        using var output = new MemoryStream(); using var writer = new BinaryWriter(output, Encoding.ASCII, true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + samples.Length * 2); writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(rate); writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(samples.Length * 2);
        foreach (double sample in samples) writer.Write((short)(Math.Clamp(sample * .07, -1, 1) * 32767));
        return output.ToArray();
    }
    public void AutoType(string text, string[] apps)
    {
        IntPtr target = Input.ForegroundWindow();
        string exe = Input.ForegroundExe(target);
        if (target == IntPtr.Zero || exe.Length == 0) return;
        if (!apps.Contains("*") && !apps.Contains(exe, StringComparer.OrdinalIgnoreCase)) return;
        var watch = Stopwatch.StartNew();
        while (Input.ModifiersDown()) { if (watch.Elapsed.TotalSeconds > 1.5 || state.Stop.IsCancellationRequested) return; Thread.Sleep(50); }
        if (Input.ForegroundWindow() != target || Input.ForegroundExe(target) != exe || state.Stop.IsCancellationRequested) return;
        Input.TypeText(text, true, target); state.Store.Log("[自动填入] 已键入 → " + exe);
    }
    public void Dispose()
    {
        state.Changed = () => { };
        timer?.Stop(); timer?.Dispose(); hotkey?.Dispose();
        if (tray is not null) { tray.Visible = false; tray.ContextMenuStrip?.Dispose(); tray.Dispose(); }
        currentIcon?.Dispose(); dispatcher?.Dispose();
    }
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint processId);
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] private static extern bool PlaySound(string path, IntPtr module, uint flags);
}

internal sealed class HotkeyWindow : NativeWindow, IDisposable
{
    private readonly Action action;
    internal bool Registered { get; }
    internal HotkeyWindow(Action action)
    {
        this.action = action;
        CreateHandle(new CreateParams { Parent = new IntPtr(-3) });
        Registered = RegisterHotKey(Handle, 1, 0x4000, 0x2D);
    }
    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x0312 && message.WParam == 1) action();
        base.WndProc(ref message);
    }
    public void Dispose() { if (Registered) UnregisterHotKey(Handle, 1); DestroyHandle(); }
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr window, int id);
}

internal static class Input
{
    private static readonly object typing = new();
    internal static IntPtr ForegroundWindow() => GetForegroundWindow();
    internal static bool KeyDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;
    [StructLayout(LayoutKind.Sequential)] internal struct Keyboard { internal ushort Key, Scan; internal uint Flags, Time; internal UIntPtr Extra; }
    [StructLayout(LayoutKind.Explicit, Size = 32)] internal struct InputUnion { [FieldOffset(0)] internal Keyboard Keyboard; }
    [StructLayout(LayoutKind.Sequential)] internal struct InputRecord { internal uint Type; internal InputUnion Union; }
    internal static InputRecord[] KeyPair(char c, bool digits)
    {
        bool physical = digits && c is >= '0' and <= '9';
        var key = new Keyboard { Key = physical ? c : (ushort)0, Scan = physical ? (ushort)MapVirtualKey(c, 0) : c, Flags = physical ? 0u : 4u };
        var up = key; up.Flags |= 2;
        return [new() { Type = 1, Union = new() { Keyboard = key } }, new() { Type = 1, Union = new() { Keyboard = up } }];
    }
    internal static void TypeText(string text, bool digits, IntPtr target)
    {
        lock (typing)
        {
            foreach (char c in text)
            {
                if (target == IntPtr.Zero || GetForegroundWindow() != target) throw new IOException("焦点已切换，取消键入");
                var pair = KeyPair(c, digits);
                if (SendInput(2, pair, Marshal.SizeOf<InputRecord>()) != 2) throw new IOException("键入失败，目标窗口可能需要管理员权限");
                if (digits) Thread.Sleep(30);
            }
        }
    }
    internal static bool ModifiersDown() => new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(x => (GetAsyncKeyState(x) & 0x8000) != 0);
    internal static string ForegroundExe(IntPtr window)
    {
        GetWindowThreadProcessId(window, out uint pid);
        IntPtr process = OpenProcess(0x1000, false, pid);
        if (process == IntPtr.Zero) return "";
        try
        {
            var path = new StringBuilder(1024); uint length = 1024;
            return QueryFullProcessImageName(process, 0, path, ref length) ? Path.GetFileName(path.ToString()).ToLowerInvariant() : "";
        }
        finally { CloseHandle(process); }
    }
    [DllImport("user32.dll", EntryPoint = "MapVirtualKeyW")] private static extern uint MapVirtualKey(uint code, uint type);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, InputRecord[] inputs, int size);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint pid);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref uint size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
