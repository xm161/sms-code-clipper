using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace SmsCodeClipper;

internal sealed class ApiServer(AppState state, IDesktop desktop,
    Func<CancellationToken, Task<JsonArray>>? devices = null) : IAsyncDisposable
{
    private WebApplication? application;
    internal async Task Start()
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ApplicationName = typeof(ApiServer).Assembly.FullName });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => {
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = 65536;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5);
            options.Listen(IPAddress.Loopback, (int)Math.Clamp(Json.Number(state.GetSettings(), "port"), 0, 65535));
        });
        application = builder.Build();
        application.Run(Handle);
        try
        {
            await application.StartAsync(state.Stop.Token);
            state.Port = new Uri(application.Urls.Single()).Port;
            state.Store.Write("runtime.json", new JsonObject { ["host"] = "127.0.0.1", ["port"] = state.Port, ["pid"] = Environment.ProcessId, ["instance_id"] = state.InstanceId });
        }
        catch { await application.DisposeAsync(); application = null; throw; }
    }
    private async Task Reply(HttpContext context, int status, JsonObject result)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsync(result.ToJsonString(Json.Options), context.RequestAborted);
    }
    private async Task Handle(HttpContext context)
    {
        var request = context.Request;
        JsonObject Error(string message) => new() { ["error"] = message };
        try
        {
            var hosts = new[] { "localhost", "127.0.0.1", $"localhost:{state.Port}", $"127.0.0.1:{state.Port}" };
            if (!hosts.Contains(request.Headers.Host.ToString(), StringComparer.OrdinalIgnoreCase)) { await Reply(context, 403, Error("只接受本机 Host")); return; }
            if (request.Headers.ContainsKey("Origin") && !hosts.Select(x => "http://" + x).Contains(request.Headers.Origin.ToString(), StringComparer.OrdinalIgnoreCase))
            { await Reply(context, 403, Error("不允许跨站请求")); return; }
            string path = request.Path.Value ?? "/";
            if (request.Headers["X-Requested-With"] != "clipper") { await Reply(context, 403, Error("missing X-Requested-With: clipper")); return; }
            if (request.Method == "GET")
            {
                var (status, result) = await Get(path, key => request.Query[key].FirstOrDefault(), context.RequestAborted);
                await Reply(context, status, result); return;
            }
            if (request.Method != "POST") { await Reply(context, 404, Error("not found")); return; }
            if (request.Headers.ContainsKey("Transfer-Encoding")) { await Reply(context, 400, Error("不支持该请求传输格式")); return; }
            if (request.ContentLength > 65536) { await Reply(context, 413, Error("请求体超过 64 KiB")); return; }
            JsonObject body = new();
            if (request.ContentLength is > 0)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                try { body = await JsonNode.ParseAsync(request.Body, cancellationToken: timeout.Token) as JsonObject ?? throw new JsonException(); }
                catch (JsonException) { await Reply(context, 400, Error("请求体必须是有效 JSON 对象")); return; }
                catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested) { await Reply(context, 408, Error("读取请求体超时")); return; }
            }
            var (code, response) = await Post(path, body, context.RequestAborted);
            await Reply(context, code, response);
            if (path == "/api/quit" && code == 200) state.Shutdown();
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        catch (BadHttpRequestException e) { if (!context.Response.HasStarted) await Reply(context, e.StatusCode, Error("请求无效")); }
        catch (Exception e) { if (!context.Response.HasStarted) await Reply(context, 500, Error(e.Message)); }
    }
    private Task<JsonArray> Devices(CancellationToken cancellation) => devices?.Invoke(cancellation) ?? AncsSource.Devices(state.Store.Paths, cancellation);
    private static int Int(string? value, int fallback, int min, int max) => int.TryParse(value, out int parsed) ? Math.Clamp(parsed, min, max) : fallback;
    private static bool Seconds(string? value, out double result)
    {
        result = 0;
        return string.IsNullOrEmpty(value) || double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result) && double.IsFinite(result) && result >= 0;
    }
    internal async Task<(int, JsonObject)> Get(string path, Func<string, string?> query, CancellationToken cancellation)
    {
        switch (path)
        {
            case "/api/status": return (200, state.Snapshot());
            case "/api/bluetooth/devices":
                try { return (200, new() { ["items"] = await Devices(cancellation) }); }
                catch (Exception e) { return (503, new() { ["error"] = e.Message }); }
            case "/api/latest":
                if (!Seconds(query("wait"), out double wait) || !Seconds(query("max_age"), out double maxAge))
                    return (400, new() { ["error"] = "wait 和 max_age 必须是非负有限数字" });
                JsonObject? item;
                if (wait > 0) item = await state.WaitNew(Int(query("since"), state.Snapshot()["code_seq"]!.GetValue<int>(), 0, int.MaxValue), Math.Min(wait, 300), cancellation);
                else item = state.Store.Latest(string.IsNullOrEmpty(query("max_age")) ? null : maxAge);
                var result = new JsonObject { ["code"] = item?["code"]?.DeepClone(), ["notification"] = item, ["code_seq"] = state.Snapshot()["code_seq"]!.DeepClone() };
                if (wait > 0) result["timed_out"] = item is null;
                return (200, result);
            case "/api/notifications": return (200, new() { ["items"] = Json.Array(state.Store.Notifications(Int(query("limit"), 50, 1, 500))) });
            case "/api/codes": return (200, new() { ["items"] = Json.Array(state.Store.RecentCodes(Int(query("limit"), 50, 1, 500))) });
            case "/api/log": return (200, new() { ["lines"] = new JsonArray(state.Store.TailLog(Int(query("n"), 200, 1, 2000)).Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()) });
            default: return (404, new() { ["error"] = "not found" });
        }
    }
    private (int, JsonObject) UpdateSettings(JsonObject patch) => state.Update(patch)
        ? (200, state.Snapshot())
        : (500, new JsonObject { ["error"] = "设置未保存，请检查数据目录的写入权限和可用空间。" });
    internal async Task<(int, JsonObject)> Post(string path, JsonObject body, CancellationToken cancellation)
    {
        switch (path)
        {
            case "/api/startup/confirm":
                if (Json.Text(body, "instance_id") != state.InstanceId || state.Stop.IsCancellationRequested) return (409, new() { ["error"] = "后台实例不匹配或正在退出" });
                state.Confirmed.TrySetResult(); break;
            case "/api/bluetooth/connect":
                string device = Json.Text(body, "device_id");
                try { if (!(await Devices(cancellation)).Any(x => Json.Text(x, "id") == device)) return (400, new() { ["error"] = "请先选择已与 Windows 配对的 iPhone" }); }
                catch (Exception e) { return (503, new() { ["error"] = e.Message }); }
                return UpdateSettings(new JsonObject { ["ancs_device"] = device, ["bluetooth_enabled"] = true });
            case "/api/bluetooth/disconnect": return UpdateSettings(new JsonObject { ["bluetooth_enabled"] = false });
            case "/api/settings":
                foreach (var (key, value) in body)
                {
                    bool valid = key switch {
                        "sound" or "autotype" or "paused" or "bluetooth_enabled" => value is JsonValue flag && flag.TryGetValue<bool>(out _),
                        "phone" or "ancs_device" => value is JsonValue text && text.TryGetValue<string>(out _),
                        "autotype_apps" => value is JsonArray apps && apps.All(x => x is JsonValue app && app.TryGetValue<string>(out _)),
                        "port" => value is JsonValue port && port.TryGetValue<int>(out int number) && number is >= 0 and <= 65535,
                        _ => true
                    };
                    if (!valid) return (400, new() { ["error"] = $"{key} 的类型或取值无效" });
                }
                return UpdateSettings(body);
            case "/api/copy":
                string code = Json.Text(body, "code").Trim();
                if (code.Length == 0) return (400, new() { ["error"] = "code 为空" });
                if (!desktop.Copy(code)) return (500, new() { ["error"] = "剪贴板写入失败" });
                return (200, new() { ["ok"] = true, ["code"] = code });
            case "/api/chime": desktop.Chime(); break;
            case "/api/show": desktop.ShowWindow(); break;
            case "/api/quit": return (200, new() { ["ok"] = true, ["message"] = "quitting" });
            default: return (404, new() { ["error"] = "not found" });
        }
        return (200, new() { ["ok"] = true });
    }
    public async ValueTask DisposeAsync()
    {
        if (application is not null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await application.StopAsync(timeout.Token); await application.DisposeAsync(); application = null;
        }
        // Keep discovery readable during shutdown; a stale file is rejected by the client.
        if (state.Store.Read("runtime.json") is JsonObject runtime && Json.Text(runtime, "instance_id") == state.InstanceId)
            state.Store.Write("runtime.json", new JsonObject { ["host"] = "127.0.0.1", ["port"] = 0, ["pid"] = Environment.ProcessId, ["instance_id"] = state.InstanceId });
    }
}
