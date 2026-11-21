using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace SmsCodeClipper;

internal interface IDesktop
{
    bool Copy(string text);
    void Chime();
    void AutoType(string text, string[] apps);
    void ShowWindow();
}

internal sealed class CodeMonitor(AppState state, IDesktop desktop)
{
    private string lastCode = "";
    private double lastTime;
    internal void Handle(JsonObject notification)
    {
        string code = Json.Text(notification, "code");
        if (code.Length == 0) { state.Store.Log("未检测到验证码"); return; }
        if (!state.Record(notification)) return;
        state.Changed();
        if (state.Snapshot()["paused"]!.GetValue<bool>())
        {
            state.Store.Log("自动复制已暂停");
            return;
        }
        if (code == lastCode && Store.Now - lastTime <= 60) return;
        if (!desktop.Copy(code))
        { state.Store.Log("剪贴板写入失败，验证码已保留，可在面板点击复制"); return; }
        lastCode = code; lastTime = Store.Now;
        state.Store.Log("验证码已复制到剪贴板");
        var settings = state.GetSettings();
        if (Json.Bool(settings, "sound", true))
            try { desktop.Chime(); } catch (Exception e) { state.Store.Log("[提示音] " + e.Message); }
        if (Json.Bool(settings, "autotype"))
        {
            string[] apps = (settings["autotype_apps"] as JsonArray ?? []).Select(x => x!.ToString()).ToArray();
            _ = Task.Run(() => { try { desktop.AutoType(code, apps); } catch (Exception e) { state.Store.Log("[自动填入] " + e.Message); } });
        }
        state.Changed();
    }
}

