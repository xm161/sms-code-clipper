using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClipperWindow;

internal sealed class ApiClient(string dataDirectory) : IDisposable
{
    private readonly HttpClient http = new(new HttpClientHandler { UseProxy = false }) { Timeout = Timeout.InfiniteTimeSpan };
    internal static TimeSpan RequestTimeout(string path) =>
        TimeSpan.FromSeconds(path is "/api/bluetooth/devices" or "/api/bluetooth/connect" ? 30 : 8);
    private Uri? runtimeAddress;
    private DateTime runtimeStamp;
    private long runtimeLength;
    internal static Uri RuntimeAddress(string json)
    {
        var runtime = JsonNode.Parse(json) ?? throw new IOException("运行信息无效");
        int port = runtime["port"]?.GetValue<int>() ?? 0;
        string host = runtime["host"]?.GetValue<string>() ?? "127.0.0.1";
        if (host != "127.0.0.1" || port < 1 || port > 65535) throw new IOException("运行地址不是有效的本机地址");
        return new Uri($"http://127.0.0.1:{port}/");
    }

    internal async Task<JsonObject> Call(string path, JsonObject? body, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(RequestTimeout(path));
        token = deadline.Token;
        var file = new FileInfo(Path.Combine(dataDirectory, "runtime.json"));
        if (runtimeAddress is null || !file.Exists || file.LastWriteTimeUtc != runtimeStamp || file.Length != runtimeLength)
        {
            string runtime = await File.ReadAllTextAsync(file.FullName, token);
            runtimeAddress = RuntimeAddress(runtime);
            runtimeStamp = file.LastWriteTimeUtc; runtimeLength = file.Length;
        }
        var address = new Uri(runtimeAddress, path);
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, address);
        request.Headers.Add("X-Requested-With", "clipper");
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        HttpResponseMessage response;
        try { response = await http.SendAsync(request, token); }
        catch { runtimeAddress = null; throw; }
        using var ownedResponse = response;
        var result = JsonNode.Parse(await response.Content.ReadAsStringAsync(token)) as JsonObject
            ?? throw new IOException("后台返回了无法识别的数据");
        if (!response.IsSuccessStatusCode) throw new IOException(result["error"]?.ToString() ?? "请求未完成");
        return result;
    }
    public void Dispose() => http.Dispose();
}

internal sealed record ConnectionView(string Title, string Description, bool Connected, bool First, bool Error)
{
    private static readonly string[] DisplayFields = ["instance_id", "running", "paused", "settings", "source_status", "source_error", "last_code"];
    internal static bool SameDisplay(JsonObject? previous, JsonObject current) => previous is not null
        && DisplayFields.All(key => JsonNode.DeepEquals(previous[key], current[key]));
    internal static string ReceiptStamp(JsonObject? state)
    {
        if (state?["running"]?.GetValue<bool>() != true) return "未运行";
        if (state["paused"]?.GetValue<bool>() == true) return "已暂停";
        return SignalFor(state) == "SignalRed" ? "未连接" : "";
    }

    internal static string SignalFor(JsonObject? state)
    {
        if (state is null || state["running"]?.GetValue<bool>() != true
            || string.IsNullOrWhiteSpace(state["settings"]?["ancs_device"]?.ToString())
            || state["settings"]?["bluetooth_enabled"]?.GetValue<bool>() == false)
            return "SignalRed";
        return state["source_status"]?.ToString() switch
        {
            "connected" => "SignalGreen",
            "connecting" or "reconnecting" or "starting" => "SignalYellow",
            _ => "SignalRed",
        };
    }

    internal static ConnectionView From(JsonObject? state)
    {
        if (state is null) return new("后台暂不可用", "正在尝试重新连接后台。", false, false, true);
        string device = state["settings"]?["ancs_device"]?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(device)) return new("连接你的 iPhone", "配对后选择手机，开始接收短信验证码。", false, true, false);
        if (state["settings"]?["bluetooth_enabled"]?.GetValue<bool>() == false)
            return new("手机已断开", "你已主动断开连接，可在下方重新连接。", false, false, false);
        if (state["running"]?.GetValue<bool>() == false) return new("接收已停止", "请重新启动后台程序。", false, false, true);
        if (state["source_status"]?.ToString() == "connected") return new("手机已连接", "正在接收短信通知", true, false, false);
        if (!string.IsNullOrEmpty(state["source_error"]?.ToString()))
            return new("连接受阻，正在重试", "请确认双方蓝牙已开启、手机在附近；仍无法连接时，检查手机的通知授权。", false, false, true);
        return new("正在连接手机", "通知连接就绪后将自动开始接收。", false, false, false);
    }
}

