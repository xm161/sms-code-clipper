using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.VisualBasic.FileIO;

namespace SmsCodeClipper;

internal static class Program
{
    internal static string MutexName => Environment.GetEnvironmentVariable("SMS_CODE_CLIPPER_MUTEX") ?? @"Local\SmsCodeClipper.SingleInstance";
    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        try
        {
            var options = Options.Parse(args);
            if (options.Command == "--help") { Console.WriteLine(Options.Help); return 0; }
            var paths = new AppPaths(options.Data);
            Directory.CreateDirectory(paths.Data);
            var store = new Store(paths);
            var client = new LocalClient(store);
            switch (options.Command)
            {
                case "--background":
                    bool started = LaunchBackground(store, options.Owner, true, options.Headless);
                    Console.WriteLine(started ? "后台已启动，打开面板: --open，停止: --stop" : "后台没有启动起来，请查看 log.txt"); return started ? 0 : 1;
                case "--ensure":
                    var status = client.Call("/api/status").GetAwaiter().GetResult();
                    bool launch = status is null;
                    if (launch && !LaunchBackground(store, options.Owner, false, options.Headless)) { Console.WriteLine(options.Json ? "{\"running\":false}" : "后台启动失败"); return 1; }
                    status ??= client.Call("/api/status").GetAwaiter().GetResult();
                    if (status is null) return 1;
                    var info = new JsonObject { ["running"] = true, ["started"] = launch, ["pid"] = status["pid"]?.DeepClone(), ["owner_pid"] = status["owner_pid"]?.DeepClone() };
                    Console.WriteLine(options.Json ? info.ToJsonString(Json.Options) : $"{(launch ? "已启动" : "已在运行")}（pid {info["pid"]}）"); return 0;
                case "--stop":
                    if (client.Call("/api/status").GetAwaiter().GetResult() is null) { Console.WriteLine("后台实例未运行"); return 0; }
                    client.Call("/api/quit", new JsonObject()).GetAwaiter().GetResult();
                    bool gone = WaitGone(client, 5); Console.WriteLine(gone ? "已停止" : "请求已发出但 5 秒内仍在运行"); return gone ? 0 : 1;
                case "--status":
                    var current = client.Call("/api/status").GetAwaiter().GetResult();
                    Console.WriteLine(current?.ToJsonString(Json.Options) ?? (options.Json ? "{\"running\":false}" : "后台实例未运行")); return current is null ? 1 : 0;
                case "--open":
                    if (client.Call("/api/status").GetAwaiter().GetResult() is null && !LaunchBackground(store, null, false)) return 1;
                    using (var state = new AppState(store)) using (var desktop = new Desktop(state)) desktop.ShowWindow(); return 0;
                case "--window":
                    using (var state = new AppState(store)) using (var desktop = new Desktop(state)) desktop.ShowWindow(); return 0;
                case "--latest": return PrintNotification(store.Latest(options.MaxAge), options.Json);
                case "--wait": return PrintNotification(store.WaitCode(options.Wait).GetAwaiter().GetResult(), options.Json);
                case "--dump":
                    var items = store.Notifications(options.Limit);
                    if (options.Json) Console.WriteLine(Json.Array(items).ToJsonString(Json.Options));
                    else foreach (var item in items) Console.WriteLine($"{item["time"]} [{item["app"]}] {item["title"]} | {item["body"]}");
                    return 0;
                case "--autostart": Install.Autostart(store, options.Autostart); return 0;
                case "--shortcut": Install.Shortcut(store); return 0;
                case "--chime":
                    using (var state = new AppState(store)) using (var desktop = new Desktop(state)) { desktop.Chime(); Thread.Sleep(600); } return 0;
                default: return RunDaemon(store, options.Owner, options.Replace || options.Command == "", options.StartupId, options.Command == "", options.Headless);
            }
        }
        catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }
    }
    internal static Mutex? TryMutex(string name)
    {
        var mutex = new Mutex(false, name, out bool created);
        if (created) return mutex;
        mutex.Dispose(); return null;
    }
    internal static ProcessStartInfo SelfCommand(AppPaths paths, params string[] args)
    {
        string exe = Environment.ProcessPath ?? throw new IOException("无法确定当前程序路径");
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = paths.Data };
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(Program).Assembly.Location);
        foreach (string arg in args) start.ArgumentList.Add(arg);
        start.Environment["SMS_CODE_CLIPPER_DATA_DIR"] = paths.Data;
        start.Environment["SMS_CODE_CLIPPER_READER"] = paths.Reader;
        start.Environment["SMS_CODE_CLIPPER_WINDOW"] = paths.Window;
        return start;
    }
    internal static bool LaunchBackground(Store store, int? owner, bool replace, bool headless = false)
    {
        Mutex? startup = null;
        for (int attempt = 0; attempt < 150 && startup is null; attempt++)
        { startup = TryMutex(MutexName + ".Startup"); if (startup is null) Thread.Sleep(200); }
        if (startup is null) return false;
        using (startup)
        {
            var client = new LocalClient(store);
            if (!replace && client.Call("/api/status").GetAwaiter().GetResult() is not null) return true;
            string id = Guid.NewGuid().ToString("N");
            var start = SelfCommand(store.Paths, "--daemon-child", "--startup-id", id);
            if (replace) start.ArgumentList.Add("--replace-existing");
            if (owner is not null) { start.ArgumentList.Add("--owner-pid"); start.ArgumentList.Add(owner.Value.ToString()); }
            if (headless) start.ArgumentList.Add("--headless");
            // Background children must not keep a caller's redirected CLI pipes open.
            start.Environment["SMS_CODE_CLIPPER_QUIET"] = "1";
            using var process = DetachedProcess.Start(start);
            for (int attempt = 0; attempt < 100; attempt++)
            {
                Thread.Sleep(200);
                var result = client.Call("/api/status", timeout: .5).GetAwaiter().GetResult();
                if (result is not null && Json.Bool(result, "running") && Json.Text(result, "instance_id") == id)
                    return Json.Bool(client.Call("/api/startup/confirm", new JsonObject { ["instance_id"] = id }).GetAwaiter().GetResult(), "ok");
                if (process.HasExited) return !replace && client.Call("/api/status").GetAwaiter().GetResult() is not null;
            }
            return false;
        }
    }
    internal static bool WaitGone(LocalClient client, double seconds)
    {
        var timer = Stopwatch.StartNew();
        do { if (client.Call("/api/status", timeout: .5).GetAwaiter().GetResult() is null) return true; Thread.Sleep(100); } while (timer.Elapsed.TotalSeconds < seconds);
        return false;
    }
    internal static int RunDaemon(Store store, int? owner, bool replace, string startupId, bool showWindow, bool headless)
    {
        using var state = new AppState(store);
        state.OwnerPid = owner;
        Process? ownerProcess = null;
        if (owner is not null)
        {
            try { ownerProcess = Process.GetProcessById(owner.Value); if (ownerProcess.HasExited) { ownerProcess.Dispose(); return 1; } }
            catch (ArgumentException) { return 1; }
        }
        using (ownerProcess)
        {
            Mutex? mutex = TryMutex(MutexName);
            if (mutex is null && replace)
            {
                new LocalClient(store).Call("/api/quit", new JsonObject()).GetAwaiter().GetResult();
                for (int attempt = 0; attempt < 40 && mutex is null; attempt++) { Thread.Sleep(200); mutex = TryMutex(MutexName); }
                if (mutex is null) throw new IOException("旧后台未释放单实例锁，已取消启动");
            }
            if (mutex is null) return 0;
            using (mutex)
            {
                if (startupId.Length > 0) state.InstanceId = startupId;
                using var desktop = new Desktop(state);
                var api = new ApiServer(state, desktop);
                Task? receiver = null, confirmation = null, ownerWatch = null, maintenance = null;
                ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; state.Shutdown(); };
                Console.CancelKeyPress += cancel;
                try
                {
                    api.Start().GetAwaiter().GetResult();
                    if (ownerProcess is not null)
                    {
                        ownerWatch = Task.Run(async () => {
                            try { await ownerProcess.WaitForExitAsync(state.Stop.Token); state.Shutdown(); }
                            catch (OperationCanceledException) { }
                            catch (System.ComponentModel.Win32Exception e) { store.Log("[所属] 无权监视进程: " + e.Message); }
                        });
                    }
                    if (startupId.Length > 0)
                        confirmation = Task.Run(async () => {
                            try { await state.Confirmed.Task.WaitAsync(TimeSpan.FromSeconds(20), state.Stop.Token); }
                            catch (TimeoutException) { store.Log("[启动] 客户端未确认，退出后台"); state.Shutdown(); }
                            catch (OperationCanceledException) { }
                        });
                    state.Cleanup(); store.Log("===== 启动 SMS Code Clipper =====");
                    maintenance = state.Maintain(TimeSpan.FromHours(1));
                    if (!headless)
                    {
                        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); Application.EnableVisualStyles();
                        desktop.Start();
                    }
                    var monitor = new CodeMonitor(state, desktop);
                    receiver = Task.Run(() => new AncsSource(state, monitor.Handle).Run());
                    if (showWindow) desktop.ShowWindow();
                    if (headless) state.Stop.Token.WaitHandle.WaitOne(); else Application.Run();
                    return 0;
                }
                finally
                {
                    state.Shutdown();
                    // Finish pending STA clipboard work before waiting for the worker to stop.
                    if (!headless && receiver is not null)
                        while (!receiver.IsCompleted) { Application.DoEvents(); Thread.Sleep(10); }
                    receiver?.GetAwaiter().GetResult();
                    confirmation?.GetAwaiter().GetResult(); ownerWatch?.GetAwaiter().GetResult();
                    maintenance?.GetAwaiter().GetResult();
                    api.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    Console.CancelKeyPress -= cancel;
                    store.Log("===== 退出 SMS Code Clipper =====");
                }
            }
        }
    }
    private static int PrintNotification(JsonObject? item, bool json)
    {
        if (item is null) { Console.WriteLine(json ? "null" : "（没有验证码）"); return 1; }
        Console.WriteLine(json ? item.ToJsonString(Json.Options) : $"{item["code"]}\t{item["time"]}\t{item["app"]} | {item["title"]} | {item["body"]}"); return 0;
    }
}

