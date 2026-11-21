using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using SmsCodeClipper;
using BackendProgram = SmsCodeClipper.Program;

internal static class TestProgram
{
    private static int checks;
    private static readonly string Temp = Path.Combine(Path.GetTempPath(), "sms-code-clipper-tests-" + Guid.NewGuid().ToString("N"));
    private static string backend = "";
    private static string Data(string name) { string path = Path.Combine(Temp, name); Directory.CreateDirectory(path); return path; }
    private static void Check(bool condition, string reason, [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
    { if (!condition) throw new Exception($"FAIL line {line}: {reason}"); checks++; }
    private static async Task Until(Func<bool> condition, int milliseconds = 5000)
    {
        var watch = Stopwatch.StartNew();
        while (!condition()) { if (watch.ElapsedMilliseconds > milliseconds) throw new TimeoutException("Test condition not reached"); await Task.Delay(20); }
    }
    private static JsonObject Notice(string id = "a", string code = "012345", double? time = null) => new() {
        ["id"] = id, ["code"] = code, ["ts"] = time ?? Store.Now, ["time"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
        ["title"] = "测试", ["body"] = "验证码：" + code, ["app"] = "短信", ["kind"] = "sms", ["source"] = "ANCS"
    };
    private static JsonObject Event(string date = "20261013T190000") => new() {
        ["kind"] = "ancs-message", ["app"] = "com.apple.MobileSMS", ["uid"] = 1,
        ["date"] = date, ["title"] = "测试", ["body"] = "验证码：012345", ["preExisting"] = false
    };
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.FirstOrDefault() == "--benchmark") { Benchmark(); return 0; }
        if (args.FirstOrDefault() == "--probe-mutex")
        {
            using var mutex = BackendProgram.TryMutex(args[1]);
            Console.WriteLine(mutex is not null ? "owner" : "busy");
            Console.ReadLine(); return 0;
        }
        if (args.FirstOrDefault() is "ancs" or "ancs-devices") return await FakeReader(args);
        backend = args.Length == 2 && args[0] == "--backend" ? Path.GetFullPath(args[1]) : "";
        try
        {
            Extraction(); Identity(); Storage(); await StateAndMonitor(); await ReplayAndPrivacy(); await Http(); await Receiver(); await Mutexes(); LaunchAndInput();
            if (backend.Length > 0) await Lifecycle();
            Console.WriteLine($"PASS: {checks} assertions. Synthetic test data: {Temp}"); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); Console.Error.WriteLine("Test data: " + Temp); return 1; }
    }
    private static void Benchmark()
    {
        var store = new Store(new AppPaths(Data("benchmark")));
        store.Write("history.json", Json.Array(Enumerable.Range(0, 50).Select(i => Notice("h" + i))));
        store.Write("notifications.json", Json.Array(Enumerable.Range(0, 200).Select(i => Notice("n" + i))));
        using var state = new AppState(store);
        for (int i = 0; i < 20; i++) _ = state.Snapshot();
        long before = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < 1000; i++) _ = state.Snapshot();
        Console.WriteLine($"Snapshot x1000: allocated={(GC.GetAllocatedBytesForCurrentThread() - before) / 1024.0:F1} KiB, elapsed={watch.Elapsed.TotalMilliseconds:F1} ms");
        Console.WriteLine("Synthetic test data: " + Temp);
    }
    private static void Extraction()
    {
        (string Text, string? Expected)[] cases = [
            ("【示例服务甲】验证码:100001（10分钟内有效，如非本人操作，请忽略）", "100001"),
            ("【示例服务乙】您的验证码为100002，有效期15分钟，如非本人操作，请忽略本短信。", "100002"),
            ("【示例服务丙】示例平台验证码：100003，请在 5 分钟内完成登录。", "100003"),
            ("【示例服务丁】您正在使用验证码登录/注册，验证码是1004，5分钟内有效。", "1004"),
            ("【示例服务戊】验证码：1005，用于登录，5分钟内有效。若非本人操作，请勿泄露他人。", "1005"),
            ("【示例服务己】您的验证码: 100006，请5分钟内完成验证。", "100006"),
            ("【示例服务庚】验证码:000007（10分钟内有效）", "000007"),
            ("Your verification code is 123456", "123456"),
            ("G-100008 is your Google verification code.", "100008"),
            ("Use code 7391 to sign in.", "7391"),
            ("Your Apple ID code is: 245 118", null),
            ("Your verification code is 1234 5678", null), ("验证码：1234-5678", null),
            ("1234 5678 是您的验证码", null), ("123-45678 is your verification code", null),
            ("验证码：1234\u202f5678", null), ("1234 - 5678 是您的验证码", null),
            ("验证码：1234-5678；验证码：000042", "000042"),
            ("验证码：12345678", "12345678"),
            ("验证码：000042 5分钟内有效", "000042"),
            ("Your verification code is 000042 10 minutes remaining", "000042"),
            ("123456 是您的验证码，请勿泄露", "123456"), ("100009 is your Uber code.", "100009"),
            ("【某平台】验证码已发送至手机00000000000，请查收。", null),
            ("【示例服务辛】您本次操作的动态码为 887766，请勿泄露", "887766"),
            ("【某银行】账户 2026年09月28日18:08:20 支出 11.49元", null),
            ("【示例商城】满10减5元，闪送到家！", null),
            ("Order #123456 has shipped", null), ("Your WeChat verification code: 665544", "665544"),
            ("Example 2026 Annual Report is now available", null),
            ("支出 120000.00元", null), ("订单号 123456，请勿将验证码告知他人", null),
            ("Your order code: 123456", null), ("验证码：120000.00 元", null),
            ("订单号 123456，验证码：078901", "078901"),
            ("订单号 12345678 验证码已核销", null), ("Order #12345678\nVerification code has been sent", null),
            ("订单号 88442211 验证码请求已受理，654321 是您的验证码", "654321"),
            ("您正在支出 ¥12800 验证码请勿告知他人", null), ("Transaction of USD 1500\nVerification code sent", null),
            ("Your order-code: 123456", null), ("Your order  code: 123456", null),
            ("Order confirmation code: 123456", null), ("123456 is your order code", null)
        ];
        foreach (var item in cases) Check(Codes.Extract(item.Text) == item.Expected, item.Text);
    }
    private static void Identity()
    {
        double now = new DateTimeOffset(new DateTime(2026, 10, 13, 19, 0, 0, DateTimeKind.Local)).ToUnixTimeSeconds();
        var item = Event(); var first = Codes.FromEvent(item, now, now)!;
        item["uid"] = 999; item["preExisting"] = true;
        Check(Json.Text(first, "id") == Json.Text(Codes.FromEvent(item, now, now), "id"), "identity ignores UID and replay flag");
        Check(Json.Text(first, "code") == "012345", "leading zero");
        Check(Codes.FromEvent(item, now - 60, now + 60) is not null, "recent replay");
        foreach (var pair in new[] { (now + 10, now + 10), (now - 300, now + 121), (now - 60, now - 31) })
            Check(Codes.FromEvent(item, pair.Item1, pair.Item2) is null, "stale/future filter");
        item["date"] = "invalid"; Check(Codes.FromEvent(item, now, now) is null, "bad date");
        item = Event(); item["app"] = "other"; Check(Codes.FromEvent(item, now, now) is null, "other app");
        item = Event(); item["body"] = ""; Check(Codes.FromEvent(item, now, now) is null, "empty body");
        item = Event(); item["body"] = "验证码：654321";
        Check(Json.Text(Codes.FromEvent(item, now, now), "id") != Json.Text(first, "id"), "edited body not lost");
        Check(Codes.Identity("com.apple.MobileSMS", "20261013T190000", "测试", "验证码：012345") == "ancs:a1d0ff4e39f0a9b44829643458c3181fe2e0ce8c726a270d76e18253e18fbe56", "identity stability");
        Check(Codes.Identity("com.apple.MobileSMS", "20261013T190000", "😀<>&", "引号\"反斜线\\\n制表\t") == "ancs:5d86ea272c35e7aca0dcf7dc9a49668906cae34a5065cec1a4e722a98c574b20", "Unicode identity stability");
    }
    private static void Storage()
    {
        var store = new Store(new AppPaths(Data("store")));
        File.WriteAllText(store.Paths.File("settings.json"), "{\"phone\":\"+00000000000\",\"sound\":false,\"ancs_device\":\"paired\",\"autotype_apps\":[\"C:\\\\Apps\\\\Chrome.EXE\",\"chrome.exe\"]}", new UTF8Encoding(false));
        var settings = store.LoadSettings();
        Check(Json.Text(settings, "phone") == "+00000000000" && !Json.Bool(settings, "sound"), "read saved settings");
        Check(((JsonArray)settings["autotype_apps"]!).Count == 1, "normalize app names");
        store.Append(Notice("old", "111111", 1), true); store.Append(Notice("new", "222222", 2));
        Check(Json.Text(store.Latest(), "code") == "222222", "merge history and cache");
        Check(store.Latest(1) is null, "max age");
        store.Append(Notice("new", "333333", 3));
        Check(store.Notifications().Count == 1 && Json.Text(store.Latest(), "code") == "333333", "replace same id");
        for (int i = 0; i < 60; i++) store.Append(Notice("history-" + i, "123456", i), true);
        Check(store.History().Count == 50, "history cap");
        store.Write("settings.json", settings);
        Check(!File.ReadAllBytes(store.Paths.File("settings.json")).Take(3).SequenceEqual(new byte[] { 239, 187, 191 }), "no BOM");
        File.WriteAllText(store.Paths.File("notifications.json"), "not json");
        Check(store.Notifications().Count == 0, "corrupt cache tolerated");
        string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        File.WriteAllText(store.Paths.File("log.txt"), $"[{DateTime.Now.AddDays(-4):yyyy-MM-dd HH:mm:ss}] old\n[{DateTime.Now.AddDays(-2):yyyy-MM-dd HH:mm:ss}] recent\n[{timestamp}] new\nuntimestamped\n");
        store.PruneLog(); Check(store.TailLog().Length == 2 && store.TailLog().All(x => !x.EndsWith("old")), "three-day log retention");
        foreach (string name in new[] { "history.json", "notifications.json" })
            store.Write(name, new JsonArray(Notice("expired", "111111", Store.Now - 4 * 86400), Notice("retained", "222222", Store.Now - 2 * 86400)));
        string savedSettings = File.ReadAllText(store.Paths.File("settings.json"));
        store.Cleanup();
        Check(store.History().Count == 1 && store.Notifications().Count == 1 && Json.Text(store.Latest(), "id") == "retained", "three-day cleanup covers history and notification cache");
        Check(File.ReadAllText(store.Paths.File("settings.json")) == savedSettings, "cleanup preserves settings exactly");
        var cached = store.Read("history.json")!.AsArray();
        cached[0]!["code"] = "mutated";
        Check(Json.Text(store.History()[0], "code") == "222222", "caller mutations cannot contaminate the read cache");
        File.WriteAllText(store.Paths.File("history.json"), new JsonArray(Notice("external", "654321")).ToJsonString(), new UTF8Encoding(false));
        Check(Json.Text(store.History()[0], "id") == "external", "external file updates invalidate cached history");
        store.Write("history.json", new JsonArray(Notice("internal", "456789")));
        Check(Json.Text(store.History()[0], "id") == "internal", "atomic writes invalidate cached history");
        string logPath = store.Paths.File("log.txt");
        string payload = new string('测', 1000);
        string[] lines = Enumerable.Range(0, 500).Select(i => $"[{timestamp}] {i:D3} {payload}").ToArray();
        var utf8 = new UTF8Encoding(false, true);
        File.WriteAllLines(logPath, lines, utf8);
        store.Log("cap check");
        const int target = 512 * 1024;
        int lineBytes = utf8.GetByteCount(lines[0] + Environment.NewLine);
        long size = new FileInfo(logPath).Length;
        Check(size <= target && size > target - lineBytes, "trimmed log is within one full UTF-8 line of 512 KiB");
        string[] kept = File.ReadAllLines(logPath, utf8);
        Check(kept.Length >= 2 && kept[^1].EndsWith("] cap check", StringComparison.Ordinal) &&
            kept.SkipLast(1).SequenceEqual(lines.TakeLast(kept.Length - 1)), "trim keeps a contiguous suffix of complete numbered lines and the newest entry");
    }
    private static async Task StateAndMonitor()
    {
        Action? queued = null;
        int writes = 0;
        Check(!Desktop.CopyOnDispatcher(a => queued = a, () => { writes++; return true; }, TimeSpan.Zero),
            "queued clipboard request expires");
        queued!();
        Check(writes == 0, "late dispatcher callback cannot overwrite the clipboard");
        Check(Desktop.CopyOnDispatcher(a => a(), () => { writes++; return true; }, TimeSpan.FromSeconds(1)) && writes == 1,
            "dispatched copy returns the actual result");

        var retained = new Store(new AppPaths(Data("history-refresh")));
        retained.Append(Notice("old", "111111", Store.Now - 4 * 86400), true);
        retained.Append(Notice("current", "222222"), true);
        using (var panelState = new AppState(retained))
        {
            var before = panelState.Snapshot();
            panelState.Cleanup();
            var after = panelState.Snapshot();
            Check(Json.Text(after["last_code"], "id") == "current" && retained.RecentCodes().Count == 1 &&
                Json.Number(after, "history_revision") > Json.Number(before, "history_revision"),
                "pruning a secondary receipt invalidates the history shown by the window");
            panelState.Cleanup();
            Check(Json.Number(panelState.Snapshot(), "history_revision") == Json.Number(after, "history_revision"),
                "no redundant history reload after empty cleanup");
        }
        using var state = new AppState(new Store(new AppPaths(Data("state"))));
        state.Update(new JsonObject { ["sound"] = false, ["phone"] = "+86 (123)abc", ["unknown"] = 4 });
        Check(Json.Text(state.GetSettings(), "phone") == "+86123" && !state.GetSettings().ContainsKey("unknown"), "patch filtering");
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(() => state.Update(new JsonObject { ["phone"] = i.ToString() }))));
        Check(state.GetSettings().ToJsonString() == state.Store.Read("settings.json")!.ToJsonString(), "serialized settings persistence");
        state.SourceError = "old"; state.Update(new JsonObject { ["ancs_device"] = "paired" });
        Check(state.Restart.IsSet && state.SourceError is null && state.SourceStatus == "connecting", "reconfigure clears old error");
        state.Update(new JsonObject { ["bluetooth_enabled"] = false });
        Check(state.SourceStatus == "disconnected", "disconnect state");
        var desktop = new FakeDesktop { CopyWorks = false }; var monitor = new CodeMonitor(state, desktop);
        monitor.Handle(Notice());
        Check(state.CodeSeq == 1 && Json.Text(state.LastCode, "code") == "012345", "copy failure preserves receipt without breaking receiver");
        desktop.CopyWorks = true; monitor.Handle(Notice()); monitor.Handle(Notice());
        Check(state.CodeSeq == 1 && desktop.Copies == 2, "successful copies deduplicate");
        var resend = state.WaitNew(state.CodeSeq, 1, CancellationToken.None);
        monitor.Handle(Notice("resent", "012345"));
        Check(Json.Text(await resend, "id") == "resent" && desktop.Copies == 2, "same digits in a new message update history and long poll without repeated actions");
        var waiting = state.WaitNew(state.CodeSeq, 3, CancellationToken.None);
        state.Update(new JsonObject { ["paused"] = true }); monitor.Handle(Notice("b", "654321"));
        Check(Json.Text(await waiting, "code") == "654321" && desktop.Copies == 2, "pause records without copying and wakes long poll");
        Check(await state.WaitNew(state.CodeSeq, .02, CancellationToken.None) is null, "long poll timeout");
        state.Update(new JsonObject { ["paused"] = false, ["sound"] = true, ["autotype"] = true });
        monitor.Handle(Notice("actions", "246802"));
        await Until(() => desktop.Typed == 1);
        Check(desktop.Chimes == 1 && desktop.Copies == 3, "sound and autotype switches invoke actions");
        state.Update(new JsonObject { ["sound"] = false, ["autotype"] = false });
        monitor.Handle(Notice("quiet", "135791"));
        await Task.Delay(100);
        Check(desktop.Chimes == 1 && desktop.Typed == 1 && desktop.Copies == 4, "disabled sound and autotype do not invoke actions");
        state.Restart.Reset(); var source = new AncsSource(state, _ => { });
        var retry = Task.Run(() => source.WaitRetry(30)); state.Restart.Set();
        Check(await retry.WaitAsync(TimeSpan.FromSeconds(1)), "interrupt backoff on reconfigure");
        state.Restart.Reset(); retry = Task.Run(() => source.WaitRetry(30)); state.Shutdown();
        await retry.WaitAsync(TimeSpan.FromSeconds(1)); Check(true, "interrupt backoff on shutdown");
        using var aging = new AppState(new Store(new AppPaths(Data("maintenance"))));
        aging.Record(Notice("expired-memory", "123456", Store.Now - 4 * 86400));
        Task maintenance = aging.Maintain(TimeSpan.FromMilliseconds(20));
        try
        {
            await Until(() => aging.LastCode is null && aging.Store.History().Count == 0);
            Check(aging.Snapshot()["last_code"] is null, "periodic cleanup expires idle history and the in-memory receipt");
        }
        finally { aging.Shutdown(); await maintenance.WaitAsync(TimeSpan.FromSeconds(1)); }
    }
    private static async Task ReplayAndPrivacy()
    {
        var store = new Store(new AppPaths(Data("replay")));
        using var state = new AppState(store);
        state.Update(new JsonObject { ["autotype"] = true });
        var desktop = new FakeDesktop();
        var monitor = new CodeMonitor(state, desktop);
        var now = DateTimeOffset.Now;
        void Receive(string body, int offset, bool replay)
        {
            var item = Event(now.AddSeconds(offset).ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture));
            item["body"] = body;
            item["preExisting"] = replay;
            var notification = Codes.FromEvent(item, now.ToUnixTimeSeconds() - 60, now.ToUnixTimeSeconds())!;
            store.Append(notification);
            monitor.Handle(notification);
        }
        Receive("Order #123456 has shipped", -30, false);
        Receive("支出 120000.00元", -20, false);
        Receive("验证码：1234-5678", -19, false);
        Check(store.Notifications().Count == 3 && store.History().Count == 0 && desktop.Copies == 0 && desktop.Typed == 0 && state.CodeSeq == 0,
            "ordinary SMS and grouped codes stay in notifications without clipboard or autotype actions");
        Receive("验证码：018765", -5, false);
        await Until(() => desktop.Typed == 1);
        var diskWait = store.WaitCode(.65);
        var apiWait = state.WaitNew(state.CodeSeq, .1, CancellationToken.None);
        Receive("验证码：318765", -15, true);
        Receive("验证码：418765", -5, true);
        Receive("验证码：018765", -5, true);
        Check(!Json.Bool(store.Latest(), "preExisting") && !Json.Bool(store.Notifications().First(), "preExisting"),
            "replaying the same event preserves live priority on disk");
        Check(await apiWait is null && await diskWait is null, "backfill does not wake API or CLI waiters");
        Check(store.History().Count == 3 && state.CodeSeq == 1, "backfill is kept in history only");
        Check(Json.Number(state.Snapshot(), "history_revision") == 3, "backfill refreshes window history independently of the current code");
        using (var reopened = new AppState(store))
            Check(Json.Text(state.LastCode, "code") == "018765" && Json.Text(reopened.LastCode, "code") == "018765",
                "live code wins over older and same-second replays in memory and after restart");
        Check(desktop.Copies == 1 && desktop.Chimes == 1 && desktop.Typed == 1, "backfill performs no desktop actions");
        Receive("验证码：518765", -3, true);
        await Until(() => desktop.Typed == 2);
        Check(Json.Text(state.LastCode, "code") == "518765" && desktop.Copies == 2, "newer replay can recover a missed code");
        state.Update(new JsonObject { ["paused"] = true });
        Receive("验证码：618765", -1, false);
        state.Update(new JsonObject { ["paused"] = false });
        Receive("验证码：618765", -1, true);
        string[] expectedActions = ["018765", "518765"];
        Check(desktop.CopiedTexts.SequenceEqual(expectedActions), "clipboard receives exactly the live and newer recovered codes, preserving leading zeros");
        Check(desktop.TypedTexts.SequenceEqual(expectedActions), "autotype receives the same exact codes in order and skips stale or paused replays");
        var log = string.Join("\n", store.TailLog());
        Check(log.Contains("验证码已复制到剪贴板") && log.Contains("自动复制已暂停"), "copy and pause outcomes are logged");
        Check(new[] { "018765", "318765", "418765", "518765", "618765" }.All(code => !log.Contains(code)),
            "diagnostic log contains no received codes");
    }

    private static async Task Http()
    {
        var store = new Store(new AppPaths(Data("http")));
        store.Append(Notice("old"), true);
        using var state = new AppState(store);
        await using var api = new ApiServer(state, new FakeDesktop(), _ => Task.FromResult(new JsonArray(new JsonObject { ["id"] = "paired", ["name"] = "Test" })));
        await api.Start();
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri($"http://127.0.0.1:{state.Port}"), Timeout = TimeSpan.FromSeconds(5) };
        async Task<(int Status, JsonObject Body)> Request(string path, string? body = null, string? header = null, string? value = null, bool marker = true)
        {
            using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, path);
            if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            if (marker) request.Headers.Add("X-Requested-With", "clipper");
            if (header is not null) request.Headers.TryAddWithoutValidation(header, value);
            using var response = await http.SendAsync(request);
            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
            return ((int)response.StatusCode, json);
        }
        foreach (string key in new[] { "wait", "max_age" }) foreach (string value in new[] { "abc", "NaN", "inf", "-1" })
            Check((await Request($"/api/latest?{key}={value}")).Status == 400, "invalid query " + value);
        foreach (string value in new[] { "[]", "\"abc\"", "123", "null", "{" })
            Check((await Request("/api/quit", value)).Status == 400, "invalid body");
        Check((await Request("/api/quit", "{\"padding\":\"" + new string('a', 65536) + "\"}")).Status == 413, "body bound");
        Check((await Request("/api/status", header: "Host", value: "evil.example")).Status == 403, "foreign host");
        Check((await Request("/api/status", header: "Origin", value: "https://evil.example")).Status == 403, "foreign origin");
        Check((await Request("/api/quit", "{}", marker: false)).Status == 403, "missing POST marker");
        Check((await Request("/api/bluetooth/devices", marker: false)).Status == 403, "cross-site GET cannot start a reader");
        Check(!state.Stop.IsCancellationRequested, "invalid requests never quit");
        Check(Json.Text((await Request("/api/latest")).Body, "code") == "012345", "latest cached record can be returned without JSON parent conflicts");
        foreach (string body in new[] { "{\"sound\":\"false\"}", "{\"phone\":{\"x\":123}}", "{\"autotype_apps\":[{}]}", "{\"port\":1.5}", "{\"bluetooth_enabled\":null}" })
            Check((await Request("/api/settings", body)).Status == 400, "malformed setting rejected without mutation");
        Check((await Request("/api/startup/confirm", "{\"instance_id\":\"wrong\"}")).Status == 409, "wrong startup id");
        Check((await Request("/api/startup/confirm", new JsonObject { ["instance_id"] = state.InstanceId }.ToJsonString())).Status == 200 && state.Confirmed.Task.IsCompleted, "startup confirmation");
        foreach (string body in new[] { "{\"paused\":true}", "{\"sound\":false}", "{\"autotype\":true}", "{\"phone\":\"123\"}" })
        {
            var response = await Request("/api/settings", body, "Origin", http.BaseAddress!.ToString().TrimEnd('/'));
            Check(response.Status == 200 && Json.Text(response.Body["last_code"], "id") == "old" && Json.Number(response.Body, "code_seq") == 0, "settings preserve receipt");
        }
        Check((await Request("/api/bluetooth/connect", "{\"device_id\":\"unknown\"}")).Status == 400, "paired device validation");
        Check((await Request("/api/bluetooth/connect", "{\"device_id\":\"paired\"}")).Status == 200, "connect paired device");
        Check((await Request("/api/bluetooth/disconnect", "{}")).Status == 200 && !Json.Bool(state.GetSettings(), "bluetooth_enabled"), "disconnect");
        foreach (string path in new[] { "/", "/index.html", "/unknown" }) Check((await Request(path)).Status == 404, "no browser dashboard");
        var pending = Request("/api/latest?wait=2&since=0"); await Task.Delay(50); state.Record(Notice("live", "654321"));
        Check(Json.Text((await pending).Body, "code") == "654321", "HTTP long poll");
        Directory.CreateDirectory(store.Paths.File("settings.tmp"));
        var saved = state.GetSettings();
        state.Restart.Reset();
        var saveFailure = await Request("/api/settings", "{\"phone\":\"456\",\"paused\":false}");
        Check(saveFailure.Status == 500 && Json.Text(saveFailure.Body, "error").Contains("设置未保存"), "save failure reaches client");
        Check((await Request("/api/bluetooth/connect", "{\"device_id\":\"paired\"}")).Status == 500, "failed connection settings are reported");
        Check((await Request("/api/bluetooth/disconnect", "{}")).Status == 500, "even an unchanged persistent setting reports a failed save");
        Check(JsonNode.DeepEquals(saved, state.GetSettings()) && JsonNode.DeepEquals(saved, store.Read("settings.json")) &&
            state.Paused && !state.Restart.IsSet, "failed write preserves disk, memory and receiver state");
        Check((await Request("/api/quit", "{}")).Status == 200, "quit response delivered");
        await Until(() => state.Stop.IsCancellationRequested);

        using var broken = new AppState(new Store(new AppPaths(Data("broken-runtime"))));
        Directory.CreateDirectory(broken.Store.Paths.File("runtime.json"));
        await using var brokenApi = new ApiServer(broken, new FakeDesktop());
        bool failed = false; try { await brokenApi.Start(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { failed = true; }
        Check(failed, "runtime write failure aborts startup");
        using var socket = new System.Net.Sockets.TcpListener(IPAddress.Loopback, broken.Port);
        socket.Start(); Check(true, "failed startup releases socket"); socket.Stop();
    }
    private static async Task<int> FakeReader(string[] args)
    {
        if (args[0] == "ancs-devices") { Console.WriteLine("{\"kind\":\"ble-device\",\"id\":\"paired\",\"name\":\"Test\"}"); return 0; }
        Console.WriteLine("{\"kind\":\"status\",\"stage\":\"ancs-ready\"}");
        Console.Error.WriteLine("Synthetic receiver diagnostic, not JSON");
        // Each reader starts at the first line, so restarting it replays the inbox.
        // This stays in the test executable; the shipped backend has no injection API.
        string? inbox = Environment.GetEnvironmentVariable("SMS_TEST_INBOX");
        if (!string.IsNullOrEmpty(inbox))
        {
            int consumed = 0;
            while (true)
            {
                try
                {
                    string[] lines = File.Exists(inbox) ? File.ReadAllLines(inbox, Encoding.UTF8) : [];
                    foreach (string line in lines.Skip(consumed)) Console.WriteLine(line);
                    consumed = lines.Length;
                }
                catch (IOException) { }
                Console.WriteLine("{\"kind\":\"heartbeat\",\"connected\":true,\"dropped\":0}");
                await Task.Delay(200);
            }
        }
        string date = Environment.GetEnvironmentVariable("SMS_TEST_DATE") ?? DateTime.Now.ToString("yyyyMMdd'T'HHmmss");
        Console.WriteLine(Event(date).ToJsonString(Json.Options)); Console.WriteLine(Event(date).ToJsonString(Json.Options));
        while (true) { Console.WriteLine("{\"kind\":\"heartbeat\",\"connected\":true,\"dropped\":0}"); await Task.Delay(200); }
    }
    private static async Task Receiver()
    {
        string? oldReader = Environment.GetEnvironmentVariable("SMS_CODE_CLIPPER_READER");
        string? oldInbox = Environment.GetEnvironmentVariable("SMS_TEST_INBOX");
        string? oldDate = Environment.GetEnvironmentVariable("SMS_TEST_DATE");
        Environment.SetEnvironmentVariable("SMS_CODE_CLIPPER_READER", Path.Combine(AppContext.BaseDirectory, "SmsCodeClipper.Tests.exe"));
        try
        {
            using var state = new AppState(new Store(new AppPaths(Data("receiver"))));
            string inbox = state.Store.Paths.File("synthetic-inbox.jsonl");
            Environment.SetEnvironmentVariable("SMS_TEST_INBOX", inbox);
            state.Update(new JsonObject { ["sound"] = false });
            Check((await AncsSource.Devices(state.Store.Paths)).Count == 1, "device subprocess JSONL");
            var desktop = new FakeDesktop(); var monitor = new CodeMonitor(state, desktop);
            var task = Task.Run(() => new AncsSource(state, monitor.Handle).Run());
            try
            {
                await Until(() => state.SourceStatus == "disconnected");
                Check(state.SourcePid is null && state.PollErrors == 0, "unconfigured receiver waits quietly without spawning or reporting failures");
                var first = Event(DateTime.Now.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture));
                File.WriteAllText(inbox, first.ToJsonString(Json.Options) + "\n", new UTF8Encoding(false));
                state.Update(new JsonObject { ["ancs_device"] = "paired" });
                await Until(() => desktop.CopiedTexts.Contains("012345"));
                int firstPid = state.Snapshot()["source_pid"]!.GetValue<int>();
                var next = Event(DateTime.Now.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture));
                next["uid"] = 2;
                next["body"] = "验证码：097531";
                string replayInbox = state.Store.Paths.File("synthetic-replay.jsonl");
                File.WriteAllLines(replayInbox, [first.ToJsonString(Json.Options), next.ToJsonString(Json.Options)], new UTF8Encoding(false));
                Environment.SetEnvironmentVariable("SMS_TEST_INBOX", replayInbox);
                using (var child = Process.GetProcessById(firstPid))
                {
                    child.Kill();
                    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
                await Until(() => desktop.CopiedTexts.Contains("097531"), 10000);
                Check(Json.Number(state.Snapshot(), "code_seq") == 2 && desktop.CopiedTexts.SequenceEqual(["012345", "097531"]),
                    "restarted reader processes replay and new message in order without duplicating the original receipt or copy");
                state.Update(new JsonObject { ["bluetooth_enabled"] = false });
                await Until(() => Json.Text(state.Snapshot(), "source_status") == "disconnected" && state.Snapshot()["source_pid"] is null);
                Check(true, "explicit disconnect stops child");
                state.Update(new JsonObject { ["bluetooth_enabled"] = true });
                await Until(() => Json.Text(state.Snapshot(), "source_status") == "connected");
                Check(true, "reconnect resumes");
            }
            finally { state.Shutdown(); await task.WaitAsync(TimeSpan.FromSeconds(5)); }
            Check(state.SourcePid is null && state.SourceStatus == "stopped", "shutdown leaves no reader");
            Environment.SetEnvironmentVariable("SMS_TEST_INBOX", null);
            Environment.SetEnvironmentVariable("SMS_TEST_DATE", DateTime.Now.ToString("yyyyMMdd'T'HHmmss"));
            using var failedCopyState = new AppState(new Store(new AppPaths(Data("receiver-copy-fail"))));
            failedCopyState.Update(new JsonObject { ["ancs_device"] = "paired", ["sound"] = false });
            var failedCopyMonitor = new CodeMonitor(failedCopyState, new FakeDesktop { CopyWorks = false });
            var failedCopyReceiver = new AncsSource(failedCopyState, failedCopyMonitor.Handle).Run();
            try
            {
                await Until(() => failedCopyState.CodeSeq == 1);
                int? pid = failedCopyState.SourcePid;
                await Task.Delay(500);
                Check(failedCopyState.SourcePid == pid && failedCopyState.SourceStatus == "connected" && failedCopyState.PollErrors == 0,
                    "clipboard failure and plain stderr do not reconnect a healthy reader");
            }
            finally { failedCopyState.Shutdown(); await failedCopyReceiver.WaitAsync(TimeSpan.FromSeconds(5)); }
        }
        finally
        {
            Environment.SetEnvironmentVariable("SMS_CODE_CLIPPER_READER", oldReader);
            Environment.SetEnvironmentVariable("SMS_TEST_INBOX", oldInbox);
            Environment.SetEnvironmentVariable("SMS_TEST_DATE", oldDate);
        }
    }
    private static async Task Mutexes()
    {
        string name = @"Local\SmsCodeClipper.Test." + Guid.NewGuid().ToString("N");
        var processes = new List<Process>();
        try
        {
            for (int i = 0; i < 8; i++)
            {
                var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "SmsCodeClipper.Tests.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true };
                start.ArgumentList.Add("--probe-mutex"); start.ArgumentList.Add(name);
                processes.Add(Process.Start(start)!);
            }
            var output = await Task.WhenAll(processes.Select(x => x.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10))));
            Check(output.Count(x => x == "owner") == 1 && output.Count(x => x == "busy") == 7, "eight simultaneous launches have exactly one owner");
        }
        finally { foreach (var process in processes) { await process.StandardInput.WriteLineAsync("done"); await process.WaitForExitAsync(); process.Dispose(); } }
        using var released = BackendProgram.TryMutex(name); Check(released is not null, "mutex releases after process exit");
    }
    private static void LaunchAndInput()
    {
        string literal = Install.VbsLiteral("C:\\用户\\O'Connor\\短信😀\"x");
        Check(literal.All(c => c < 128) && literal.Contains("ChrW("), "ASCII launcher for Unicode paths");
        Check(Marshal.SizeOf<Input.InputRecord>() == 40, "x64 SendInput layout");
        foreach (IntPtr target in new[] { IntPtr.Zero, new IntPtr(-1) })
        {
            bool cancelled = false;
            try { Input.TypeText("x", false, target); } catch (IOException) { cancelled = true; }
            Check(cancelled, "missing or changed foreground cancels before emitting keys");
        }
        var pair = Input.KeyPair('7', true);
        Check(pair[0].Union.Keyboard.Key == '7' && pair[0].Union.Keyboard.Flags == 0 && pair[1].Union.Keyboard.Flags == 2, "physical digits for segmented OTP boxes");
        pair = Input.KeyPair('+', false);
        Check(pair[0].Union.Keyboard.Key == 0 && pair[0].Union.Keyboard.Scan == '+' && pair[0].Union.Keyboard.Flags == 4, "Unicode phone input");
        byte[] chime = Desktop.MakeChime();
        Check(Encoding.ASCII.GetString(chime, 0, 4) == "RIFF" && chime.Length == 39734, "same chime duration and format");
        foreach (string value in new[] { "NaN", "inf", "-1" })
        {
            bool rejected = false; try { Options.Parse(["--wait", value]); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "CLI finite wait");
        }
        bool conflict = false; try { Options.Parse(["--open", "--stop"]); } catch (ArgumentException) { conflict = true; }
        Check(conflict, "exclusive CLI commands");
        var data = new AppPaths(Data("paths"), AppContext.BaseDirectory);
        Check(Path.IsPathFullyQualified(data.Data) && Path.IsPathFullyQualified(data.Reader) && Path.IsPathFullyQualified(data.Window), "absolute paths");
    }
    private static async Task<(int Code, string Output)> Cli(string data, string mutex, params string[] args)
    {
        var start = new ProcessStartInfo(backend) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetTempPath() };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        start.ArgumentList.Add("--data-dir"); start.ArgumentList.Add(data);
        start.Environment["SMS_CODE_CLIPPER_MUTEX"] = mutex;
        using var process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(35)); }
        finally { if (!process.HasExited) process.Kill(); }
        return (process.ExitCode, await output.WaitAsync(TimeSpan.FromSeconds(5)) + await error.WaitAsync(TimeSpan.FromSeconds(5)));
    }
    private static async Task Lifecycle()
    {
        string data = Data("lifecycle"); string mutex = @"Local\SmsCodeClipper.Lifecycle." + Guid.NewGuid().ToString("N");
        new Store(new AppPaths(data)).Write("settings.json", new JsonObject { ["bluetooth_enabled"] = false });
        // Headless daemon uses the same API, mutex and supervision, without real clipboard/hotkeys.
        var start = new ProcessStartInfo(backend) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string arg in new[] { "--daemon-child", "--headless", "--data-dir", data, "--owner-pid", Environment.ProcessId.ToString() }) start.ArgumentList.Add(arg);
        start.Environment["SMS_CODE_CLIPPER_MUTEX"] = mutex;
        using var daemon = Process.Start(start)!;
        var client = new LocalClient(new Store(new AppPaths(data)));
        try
        {
            await Until(() => File.Exists(Path.Combine(data, "runtime.json")));
            var status = await Cli(data, mutex, "--status", "--json");
            Check(status.Code == 0 && Json.Bool(JsonNode.Parse(status.Output), "running"), "CLI status from unrelated cwd");
            var ensured = await Cli(data, mutex, "--ensure", "--json", "--owner-pid", "1");
            var info = JsonNode.Parse(ensured.Output);
            Check(ensured.Code == 0 && !Json.Bool(info, "started") && Json.Number(info, "owner_pid") == Environment.ProcessId, "ensure reuses without taking ownership");
            Check((await Cli(data, mutex, "--stop")).Code == 0, "CLI graceful stop");
            await daemon.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
            Check(daemon.ExitCode == 0, "daemon exit success");
            Check((await Cli(data, mutex, "--status", "--json")).Code == 1, "stale runtime is not alive");
            var store = new Store(new AppPaths(data)); store.Append(Notice("cli"), true);
            var latest = await Cli(data, mutex, "--latest", "--json");
            Check(latest.Code == 0 && Json.Text(JsonNode.Parse(latest.Output), "id") == "cli", "offline latest");
            var launches = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Cli(data, mutex, "--ensure", "--headless", "--json")));
            Check(launches.All(x => x.Code == 0), "concurrent ensure succeeds without holding CLI output pipes");
            var ids = launches.Select(x => Json.Number(JsonNode.Parse(x.Output), "pid")).Distinct().ToArray();
            Check(ids.Length == 1 && ids[0] > 0, "concurrent ensure has one daemon");
            using (var active = Process.GetProcessById((int)ids[0]))
            {
                var restart = await Cli(data, mutex, "--background", "--headless");
                Check(restart.Code == 0, "explicit background restart");
                await active.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Check(true, "restart waits for old instance to exit");
            }
            await Cli(data, mutex, "--stop");

            var ownerStart = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "SmsCodeClipper.Tests.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true };
            ownerStart.ArgumentList.Add("--probe-mutex"); ownerStart.ArgumentList.Add(mutex + ".Owner");
            using (var owner = Process.Start(ownerStart)!)
            {
                await owner.StandardOutput.ReadLineAsync();
                var owned = await Cli(data, mutex, "--ensure", "--headless", "--json", "--owner-pid", owner.Id.ToString());
                Check(owned.Code == 0, "owner-bound daemon starts");
                using var child = Process.GetProcessById((int)Json.Number(JsonNode.Parse(owned.Output), "pid"));
                await owner.StandardInput.WriteLineAsync("done"); await owner.WaitForExitAsync();
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
                Check(child.ExitCode == 0, "owner exit stops daemon");
            }
            var unconfirmedStart = new ProcessStartInfo(backend) { UseShellExecute = false, CreateNoWindow = true };
            foreach (string arg in new[] { "--daemon-child", "--headless", "--startup-id", "unconfirmed", "--data-dir", data }) unconfirmedStart.ArgumentList.Add(arg);
            unconfirmedStart.Environment["SMS_CODE_CLIPPER_MUTEX"] = mutex;
            using (var unconfirmed = Process.Start(unconfirmedStart)!)
            {
                try { await unconfirmed.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(26)); Check(unconfirmed.ExitCode == 0, "unconfirmed startup self-terminates"); }
                finally { if (!unconfirmed.HasExited) unconfirmed.Kill(); }
            }
        }
        finally { await client.Call("/api/quit", new JsonObject()); if (!daemon.HasExited) { daemon.Kill(); await daemon.WaitForExitAsync(); } }
    }
    private sealed class FakeDesktop : IDesktop
    {
        internal bool CopyWorks = true;
        internal readonly ConcurrentQueue<string> CopiedTexts = new();
        internal readonly ConcurrentQueue<string> TypedTexts = new();
        internal int Copies => CopiedTexts.Count;
        internal int Typed => TypedTexts.Count;
        internal int Chimes;
        public bool Copy(string text) { CopiedTexts.Enqueue(text); return CopyWorks; }
        public void Chime() { Interlocked.Increment(ref Chimes); }
        public void AutoType(string text, string[] apps) { TypedTexts.Enqueue(text); }
        public void ShowWindow() { }
    }
}