internal static class SelfTest
{
    internal static int Run()
    {
        void Check(bool value, [System.Runtime.CompilerServices.CallerLineNumber] int line = 0) { if (!value) throw new InvalidOperationException($"Native window regression failed at line {line}"); }
        Check(ApiClient.RuntimeAddress("{\"port\":12345}").Host == "127.0.0.1");
        Check(ApiClient.RequestTimeout("/api/bluetooth/devices") == TimeSpan.FromSeconds(30));
        Check(ApiClient.RequestTimeout("/api/bluetooth/connect") == TimeSpan.FromSeconds(30));
        Check(ApiClient.RequestTimeout("/api/status") == TimeSpan.FromSeconds(8));
        foreach (string runtime in new[] { "{\"port\":0}", "{\"port\":65536}", "{\"port\":80,\"host\":\"example.com\"}" })
        {
            bool rejected = false;
            try { ApiClient.RuntimeAddress(runtime); } catch (IOException) { rejected = true; }
            Check(rejected);
        }
        var receipt = MainWindow.ReceiptOutline(336, 460);
        Check(receipt.FillContains(new System.Windows.Point(7, 2)));
        Check(!receipt.FillContains(new System.Windows.Point(14, 2)));
        Check(receipt.FillContains(new System.Windows.Point(7, 458)));
        Check(!receipt.FillContains(new System.Windows.Point(14, 458)));
        Check(receipt.FillContains(new System.Windows.Point(1, 200)));
        Check(receipt.FillContains(new System.Windows.Point(335, 200)));
        // A rotated stamp must fit its layout allocation and stay above receipt content.
        var window = new MainWindow(Path.GetTempPath());
        var stamp = (System.Windows.Controls.Border)window.FindName("StateStamp");
        var stampText = (System.Windows.Controls.TextBlock)window.FindName("StateStampText");
        stampText.Text = "已暂停";
        stamp.Visibility = System.Windows.Visibility.Visible;
        var paper = (System.Windows.Controls.Grid)window.Content;
        paper.Measure(new System.Windows.Size(340, 550));
        paper.Arrange(new System.Windows.Rect(0, 0, 340, 550));
        paper.UpdateLayout();
        Check(System.Windows.Controls.Grid.GetRowSpan(stamp) == 2);
        Check(System.Windows.Controls.Panel.GetZIndex(stamp) > 0);
        Check(stamp.ActualHeight > 24);
        Check(System.Windows.Media.VisualTreeHelper.GetClip(stamp) is null);
        window.Close();
        Check(ConnectionView.From(null).Error);
        Check(ConnectionView.SignalFor(null) == "SignalRed");
        Check(ConnectionView.ReceiptStamp(null) == "未运行");
        var state = JsonNode.Parse("{\"running\":true,\"settings\":{},\"source_error\":\"no device\"}")!.AsObject();
        Check(ConnectionView.From(state).First && !ConnectionView.From(state).Error);
        Check(ConnectionView.SignalFor(state) == "SignalRed");
        var same = state.DeepClone().AsObject();
        same["uptime_seconds"] = 100; same["last_poll_age"] = 1;
        Check(ConnectionView.SameDisplay(state, same));
        same["settings"]!["sound"] = true;
        Check(!ConnectionView.SameDisplay(state, same));
        same = state.DeepClone().AsObject(); same["last_code"] = new JsonObject { ["code"] = "012345" };
        Check(!ConnectionView.SameDisplay(state, same));
        state["settings"]!["ancs_device"] = "test";
        Check(ConnectionView.From(state).Error);
        state["settings"]!["bluetooth_enabled"] = false;
        Check(ConnectionView.From(state).Title == "手机已断开");
        Check(ConnectionView.SignalFor(state) == "SignalRed");
        state["settings"]!["bluetooth_enabled"] = true;
        state["source_error"] = null;
        state["source_status"] = "connected";
        Check(ConnectionView.From(state).Connected);
        Check(ConnectionView.SignalFor(state) == "SignalGreen");
        Check(ConnectionView.ReceiptStamp(state) == "");
        state["paused"] = true;
        Check(ConnectionView.ReceiptStamp(state) == "已暂停");
        Check(ConnectionView.SignalFor(state) == "SignalGreen");
        foreach (string stage in new[] { "connecting", "reconnecting", "starting" })
        {
            state["source_status"] = stage;
            Check(ConnectionView.SignalFor(state) == "SignalYellow");
        }
        foreach (string stage in new[] { "disconnected", "stopped", "unknown" })
        {
            state["source_status"] = stage;
            Check(ConnectionView.SignalFor(state) == "SignalRed");
        }
        state["running"] = false;
        state["source_status"] = "connected";
        Check(ConnectionView.SignalFor(state) == "SignalRed");
        return 0;
    }
}