internal static class DetachedProcess
{
    // Do not inherit any handles: even redirected .NET launches can inherit the
    // caller's other pipe handles, preventing a CLI consumer from seeing EOF.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        internal int Size; internal string? Reserved, Desktop, Title;
        internal uint X, Y, XSize, YSize, XChars, YChars, Fill, Flags;
        internal ushort Show, ReservedSize; internal IntPtr ReservedPointer, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo { internal IntPtr Process, Thread; internal uint Id, ThreadId; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(string application, StringBuilder command, IntPtr processAttributes,
        IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInfo information);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    internal static string Quote(string text)
    {
        var output = new StringBuilder("\""); int slashes = 0;
        foreach (char c in text)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') output.Append('\\', slashes * 2 + 1).Append(c);
            else output.Append('\\', slashes).Append(c);
            slashes = 0;
        }
        return output.Append('\\', slashes * 2).Append('"').ToString();
    }
    internal static Process Start(ProcessStartInfo start)
    {
        var command = new StringBuilder(string.Join(" ", new[] { start.FileName }.Concat(start.ArgumentList).Select(Quote)));
        string environment = string.Join("\0", start.Environment.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(x => x.Key + "=" + x.Value)) + "\0\0";
        IntPtr block = Marshal.StringToHGlobalUni(environment);
        try
        {
            var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Flags = 0x100 };
            if (!CreateProcessW(start.FileName, command, IntPtr.Zero, IntPtr.Zero, false, 0x08000000 | 0x00000400 | 0x00000200, block, start.WorkingDirectory, ref startup, out var information))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try { return Process.GetProcessById((int)information.Id); }
            finally { CloseHandle(information.Process); CloseHandle(information.Thread); }
        }
        finally { Marshal.FreeHGlobal(block); }
    }
}

