using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ClipperWindow;

public partial class MainWindow : Window
{
    private readonly ApiClient api;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim requests = new(1, 1);
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private JsonObject? state;
    private string? latestCode;
    private string? latestPresentation;
    private string? historyRevision;
    private bool closed, busy, devicesLoaded, phoneDirty;
    private int missedStatus;
    private DateTime feedbackUntil;
    private DateTime renderedDate;
    private List<Device> deviceList = [];
    private Device? selectedDevice;
    private int devicePage;

    public MainWindow(string dataDirectory)
    {
        api = new ApiClient(dataDirectory);
        InitializeComponent();
        var opened = DateTime.Now;
        OpenedDate.Text = opened.ToString("yyyy-MM-dd");
        OpenedTime.Text = "OPEN " + opened.ToString("HH:mm:ss");
        MaxHeight = Math.Max(300, SystemParameters.WorkArea.Height * 0.85);
        ApplyTheme();
        SystemEvents.UserPreferenceChanged += PreferencesChanged;
        Phone.TextChanged += (_, _) => { if (Phone.IsKeyboardFocusWithin) phoneDirty = true; };
        timer.Tick += async (_, _) => await RefreshStatus();
        StateChanged += async (_, _) =>
        {
            timer.Interval = TimeSpan.FromSeconds(WindowState == WindowState.Minimized ? 10 : 2);
            if (IsLoaded && WindowState != WindowState.Minimized && !closed) await RefreshStatus();
        };
        Loaded += async (_, _) => { timer.Start(); await RefreshStatus(); };
        Closed += (_, _) =>
        {
            closed = true;
            timer.Stop();
            SystemEvents.UserPreferenceChanged -= PreferencesChanged;
            lifetime.Cancel();
            api.Dispose();
        };
    }

    internal static Geometry ReceiptOutline(double width, double height)
    {
        const double toothDepth = 7;
        int count = Math.Max(1, (int)Math.Round(width / 14));
        double pitch = width / count;
        var outline = new StreamGeometry();
        using (var path = outline.Open())
        {
            path.BeginFigure(new Point(0, toothDepth), true, true);
            for (int i = 0; i < count; i++)
            {
                path.LineTo(new Point((i + 0.5) * pitch, 0), true, false);
                path.LineTo(new Point((i + 1) * pitch, toothDepth), true, false);
            }
            path.LineTo(new Point(width, height - toothDepth), true, false);
            for (int i = count - 1; i >= 0; i--)
            {
                path.LineTo(new Point((i + 0.5) * pitch, height), true, false);
                path.LineTo(new Point(i * pitch, height - toothDepth), true, false);
            }
        }
        outline.Freeze();
        return outline;
    }