internal sealed class AncsSource(AppState state, Action<JsonObject> handler)
{
    internal static ProcessStartInfo ReaderCommand(AppPaths paths, params string[] args)
    {
        var start = new ProcessStartInfo(paths.Reader) {
            WorkingDirectory = paths.Data, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        start.Environment["ANCS_PROBE_HISTORY"] = "1";
        start.Environment["ANCS_PARENT_PID"] = Environment.ProcessId.ToString();
        return start;
    }
    internal static async Task<JsonArray> Devices(AppPaths paths, CancellationToken cancellation = default)
    {
        Directory.CreateDirectory(paths.Data);
        using var process = Process.Start(ReaderCommand(paths, "ancs-devices")) ?? throw new IOException("无法启动蓝牙接收器");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            Task<string> output = process.StandardOutput.ReadToEndAsync(deadline.Token);
            Task<string> errors = process.StandardError.ReadToEndAsync(deadline.Token);
            await process.WaitForExitAsync(deadline.Token);
            string text = await output;
            string stderr = await errors;
            var records = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => JsonNode.Parse(x)).OfType<JsonObject>().ToArray();
            if (process.ExitCode != 0) throw new IOException(records.FirstOrDefault(x => Json.Text(x, "kind") == "error")?["message"]?.ToString() ?? (stderr.Length > 0 ? stderr : "蓝牙设备查询失败"));
            return new JsonArray(records.Where(x => Json.Text(x, "kind") == "ble-device")
                .Select(x => (JsonNode)new JsonObject { ["id"] = Json.Text(x, "id"), ["name"] = Json.Text(x, "name") }).ToArray());
        }
        finally { Kill(process); }
    }
    internal static void Kill(Process process)
    {
        try { if (!process.HasExited) { process.Kill(); process.WaitForExit(5000); } }
        catch (InvalidOperationException) { }
    }
    private void Status(string status, string? error = null, int? pid = null)
    {
        lock (state.Gate)
        {
            if (state.SourceStatus == status && state.SourceError == error && state.SourcePid == pid) return;
            state.SourceStatus = status; state.SourceError = error; state.SourcePid = pid;
            if (status == "connected") state.PollErrors = 0;
            else if (error is not null) state.PollErrors++;
        }
        state.Changed();
    }
    internal bool WaitRetry(double seconds) => WaitHandle.WaitAny([state.Restart.WaitHandle, state.Stop.Token.WaitHandle], TimeSpan.FromSeconds(seconds)) == 0;

    internal async Task Run()
    {
        double delay = 1;
        double started = Store.Now;
        var seen = state.Store.History().Select(x => Json.Text(x, "id")).ToHashSet();
        while (!state.Stop.IsCancellationRequested)
        {
            // Clear before reading settings so a concurrent change cannot be lost.
            state.Restart.Reset();
            var settings = state.GetSettings();
            if (!Json.Bool(settings, "bluetooth_enabled", true) || Json.Text(settings, "ancs_device").Length == 0)
            { delay = 1; Status("disconnected"); WaitRetry(1); continue; }
            Process? process = null;
            Task? outputPump = null, errorPump = null;
            using var pumpStop = CancellationTokenSource.CreateLinkedTokenSource(state.Stop.Token);
            double? connectedAt = null;
            var session = Stopwatch.StartNew();
            string stderr = "";
            bool reconfigured = false;
            try
            {
                string device = Json.Text(settings, "ancs_device");
                if (device.Length == 0) throw new IOException("尚未选择已配对的 iPhone");
                if (!File.Exists(state.Store.Paths.Reader)) throw new FileNotFoundException("ANCS 接收程序缺失，请运行 scripts/build.ps1 build");
                Status("connecting");
                process = Process.Start(ReaderCommand(state.Store.Paths, "ancs", device, "0")) ?? throw new IOException("无法启动接收器");
                var lines = Channel.CreateBounded<string>(4096);
                int overflow = 0;
                async Task Pump(StreamReader reader, bool diagnostic = false)
                {
                    try
                    {
                        while (await reader.ReadLineAsync(pumpStop.Token) is string line)
                        {
                            if (diagnostic) { if (!string.IsNullOrWhiteSpace(line)) stderr = line.Length > 512 ? line[..512] : line; continue; }
                            if (!lines.Writer.TryWrite(line)) Interlocked.Exchange(ref overflow, 1);
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (IOException) { }
                }
                outputPump = Pump(process.StandardOutput);
                errorPump = Pump(process.StandardError, true);
                var activity = Stopwatch.StartNew();
                while (!state.Stop.IsCancellationRequested)
                {
                    if (state.Restart.IsSet) { reconfigured = true; break; }
                    if (Volatile.Read(ref overflow) != 0) throw new IOException("ANCS 输出队列溢出，重新同步");
                    if (!lines.Reader.TryRead(out string? line))
                    {
                        if (process.HasExited && outputPump.IsCompleted && errorPump.IsCompleted) throw new IOException($"ANCS 接收进程退出 ({process.ExitCode}) {stderr}".TrimEnd());
                        if (activity.Elapsed.TotalSeconds > (connectedAt is null ? 60 : 20)) throw new IOException("ANCS 心跳超时");
                        await Task.Delay(100, state.Stop.Token);
                        continue;
                    }
                    var item = JsonNode.Parse(line) as JsonObject ?? throw new IOException("接收器输出格式无效");
                    activity.Restart();
                    string kind = Json.Text(item, "kind");
                    if (kind == "error") throw new IOException(Json.Text(item, "message", "ANCS 错误"));
                    if (kind == "status" && Json.Text(item, "stage") == "ancs-ready")
                    { connectedAt ??= session.Elapsed.TotalSeconds; Status("connected", pid: process.Id); state.Store.Log("[ANCS] 已连接，监听新短信并补收运行期间最近两分钟内的通知"); }
                    if (kind == "heartbeat")
                    {
                        if (!Json.Bool(item, "connected") || Json.Number(item, "dropped") != 0) throw new IOException("蓝牙断开或接收队列丢失，重新同步");
                        lock (state.Gate) state.LastPoll = Store.Now;
                    }
                    var notification = Codes.FromEvent(item, started, Store.Now);
                    if (notification is not null && !seen.Contains(Json.Text(notification, "id")))
                    {
                        state.Store.Append(notification);
                        handler(notification);
                        seen.Add(Json.Text(notification, "id"));
                        if (seen.Count > 2000) seen = state.Store.History().Select(x => Json.Text(x, "id")).Append(Json.Text(notification, "id")).ToHashSet();
                    }
                }
            }
            catch (OperationCanceledException) when (state.Stop.IsCancellationRequested) { }
            catch (Exception e) { Status("reconnecting", e.Message); state.Store.Log("[ANCS] " + e.Message + "，准备重连"); }
            finally
            {
                if (connectedAt is not null && session.Elapsed.TotalSeconds - connectedAt > 30) delay = 1;
                pumpStop.Cancel();
                if (process is not null) { Kill(process); process.Dispose(); }
                if (outputPump is not null) await outputPump;
                if (errorPump is not null) await errorPump;
                lock (state.Gate) state.SourcePid = null;
            }
            if (state.Stop.IsCancellationRequested) break;
            if (reconfigured || WaitRetry(delay)) { delay = 1; continue; }
            delay = Math.Min(delay * 2, 30);
        }
        Status("stopped");
    }
}