internal sealed class LocalClient(Store store)
{
    internal async Task<JsonObject?> Call(string path, JsonObject? body = null, double timeout = 3)
    {
        var runtime = store.Read("runtime.json");
        int port = (int)Json.Number(runtime, "port");
        if (port is < 1 or > 65535 || Json.Text(runtime, "host", "127.0.0.1") != "127.0.0.1") return null;
        try
        {
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(timeout) };
            using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, $"http://127.0.0.1:{port}{path}");
            request.Headers.Add("X-Requested-With", "clipper");
            if (body is not null) request.Content = new StringContent(body.ToJsonString(Json.Options), Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request);
            if (!response.IsSuccessStatusCode) return null;
            return JsonNode.Parse(await response.Content.ReadAsStringAsync()) as JsonObject;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or IOException) { return null; }
    }
}

internal sealed class Options
{
    internal string Command = "", StartupId = "";
    internal bool Json, Replace, Headless, Autostart;
    internal int? Owner;
    internal string? Data;
    internal double Wait;
    internal double? MaxAge;
    internal int Limit = 20;
    internal const string Help = "SMS Code Clipper\n--open  打开面板\n--background  重启后台\n--ensure [--owner-pid PID]  按需启动后台\n--stop  停止后台\n--status [--json]  状态\n--latest [--max-age SEC] [--json]  最近验证码\n--wait SEC [--json]  等待新验证码\n--dump [--limit N] [--json]  通知记录\n--autostart on|off  登录自启\n--shortcut  桌面快捷方式\n--chime  试听提示音\n--data-dir PATH  数据目录";
    internal static Options Parse(string[] args)
    {
        var result = new Options();
        string[] commands = ["--open", "--background", "--ensure", "--daemon-child", "--window", "--stop", "--status", "--latest", "--wait", "--dump", "--autostart", "--shortcut", "--chime", "--help"];
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i] == "-h" ? "--help" : args[i];
            string Value() => ++i < args.Length ? args[i] : throw new ArgumentException(arg + " 缺少参数");
            if (commands.Contains(arg))
            {
                if (result.Command.Length > 0) throw new ArgumentException("一次只能指定一个操作");
                result.Command = arg;
            }
            switch (arg)
            {
                case "--wait": result.Wait = ParseSeconds(Value()); break;
                case "--max-age": result.MaxAge = ParseSeconds(Value()); break;
                case "--limit": result.Limit = Math.Clamp(int.Parse(Value()), 1, 500); break;
                case "--owner-pid": result.Owner = int.Parse(Value()); if (result.Owner <= 0) throw new ArgumentException("PID 必须为正整数"); break;
                case "--data-dir": result.Data = Path.GetFullPath(Value()); break;
                case "--startup-id": result.StartupId = Value(); break;
                case "--autostart": string value = Value(); if (value is not ("on" or "off")) throw new ArgumentException("自启参数必须为 on 或 off"); result.Autostart = value == "on"; break;
                case "--json": result.Json = true; break;
                case "--replace-existing": result.Replace = true; break;
                case "--headless": result.Headless = true; break;
                default: if (!commands.Contains(arg)) throw new ArgumentException("未知参数: " + arg); break;
            }
        }
        return result;
    }
    private static double ParseSeconds(string text) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value) && value >= 0
        ? value : throw new ArgumentException("秒数必须为非负有限数字");
}