    private void ReceiptSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width > 14 && e.NewSize.Height > 14)
            ReceiptPaper.Clip = ReceiptOutline(e.NewSize.Width, e.NewSize.Height);
    }

    private void DragReceipt(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var source = e.OriginalSource as DependencyObject;
        while (source is not null)
        {
            if (source is System.Windows.Controls.Primitives.ButtonBase) return;
            source = VisualTreeHelper.GetParent(source);
        }
        DragMove();
    }

    private void PreferencesChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (!closed) Dispatcher.BeginInvoke(() => { if (!closed) ApplyTheme(); });
    }

    private void ApplyTheme()
    {
        bool dark = false;
        try { dark = (int?)Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) == 0; }
        catch (Exception) { /* A theme preference must not prevent startup. */ }
        string[] keys = ["Paper", "Ink", "Muted", "Rule", "Hover", "ErrorInk", "Note", "Flap", "Accent"];
        string[] colors = dark
            ? ["#1D2022", "#E8E9E5", "#8C9295", "#3A3F42", "#282C2E", "#F0705F", "#1D2022", "#282C2E", "#3A3F42"]
            : ["#F7F6F1", "#1B1D1F", "#6C7174", "#B9BDB9", "#EFEEE8", "#B0281C", "#F7F6F1", "#EFEEE8", "#E6E5DE"];
        for (int i = 0; i < keys.Length; i++)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
            brush.Freeze(); Resources[keys[i]] = brush;
        }
        Resources["SignalGreen"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? "#72C68D" : "#2E7D47"));
        Resources["SignalYellow"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? "#F0C35B" : "#B87D14"));
        Resources["SignalRed"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? "#EF8277" : "#B84A3E"));
        if (SystemParameters.HighContrast)
        {
            Resources["Paper"] = SystemColors.WindowBrush;
            Resources["Ink"] = SystemColors.WindowTextBrush;
            Resources["Muted"] = SystemColors.WindowTextBrush;
            Resources["Rule"] = SystemColors.WindowTextBrush;
            Resources["Hover"] = SystemColors.HighlightBrush;
            Resources["ErrorInk"] = SystemColors.WindowTextBrush;
            Resources["Note"] = Resources["Flap"] = Resources["Accent"] = SystemColors.WindowBrush;
        }
        Resources[SystemColors.WindowBrushKey] = Resources["Paper"];
        Resources[SystemColors.WindowTextBrushKey] = Resources["Ink"];
        Resources[SystemColors.ControlBrushKey] = Resources["Paper"];
        Resources[SystemColors.ControlTextBrushKey] = Resources["Ink"];
    }

    private async Task RefreshStatus()
    {
        if (closed || !await requests.WaitAsync(0)) return;
        try
        {
            var snapshot = await api.Call("/api/status", null, lifetime.Token);
            if (closed) return;
            bool recovered = missedStatus > 0;
            missedStatus = 0;
            if (recovered || renderedDate != DateTime.Today || !ConnectionView.SameDisplay(state, snapshot)) ApplyState(snapshot);
            else { state = snapshot; RefreshFeedback(); }
            string revision = $"{snapshot["instance_id"]}:{snapshot["history_revision"]}:{snapshot["last_code"]?["id"]}:{DateTime.Today:yyyy-MM-dd}";
            if (revision != historyRevision)
            {
                var history = await api.Call("/api/codes?limit=5", null, lifetime.Token);
                if (closed) return;
                RenderHistory(history["items"] as JsonArray ?? []);
                historyRevision = revision;
            }
            if (ConnectionOverlay.Visibility == Visibility.Visible && !devicesLoaded) await LoadDevices();
        }
        catch (OperationCanceledException) when (closed) { }
        catch (Exception)
        {
            if (closed) return;
            missedStatus++;
            StatusLabel.Text = "未运行";
            StateStampText.Text = "未运行";
            StateStamp.Visibility = Visibility.Visible;
            StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "SignalRed");
            ShowError("暂时无法联系后台，正在重试。已有验证码仍可复制。");
            ConnectionControls.IsEnabled = SettingsControls.IsEnabled = false;
            // Close after repeated loss of contact with the daemon.
            if (missedStatus >= 3) Close();
        }
        finally { requests.Release(); }
    }

    private void ApplyState(JsonObject snapshot)
    {
        renderedDate = DateTime.Today;
        state = snapshot;
        var view = ConnectionView.From(state);
        StateStampText.Text = ConnectionView.ReceiptStamp(state);
        StateStamp.Visibility = StateStampText.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        bool paused = state["paused"]?.GetValue<bool>() == true;
        string signal = ConnectionView.SignalFor(state);
        StatusLabel.Text = signal == "SignalGreen" ? "已连接"
            : signal == "SignalYellow" ? state["source_status"]?.ToString() == "reconnecting" ? "重连中" : "连接中"
            : "未连接";
        StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, signal);
        ConnectionNav.ToolTip = view.Title + " · 点击管理手机连接";
        ConnectionIntro.Visibility = view.First ? Visibility.Visible : Visibility.Collapsed;
        IntroTitle.Text = view.Title;
        IntroText.Text = "选择已配对的 iPhone，开始接收。";
        ConnectionDetail.Text = StatusLabel.Text;
        ConnectionDetail.ToolTip = view.Title;
        ConnectionStatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, signal);
        ConnectionControls.IsEnabled = SettingsControls.IsEnabled = !busy;
        UpdateDeviceButtons();
        if (paused && view.Connected) ConnectionDetail.ToolTip = "手机已连接 · 自动复制已暂停";
        var latest = state["last_code"] as JsonObject;
        LatestPanel.Visibility = view.First ? Visibility.Collapsed : Visibility.Visible;
        // Settings and status polls must not clear or rebuild unchanged receipt content.
        string presentation = (latest?.ToJsonString() ?? (view.Connected ? "waiting" : "disconnected"))
            + ":" + DateTime.Today.ToString("yyyy-MM-dd");
        if (presentation != latestPresentation)
        {
            latestCode = latest?["code"]?.ToString();
            CodeButton.IsEnabled = !string.IsNullOrEmpty(latestCode);
            CodeText.Text = latestCode ?? (view.Connected ? "等待短信" : "暂无验证码");
            CodeText.FontSize = latestCode is null ? 26 : latestCode.Length > 8 ? 26.6 : 44.8;
            var brand = Regex.Match(latest?["body"]?.ToString() ?? "", "【([^】]+)】");
            SourceName.Text = brand.Success ? brand.Groups[1].Value : latest?["title"]?.ToString() ?? "最新验证码";
            MessageText.Text = latest?["body"]?.ToString() ?? (view.Connected ? "收到验证码后显示在这里" : "连接就绪后开始接收");
            CodeButton.ToolTip = latest?["body"]?.ToString() ?? "点击复制验证码";
            CodeTime.Text = TimeLabel(latest?["time"]?.ToString());
            latestPresentation = presentation;
            CopyFeedback.Text = latestCode is null ? "" : "点击数字复制";
        }
        RefreshFeedback();
        if (!phoneDirty && !Phone.IsKeyboardFocusWithin) Phone.Text = state["settings"]?["phone"]?.ToString() ?? "";
        Sound.IsChecked = state["settings"]?["sound"]?.GetValue<bool>() == true;
        AutoCopy.IsChecked = !paused;
        AutoType.IsChecked = state["settings"]?["autotype"]?.GetValue<bool>() == true;
        var apps = state["settings"]?["autotype_apps"] as JsonArray;
        AppsNote.Text = apps is null || apps.Count == 0 ? "未设置"
            : apps.Any(x => x?.ToString() == "*") ? "任何窗口" : "指定应用";
        AppsNote.ToolTip = apps is null ? "" : string.Join("、", apps.Select(x => x?.ToString()));
        if (DateTime.UtcNow > feedbackUntil) ErrorBox.Visibility = Visibility.Collapsed;
        // The status explains connection failures without misdiagnosing authorization.
        const string connectionError = "连接遇到问题，请检查蓝牙或查看连接帮助。";
        const string connecting = "正在建立通知连接…";
        if (view.Error && (DeviceFeedback.Text.Length == 0 || DeviceFeedback.Text is connectionError or connecting))
        { DeviceFeedback.Text = connectionError; DeviceFeedback.ToolTip = state["source_error"]?.ToString() ?? view.Description; }
        else if (!view.Error)
        {
            if (DeviceFeedback.Text == connectionError || view.Connected && DeviceFeedback.Text == connecting) DeviceFeedback.Text = "";
            DeviceFeedback.ToolTip = null;
        }
    }

    private void RefreshFeedback()
    {
        if (DateTime.UtcNow <= feedbackUntil) return;
        CopyFeedback.Text = latestCode is null ? "" : "点击数字复制";
        Footer.Text = ""; Footer.Visibility = Visibility.Collapsed;
        ErrorBox.Visibility = Visibility.Collapsed;
    }

    private static string TimeLabel(string? value) => DateTime.TryParse(value, out var date)
        ? date.Date == DateTime.Today ? date.ToString("HH:mm") : date.ToString("MM-dd HH:mm") : "";

    private void RenderHistory(JsonArray notices)
    {
        HistoryRows.Children.Clear();
        AllHistoryRows.Children.Clear();
        foreach (var target in new[] { HistoryRows, AllHistoryRows })
        {
            var entries = notices.Take(target == HistoryRows ? 3 : 5).ToArray();
            if (entries.Length == 0)
            {
                var empty = new TextBlock { Text = "暂无记录", FontSize = 14, Margin = new Thickness(0, 12, 0, 0) };
                empty.SetResourceReference(ForegroundProperty, "Muted");
                target.Children.Add(empty);
            }
            foreach (var notice in entries)
            {
                string code = notice?["code"]?.ToString() ?? "";
                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var number = new TextBlock { Text = code, FontFamily = new FontFamily("Consolas"), FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
                var brand = Regex.Match(notice?["body"]?.ToString() ?? "", "【([^】]+)】");
                var sender = new TextBlock { Text = brand.Success ? brand.Groups[1].Value : notice?["title"]?.ToString() ?? "短信", FontSize = 12.5, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
                sender.SetResourceReference(ForegroundProperty, "Muted");
                string originalTime = TimeLabel(notice?["time"]?.ToString());
                var time = new TextBlock { Text = originalTime, FontFamily = new FontFamily("Consolas"), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
                time.SetResourceReference(ForegroundProperty, "Muted");
                Grid.SetColumn(time, 1); Grid.SetColumn(sender, 2);
                sender.HorizontalAlignment = HorizontalAlignment.Right;
                time.Margin = new Thickness(0, 0, 12, 0);
                row.Children.Add(number); row.Children.Add(sender); row.Children.Add(time);
                var button = new Button { Content = row, BorderThickness = new Thickness(0), Padding = new Thickness(0, 4, 0, 4), HorizontalContentAlignment = HorizontalAlignment.Stretch, ToolTip = "点击此行复制 · " + notice?["body"]?.ToString() };
                button.SetResourceReference(BorderBrushProperty, "Rule");
                System.Windows.Automation.AutomationProperties.SetName(button, "复制验证码 " + code);
                button.Click += async (_, _) =>
                {
                    if (!await CopyCode(code)) return;
                    time.Text = "✓ 已复制";
                    await Task.Delay(1500);
                    if (!closed) time.Text = originalTime;
                };
                target.Children.Add(button);
            }
        }
    }

    private sealed record Device(string Id, string Name);
    private async Task LoadDevices()
    {
        DeviceFeedback.Text = "查找已配对设备…";
        try
        {
            var result = await api.Call("/api/bluetooth/devices", null, lifetime.Token);
            if (closed) return;
            string selected = selectedDevice?.Id ?? state?["settings"]?["ancs_device"]?.ToString() ?? "";
            deviceList = (result["items"] as JsonArray ?? []).Select(d => new Device(d?["id"]?.ToString() ?? "", d?["name"]?.ToString() ?? "未命名设备")).ToList();
            selectedDevice = deviceList.FirstOrDefault(d => d.Id == selected);
            devicesLoaded = true;
            DeviceFeedback.Text = deviceList.Count == 0 ? "未找到设备，可查看首次连接帮助。" : "";
            RenderDevices();
            UpdateDeviceButtons();
        }
        catch (OperationCanceledException) when (closed) { }
        catch (Exception) { if (!closed) { devicesLoaded = true; DeviceFeedback.Text = "查询失败，请开启蓝牙后刷新。"; DeviceCards.Children.Clear(); DeviceCards.Children.Add(new TextBlock { Text = "无法查询设备，请开启蓝牙后刷新。", FontSize = 15, TextWrapping = TextWrapping.Wrap }); } }
    }

    private void UpdateDeviceButtons()
    {
        if (ConnectButton is null) return;
        string saved = state?["settings"]?["ancs_device"]?.ToString() ?? "";
        bool active = state?["settings"]?["bluetooth_enabled"]?.GetValue<bool>() != false && !string.IsNullOrEmpty(saved);
        bool same = selectedDevice is null || selectedDevice.Id == saved;
        ConnectButton.IsEnabled = state is not null && selectedDevice is not null;
        ConnectButton.Visibility = active && same ? Visibility.Collapsed : Visibility.Visible;
        DisconnectButton.IsEnabled = active;
        DisconnectButton.Visibility = active && same ? Visibility.Visible : Visibility.Collapsed;
        SelectedDeviceText.Text = selectedDevice?.Name ?? (string.IsNullOrEmpty(saved) ? "尚未选择" : "已保存的手机");
        SelectedDeviceText.ToolTip = SelectedDeviceText.Text;
        if (selectedDevice is not null && !same) ConnectionDetail.Text = "等待连接";
    }

    private void RenderDevices()
    {
        const int perPage = 4;
        int pages = Math.Max(1, (deviceList.Count + perPage - 1) / perPage);
        devicePage = Math.Clamp(devicePage, 0, pages - 1);
        DeviceCards.Children.Clear();
        foreach (var device in deviceList.Skip(devicePage * perPage).Take(perPage))
        {
            var label = new TextBlock { Text = device.Name, FontSize = 16, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };
            var button = new Button { Content = label, Height = 54, Margin = new Thickness(0, 0, 0, 10), HorizontalContentAlignment = HorizontalAlignment.Left, ToolTip = device.Name };
            button.SetResourceReference(BackgroundProperty, "Note");
            button.Click += (_, _) => { selectedDevice = device; UpdateDeviceButtons(); DeviceFeedback.Text = "已选好，点连接开始接收。"; ShowConnection(this, new RoutedEventArgs()); };
            DeviceCards.Children.Add(button);
        }
        if (deviceList.Count == 0) DeviceCards.Children.Add(new TextBlock { Text = "还没有已配对的设备。\n请先到系统蓝牙设置中添加 iPhone。", FontSize = 15, LineHeight = 25 });
        PreviousDevices.IsEnabled = devicePage > 0;
        NextDevices.IsEnabled = devicePage + 1 < pages;
        DevicesPageLabel.Text = $"{devicePage + 1} / {pages}";
    }

    private void ShowError(string text)
    {
        ErrorText.Text = text; ErrorBox.Visibility = Visibility.Visible;
        feedbackUntil = DateTime.UtcNow.AddSeconds(8);
    }

    private async Task Execute(Func<Task> action)
    {
        if (busy || closed) return;
        busy = true;
        ConnectionControls.IsEnabled = SettingsControls.IsEnabled = DeviceCards.IsEnabled = RefreshButton.IsEnabled = false;
        await requests.WaitAsync();
        try { if (!closed) await action(); }
        catch (OperationCanceledException) when (closed) { }
        catch (System.IO.IOException e) { if (!closed) ShowError(e.Message); }
        catch (Exception) { if (!closed) ShowError("操作未完成，请检查后台连接后重试。"); }
        finally
        {
            requests.Release(); busy = false;
            if (!closed) DeviceCards.IsEnabled = RefreshButton.IsEnabled = true;
            if (!closed) { if (state is not null) ApplyState(state); else ConnectionControls.IsEnabled = SettingsControls.IsEnabled = false; }
        }
    }

    private async Task SaveSettings(JsonObject patch)
    {
        var result = await api.Call("/api/settings", patch, lifetime.Token);
        if (!closed) ApplyState(result);
    }
    private async Task<bool> CopyCode(string code)
    {
        if (string.IsNullOrEmpty(code)) return false;
        // Clipboard is a local UI action; it remains available if the daemon is down.
        for (int attempt = 0; attempt < 4 && !closed; attempt++)
        {
            try { Clipboard.SetText(code); if (code == latestCode) CopyFeedback.Text = "✓ 已复制"; feedbackUntil = DateTime.UtcNow.AddSeconds(2); return true; }
            catch (System.Runtime.InteropServices.ExternalException) { await Task.Delay(60); }
        }
        if (!closed) ShowError("剪贴板正被其他程序使用，请重试。");
        return false;
    }
    private async void CopyLatest(object sender, RoutedEventArgs e) => await CopyCode(latestCode ?? "");
    private async void RefreshDevicesClick(object sender, RoutedEventArgs e) => await Execute(LoadDevices);
    private void Navigate(FrameworkElement page)
    {
        ConnectionOverlay.Visibility = Visibility.Collapsed;
        foreach (var candidate in new[] { HomePage, HistoryPage, DevicesPage, HelpPage })
            candidate.Visibility = candidate == page ? Visibility.Visible : Visibility.Collapsed;
    }
    private void ShowHome(object sender, RoutedEventArgs e) => Navigate(HomePage);
    private async void ShowConnection(object sender, RoutedEventArgs e)
    {
        bool open = ConnectionOverlay.Visibility != Visibility.Visible;
        if (DevicesPage.Visibility == Visibility.Visible || HelpPage.Visibility == Visibility.Visible) Navigate(HomePage);
        ConnectionOverlay.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        if (open && !devicesLoaded) await Execute(LoadDevices);
    }
    private void CloseConnection(object sender, RoutedEventArgs e) => ConnectionOverlay.Visibility = Visibility.Collapsed;
    private void DismissConnection(object sender, MouseButtonEventArgs e) { ConnectionOverlay.Visibility = Visibility.Collapsed; e.Handled = true; }
    private void KeepConnectionOpen(object sender, MouseButtonEventArgs e) => e.Handled = true;
    private void ShowHistory(object sender, RoutedEventArgs e) => Navigate(HistoryPage);
    private void ShowHelp(object sender, RoutedEventArgs e) => Navigate(HelpPage);
    private async void ShowDevices(object sender, RoutedEventArgs e)
    {
        Navigate(DevicesPage);
        RenderDevices();
        if (!devicesLoaded) await Execute(LoadDevices);
    }
    private void PreviousDevicesClick(object sender, RoutedEventArgs e) { devicePage--; RenderDevices(); }
    private void NextDevicesClick(object sender, RoutedEventArgs e) { devicePage++; RenderDevices(); }
    private async void ConnectClick(object sender, RoutedEventArgs e)
    {
        if (selectedDevice is not Device device) return;
        await Execute(async () =>
        {
            var result = await api.Call("/api/bluetooth/connect", new JsonObject { ["device_id"] = device.Id }, lifetime.Token);
            if (!closed) { ApplyState(result); DeviceFeedback.Text = "正在建立通知连接…"; }
        });
    }
    private async void DisconnectClick(object sender, RoutedEventArgs e) => await Execute(async () =>
    {
        var result = await api.Call("/api/bluetooth/disconnect", new JsonObject(), lifetime.Token);
        if (!closed) { ApplyState(result); DeviceFeedback.Text = "已断开，可随时重新连接。"; }
    });
    private void OpenBluetoothSettings(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true }); }
        catch (Exception) { ShowError("无法打开设置，请手动打开 Windows「设置 → 蓝牙和设备」。"); }
    }
    private async void SavePhoneClick(object sender, RoutedEventArgs e) => await StorePhone();
    private async void PhoneKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; await StorePhone(); } }
    private async Task StorePhone() => await Execute(async () =>
    {
        await SaveSettings(new JsonObject { ["phone"] = Phone.Text.Trim() });
        Phone.Text = state?["settings"]?["phone"]?.ToString() ?? "";
        phoneDirty = false;
        Footer.Text = "手机号已保存"; Footer.Visibility = Visibility.Visible; feedbackUntil = DateTime.UtcNow.AddSeconds(3);
    });
    private async void SoundClick(object sender, RoutedEventArgs e) { bool value = Sound.IsChecked == true; await Execute(() => SaveSettings(new JsonObject { ["sound"] = value })); }
    private async void AutoCopyClick(object sender, RoutedEventArgs e) { bool value = AutoCopy.IsChecked != true; await Execute(() => SaveSettings(new JsonObject { ["paused"] = value })); }
    private async void AutoTypeClick(object sender, RoutedEventArgs e) { bool value = AutoType.IsChecked == true; await Execute(() => SaveSettings(new JsonObject { ["autotype"] = value })); }
    private async void ChimeClick(object sender, RoutedEventArgs e) => await Execute(async () => { await api.Call("/api/chime", new JsonObject(), lifetime.Token); });
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
    private void WindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        if (ConnectionOverlay.Visibility == Visibility.Visible) ConnectionOverlay.Visibility = Visibility.Collapsed;
        else if (DevicesPage.Visibility == Visibility.Visible || HelpPage.Visibility == Visibility.Visible) ShowConnection(sender, e);
        else if (HomePage.Visibility != Visibility.Visible) Navigate(HomePage); else Close();
    }
}
