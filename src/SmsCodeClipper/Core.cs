using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SmsCodeClipper;

internal sealed class AppPaths
{
    internal string Data { get; }
    internal string Reader { get; }
    internal string Window { get; }
    internal string Assets { get; }
    internal string? Root { get; }
    internal string File(string name) => Path.Combine(Data, name);

    internal AppPaths(string? data = null, string? baseDirectory = null)
    {
        string location = Path.GetFullPath(baseDirectory ?? AppContext.BaseDirectory);
        for (var directory = new DirectoryInfo(location); directory is not null; directory = directory.Parent)
            if (System.IO.File.Exists(Path.Combine(directory.FullName, "src", "SmsCodeClipper", "SmsCodeClipper.csproj")))
            { Root = directory.FullName; break; }
        Data = Path.GetFullPath(data ?? Environment.GetEnvironmentVariable("SMS_CODE_CLIPPER_DATA_DIR")
            ?? Root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SMSCodeClipper"));
        string binaries = Root is null ? location : Path.Combine(Root, "dist");
        Reader = Path.GetFullPath(Environment.GetEnvironmentVariable("SMS_CODE_CLIPPER_READER") ?? Path.Combine(binaries, "ancs-reader", "AncsReader.exe"));
        Window = Path.GetFullPath(Environment.GetEnvironmentVariable("SMS_CODE_CLIPPER_WINDOW") ?? Path.Combine(binaries, "clipper-window", "ClipperWindow.exe"));
        Assets = Directory.Exists(Path.Combine(location, "assets")) ? Path.Combine(location, "assets")
            : Path.Combine(Root ?? location, "src", "Assets");
    }
}

internal static class Json
{
    internal static readonly JsonSerializerOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    internal static string Text(JsonNode? node, string key, string fallback = "") => node?[key]?.ToString() ?? fallback;
    internal static double Number(JsonNode? node, string key, double fallback = 0) =>
        double.TryParse(node?[key]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value) ? value : fallback;
    internal static bool Bool(JsonNode? node, string key, bool fallback = false) => node?[key] is null ? fallback
        : node[key]!.ToJsonString() is not ("false" or "0" or "\"\"" or "[]" or "{}");
    internal static JsonObject Clone(JsonObject value) => (JsonObject)value.DeepClone();
    internal static JsonArray Array(IEnumerable<JsonObject> values) => new(values.Select(x => x.DeepClone()).ToArray());
}

internal static class Codes
{
    private const string Keywords = @"验证码|校验码|动态码|动态密码|动态口令|安全码|短信码|登录码|驗證碼|\b(?:verification|verify|security|login)\s*code\b|\b(?:passcode|otp|pin)\b";
    // Reject either half of a grouped number, in both keyword orders.
    private const string GroupSeparator = @"[\p{Zs}\t\p{Pd}]+";
    private const string ValidityPeriod = @"[0-9]{1,3}[ \t]*(?:分钟|分鐘|秒|minutes?\b|seconds?\b|mins?\b)";
    private const string Digits = $@"(?<!\d)(?<!\d[.,])(?<!\d{GroupSeparator})([0-9]{{4,8}})(?!\d|[.,]\d|{GroupSeparator}(?!{ValidityPeriod})\d|\s*(?:元|圆|美元|人民币|USD\b|CNY\b))";
    private static readonly Regex After = new($@"(?:{Keywords}|(?:^|\buse\s+|\byour\s+)code\b)[ \t]*(?:is\b|是|为|為)?[ \t:：-]*{Digits}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Before = new($@"{Digits}[ \t]*(?:(?:是|为|為)(?:您|你)?的?[ \t]*(?:{Keywords})|is[ \t]+your[ \t]+(?!(?:order|postal|zip|promo|coupon|booking|tracking)\b)(?:[a-z]+[ \t]+)?(?:{Keywords}|\bcode\b))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    internal static string? Extract(string text)
    {
        foreach (var regex in new[] { After, Before })
        {
            var match = regex.Match(text);
            if (match.Success) return match.Groups[1].Value;
        }
        return null;
    }

    // Hash a UTF-8 JSON array with one space after each comma.
    internal static string Identity(params string[] parts)
    {
        static string Quote(string text)
        {
            var result = new StringBuilder("\"");
            foreach (char c in text)
                result.Append(c switch {
                    '"' => "\\\"", '\\' => "\\\\", '\b' => "\\b", '\f' => "\\f",
                    '\n' => "\\n", '\r' => "\\r", '\t' => "\\t",
                    < ' ' => "\\u" + ((int)c).ToString("x4"), _ => c.ToString()
                });
            return result.Append('"').ToString();
        }
        string identity = "[" + string.Join(", ", parts.Select(Quote)) + "]";
        return "ancs:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    internal static JsonObject? FromEvent(JsonObject item, double started, double now)
    {
        if (Json.Text(item, "kind") != "ancs-message" || Json.Text(item, "app") != "com.apple.MobileSMS") return null;
        if (!DateTime.TryParseExact(Json.Text(item, "date"), "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal, out var date)) return null;
        double ts = new DateTimeOffset(date).ToUnixTimeSeconds();
        if (ts < started - 5 || now - ts > 120 || ts - now > 30) return null;
        if (item["body"] is not JsonValue bodyNode || !bodyNode.TryGetValue<string>(out string? body) || string.IsNullOrWhiteSpace(body)) return null;
        string title = Json.Text(item, "title");
        return new JsonObject {
            ["id"] = Identity(Json.Text(item, "app"), Json.Text(item, "date"), title, body),
            ["time"] = date.ToString("yyyy-MM-dd HH:mm:ss"), ["ts"] = ts, ["kind"] = "sms",
            ["app"] = "短信", ["source"] = "ANCS", ["title"] = title, ["body"] = body, ["code"] = Extract(body),
            ["preExisting"] = Json.Bool(item, "preExisting")
        };
    }
}

internal sealed class Store(AppPaths paths)
{
    internal AppPaths Paths { get; } = paths;
    private readonly object disk = new();
    private readonly object logLock = new();
    private readonly Dictionary<string, (DateTime Stamp, long Length, JsonNode Value)> readCache = new();
    internal const int RetentionDays = 3;
    internal const int MaxLogBytes = 1024 * 1024;
    internal static double Now => DateTimeOffset.Now.ToUnixTimeMilliseconds() / 1000.0;
    internal static JsonObject Defaults() => new() {
        ["phone"] = "", ["sound"] = true, ["port"] = 0, ["ancs_device"] = "", ["bluetooth_enabled"] = true,
        ["autotype"] = false, ["autotype_apps"] = new JsonArray("chrome.exe", "msedge.exe", "firefox.exe", "brave.exe", "opera.exe", "vivaldi.exe")
    };
    internal static JsonArray CleanApps(JsonNode? value) => new((value as JsonArray ?? [])
        .Select(x => Path.GetFileName(x?.ToString().Trim().Replace('/', '\\') ?? "").ToLowerInvariant())
        .Where(x => x.Length > 0).Distinct().Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());

    internal JsonNode? Read(string name)
    {
        lock (disk)
        {
            try
            {
                bool cacheable = name is "history.json" or "notifications.json";
                var file = new FileInfo(Paths.File(name));
                if (cacheable && file.Exists && readCache.TryGetValue(name, out var cached)
                    && cached.Stamp == file.LastWriteTimeUtc && cached.Length == file.Length)
                    return cached.Value.DeepClone();
                using var stream = new FileStream(Paths.File(name), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var value = JsonNode.Parse(stream);
                if (cacheable && value is not null) readCache[name] = (file.LastWriteTimeUtc, file.Length, value.DeepClone());
                return value;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { readCache.Remove(name); return null; }
        }
    }
    internal void Write(string name, JsonNode value)
    {
        lock (disk)
        {
            Directory.CreateDirectory(Paths.Data);
            string temp = Paths.File(Path.ChangeExtension(name, ".tmp"));
            File.WriteAllText(temp, value.ToJsonString(Json.Options), new UTF8Encoding(false));
            File.Move(temp, Paths.File(name), true);
            readCache.Remove(name);
        }
    }
    internal JsonObject LoadSettings()
    {
        var result = Defaults();
        if (Read("settings.json") is JsonObject saved)
            foreach (string key in result.Select(x => x.Key).ToArray())
                if (saved.ContainsKey(key)) result[key] = saved[key]?.DeepClone();
        result["autotype"] = Json.Bool(result, "autotype");
        result["autotype_apps"] = CleanApps(result["autotype_apps"]);
        return result;
    }
    internal List<JsonObject> Notifications(int limit = 50) => (Read("notifications.json") as JsonArray ?? [])
        .OfType<JsonObject>().Where(x => x.ContainsKey("id")).Take(limit).ToList();
    internal List<JsonObject> History() => (Read("history.json") as JsonArray ?? [])
        .OfType<JsonObject>().Where(x => x.ContainsKey("id") && Json.Text(x, "code").Length > 0).ToList();
    internal void Append(JsonObject item, bool history = false)
    {
        lock (disk)
        {
            var existing = history ? History() : Notifications(200);
            string id = Json.Text(item, "id");
            if (Json.Bool(item, "preExisting") && existing.Any(x => Json.Text(x, "id") == id && !Json.Bool(x, "preExisting"))) return;
            var items = existing.Where(x => Json.Text(x, "id") != id).Prepend(item)
                .OrderByDescending(x => Json.Number(x, "ts")).ThenBy(x => Json.Bool(x, "preExisting")).Take(history ? 50 : 200);
            Write(history ? "history.json" : "notifications.json", Json.Array(items));
        }
    }
    internal List<JsonObject> RecentCodes(int limit = 50) => History().Concat(Notifications(200).Where(x => Json.Text(x, "code").Length > 0))
        .GroupBy(x => Json.Text(x, "id")).Select(g => g.LastOrDefault(x => !Json.Bool(x, "preExisting")) ?? g.Last())
        .OrderByDescending(x => Json.Number(x, "ts"))
        .ThenBy(x => Json.Bool(x, "preExisting")).Take(limit).ToList();
    internal JsonObject? Latest(double? maxAge = null)
    {
        var value = RecentCodes(1).FirstOrDefault();
        return value is not null && (maxAge is null || Now - Json.Number(value, "ts") <= maxAge) ? Json.Clone(value) : null;
    }
    internal async Task<JsonObject?> WaitCode(double seconds, CancellationToken cancellation = default)
    {
        var seen = History().Select(x => Json.Text(x, "id")).ToHashSet();
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (timer.Elapsed.TotalSeconds < seconds)
        {
            var next = History().FirstOrDefault();
            if (next is not null && !seen.Contains(Json.Text(next, "id"))) return next;
            double remaining = seconds - timer.Elapsed.TotalSeconds;
            if (remaining > 0) await Task.Delay(TimeSpan.FromSeconds(Math.Min(.5, remaining)), cancellation);
        }
        return null;
    }
    internal void Log(string text)
    {
        text = (text.Length > 4096 ? text[..4096] : text).Replace('\r', ' ').Replace('\n', ' ');
        string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}";
        lock (logLock)
        {
            try { if (Environment.GetEnvironmentVariable("SMS_CODE_CLIPPER_QUIET") != "1") Console.WriteLine(line); } catch (IOException) { }
            try
            {
                Directory.CreateDirectory(Paths.Data);
                File.AppendAllText(Paths.File("log.txt"), line + "\n", new UTF8Encoding(false));
                if (new FileInfo(Paths.File("log.txt")).Length > MaxLogBytes) PruneLog();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
    internal string[] TailLog(int count = 200)
    {
        try { return File.ReadLines(Paths.File("log.txt"), Encoding.UTF8).TakeLast(count).ToArray(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }
    internal void PruneLog()
    {
        lock (logLock)
        {
            try
            {
                if (!File.Exists(Paths.File("log.txt"))) return;
                var kept = new Queue<string>();
                int bytes = 0;
                bool changed = false;
                DateTime cutoff = DateTime.Now.AddDays(-RetentionDays);
                foreach (string line in File.ReadLines(Paths.File("log.txt"), Encoding.UTF8))
                {
                    if (line.Length < 21 || !DateTime.TryParseExact(line.Substring(1, 19), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) || time < cutoff)
                    { changed = true; continue; }
                    string bounded = line.Length > 4120 ? line[..4120] : line;
                    changed |= bounded.Length != line.Length;
                    kept.Enqueue(bounded);
                    bytes += Encoding.UTF8.GetByteCount(bounded) + Environment.NewLine.Length;
                    // Leave room for new lines so a noisy receiver does not rewrite on every message.
                    while (bytes > MaxLogBytes / 2)
                    { bytes -= Encoding.UTF8.GetByteCount(kept.Dequeue()) + Environment.NewLine.Length; changed = true; }
                }
                if (changed) File.WriteAllLines(Paths.File("log.txt"), kept, new UTF8Encoding(false));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
    internal bool Cleanup()
    {
        PruneLog();
        bool recordsChanged = false;
        lock (disk)
        {
            foreach (string name in new[] { "history.json", "notifications.json" })
            {
                if (Read(name) is not JsonArray items) continue;
                var kept = items.OfType<JsonObject>().Where(x => Json.Number(x, "ts") >= Now - RetentionDays * 86400)
                    .OrderByDescending(x => Json.Number(x, "ts")).Take(name == "history.json" ? 50 : 200).ToArray();
                if (kept.Length == items.Count) continue;
                try { Write(name, Json.Array(kept)); recordsChanged = true; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log("[自动清理] " + e.Message); }
            }
        }
        return recordsChanged;
    }
}

internal sealed class AppState(Store store) : IDisposable
{
    internal Store Store { get; } = store;
    internal readonly object Gate = new();
    internal readonly CancellationTokenSource Stop = new();
    internal readonly ManualResetEventSlim Restart = new(false);
    internal readonly TaskCompletionSource Confirmed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal JsonObject Settings = store.LoadSettings();
    internal string InstanceId = Guid.NewGuid().ToString("N");
    internal bool Paused;
    internal int Port, CodeSeq, PollErrors;
    private int historyRevision;
    internal int? OwnerPid, SourcePid;
    internal string SourceStatus = "starting";
    internal string? SourceError;
    internal double LastPoll;
    internal readonly double Started = Store.Now;
    internal JsonObject? LastCode = store.Latest();
    internal Action Changed = () => { };
    private TaskCompletionSource signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal JsonObject GetSettings() { lock (Gate) return Json.Clone(Settings); }
    internal void Cleanup()
    {
        bool recordsChanged = Store.Cleanup();
        lock (Gate)
        {
            if (recordsChanged) historyRevision++;
            if (LastCode is not null && Json.Number(LastCode, "ts") < Store.Now - Store.RetentionDays * 86400) LastCode = null;
        }
        Changed();
    }
    internal async Task Maintain(TimeSpan interval)
    {
        using var timer = new PeriodicTimer(interval);
        try { while (await timer.WaitForNextTickAsync(Stop.Token)) Cleanup(); }
        catch (OperationCanceledException) when (Stop.IsCancellationRequested) { }
    }
    internal void Shutdown() { Stop.Cancel(); Restart.Set(); lock (Gate) signal.TrySetResult(); }
    internal bool Update(JsonObject patch)
    {
        lock (Gate)
        {
            var next = Json.Clone(Settings);
            if (patch.ContainsKey("ancs_device")) next["ancs_device"] = Json.Text(patch, "ancs_device");
            foreach (string key in new[] { "bluetooth_enabled", "sound", "autotype" })
                if (patch.ContainsKey(key)) next[key] = Json.Bool(patch, key);
            if (patch.ContainsKey("phone")) next["phone"] = new string(Json.Text(patch, "phone").Where(c => char.IsDigit(c) || c == '+').Take(20).ToArray());
            if (patch.ContainsKey("autotype_apps")) next["autotype_apps"] = Store.CleanApps(patch["autotype_apps"]);
            if (patch.ContainsKey("port")) next["port"] = (int)Math.Clamp(Json.Number(patch, "port", Json.Number(Settings, "port")), 0, 65535);
            // A pause-only change belongs to the current session. Persist explicit settings
            // even when their values match memory, so a retry can repair a missing file.
            if (patch.Any(field => next.ContainsKey(field.Key)))
            {
                try { Store.Write("settings.json", next); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                { Store.Log("[设置] 写入失败: " + e.Message); return false; }
                Settings = next;
            }
            if (patch.ContainsKey("paused")) Paused = Json.Bool(patch, "paused");
            if (patch.ContainsKey("ancs_device") || patch.ContainsKey("bluetooth_enabled"))
            { SourceError = null; SourceStatus = Json.Bool(Settings, "bluetooth_enabled", true) ? "connecting" : "disconnected"; Restart.Set(); }
        }
        Changed();
        return true;
    }
    internal bool Record(JsonObject item)
    {
        bool current;
        lock (Gate)
        {
            bool replay = Json.Bool(item, "preExisting");
            if (LastCode is not null && Json.Text(item, "id") == Json.Text(LastCode, "id"))
            {
                if (replay) return false;
                if (!Json.Bool(LastCode, "preExisting")) return true;
            }
            double timestamp = Json.Number(item, "ts"), latest = Json.Number(LastCode, "ts");
            current = LastCode is null || timestamp > latest || timestamp == latest &&
                (!replay || Json.Bool(LastCode, "preExisting"));
            if (current)
            {
                LastCode = Json.Clone(item);
                CodeSeq++;
                signal.TrySetResult();
                signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
        try
        {
            Store.Append(item, true);
            lock (Gate) historyRevision++;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Store.Log("[历史] 写入失败: " + e.Message); }
        return current;
    }
    internal async Task<JsonObject?> WaitNew(int since, double seconds, CancellationToken cancellation)
    {
        Task pending;
        lock (Gate) { if (CodeSeq > since) return LastCode?.DeepClone().AsObject(); pending = signal.Task; }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, Stop.Token);
        try { await pending.WaitAsync(TimeSpan.FromSeconds(seconds), linked.Token); }
        catch (TimeoutException) { return null; }
        catch (OperationCanceledException) { return null; }
        lock (Gate) return CodeSeq > since ? LastCode?.DeepClone().AsObject() : null;
    }
    internal JsonObject Snapshot()
    {
        lock (Gate) return new JsonObject {
            ["running"] = !Stop.IsCancellationRequested, ["paused"] = Paused, ["settings"] = Settings.DeepClone(),
            ["port"] = Port, ["pid"] = Environment.ProcessId, ["instance_id"] = InstanceId, ["owner_pid"] = OwnerPid,
            ["uptime_seconds"] = (int)(Store.Now - Started), ["source"] = "ANCS", ["source_status"] = SourceStatus,
            ["source_error"] = SourceError, ["source_pid"] = SourcePid,
            ["last_poll_age"] = LastPoll > 0 ? Math.Round(Store.Now - LastPoll, 1) : null,
            ["poll_errors"] = PollErrors, ["last_code"] = LastCode?.DeepClone(), ["code_seq"] = CodeSeq,
            ["history_revision"] = historyRevision
        };
    }
    public void Dispose() { Stop.Dispose(); Restart.Dispose(); }
}
