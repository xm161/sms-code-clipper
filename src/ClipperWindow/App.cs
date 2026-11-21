using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace ClipperWindow;

internal static class App
{
    internal const string WindowTitle = "SMS Code Clipper";
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string title);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--selftest"))
        {
            try { return SelfTest.Run(); }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        try
        {
            using var mutex = new Mutex(false, @"Local\SmsCodeClipper.Window", out bool created);
            if (!created)
            {
                // A concurrent first launch may still be constructing its window.
                for (int i = 0; i < 30; i++)
                {
                    var handle = FindWindow(null, WindowTitle);
                    if (handle != IntPtr.Zero) { ShowWindow(handle, 9); SetForegroundWindow(handle); return 0; }
                    Thread.Sleep(100);
                }
                return 1;
            }
            string data = Environment.GetEnvironmentVariable("SMS_CODE_CLIPPER_DATA_DIR")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SMSCodeClipper");
            int index = Array.IndexOf(args, "--data-dir");
            if (index >= 0 && index + 1 < args.Length) data = args[index + 1];
            var application = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            return application.Run(new MainWindow(Path.GetFullPath(data)));
        }
        catch (Exception)
        {
            MessageBox.Show("小票窗口无法启动。请检查原生窗口安装是否完整。", WindowTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }
}
