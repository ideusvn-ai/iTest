using System.IO;
using System.Windows;

namespace ITest.UI;

/// <summary>
/// Lưới an toàn chung cho cả ba ứng dụng: lỗi bất ngờ ở giao diện được ghi vào file nhật ký và chương trình chạy tiếp,
/// thay vì đóng sập giữa buổi thi. Lỗi nghiêm trọng (không cứu được) gọi <c>onFatal</c> để dọn dẹp trước khi thoát.
/// </summary>
public static class CrashGuard
{
    public static string LogPath { get; private set; } = "";
    private static int _shown;

    /// <param name="notify">true: báo cho người dùng một lần (Editor, Server); false: chỉ ghi nhật ký (máy thí sinh đang thi).</param>
    public static void Install(Application app, string appName, bool notify, Action? onFatal = null)
    {
        LogPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), appName, "loi.log");
        app.DispatcherUnhandledException += (_, e) =>
        {
            Write(e.Exception);
            e.Handled = true;
            if (notify && Interlocked.Exchange(ref _shown, 1) == 0)
            {
                try
                {
                    MessageBox.Show("Đã xảy ra lỗi ngoài dự kiến, chương trình vẫn tiếp tục chạy.\n\n" + e.Exception.Message +
                                    "\n\nChi tiết được ghi tại:\n" + LogPath, appName, MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                catch { }
            }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) Write(ex);
            try { onFatal?.Invoke(); } catch { }
        };
        TaskScheduler.UnobservedTaskException += (_, e) => { Write(e.Exception); e.SetObserved(); };
    }

    public static void Write(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 2_000_000) File.Delete(LogPath);
            File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { }
    }
}