internal static class Install
{
    internal static string VbsLiteral(string value)
    {
        var parts = new List<string>(); var text = new StringBuilder();
        void Flush() { if (text.Length > 0) { parts.Add("\"" + text.ToString().Replace("\"", "\"\"") + "\""); text.Clear(); } }
        foreach (char c in value)
            if (c is >= ' ' and < (char)127) text.Append(c);
            else { Flush(); parts.Add($"ChrW({(short)c})"); }
        Flush(); return parts.Count == 0 ? "\"\"" : string.Join(" & ", parts);
    }
    internal static string Launcher(Store store, string command)
    {
        string exe = Environment.ProcessPath ?? throw new IOException("无法确定程序路径");
        string invocation = $"\"{exe}\" {command} --data-dir \"{store.Paths.Data}\"";
        return "Set sh = CreateObject(\"WScript.Shell\")\n" + $"sh.CurrentDirectory = {VbsLiteral(store.Paths.Data)}\nsh.Run {VbsLiteral(invocation)}, 0, False\n";
    }
    internal static void Autostart(Store store, bool enabled)
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "SmsCodeClipper.vbs");
        if (enabled) File.WriteAllText(path, Launcher(store, "--daemon-child"), new UTF8Encoding(false));
        else if (File.Exists(path)) FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        Console.WriteLine(enabled ? "已开启登录自启" : "已关闭登录自启");
    }
    internal static void Shortcut(Store store)
    {
        string launcher = store.Paths.File("launch.vbs");
        File.WriteAllText(launcher, Launcher(store, "--open"), new UTF8Encoding(false));
        Type type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new IOException("Windows Script Host 不可用");
        dynamic shell = Activator.CreateInstance(type)!;
        dynamic shortcut = shell.CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "SMS Code Clipper.lnk"));
        try
        {
            shortcut.TargetPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "wscript.exe");
            shortcut.Arguments = "\"" + launcher + "\""; shortcut.WorkingDirectory = store.Paths.Data;
            shortcut.IconLocation = Path.Combine(store.Paths.Assets, "icon.ico"); shortcut.Description = "SMS Code Clipper"; shortcut.Save();
        }
        finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut); System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
        Console.WriteLine("已创建桌面快捷方式");
    }
}
