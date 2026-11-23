using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ITest.UI;

/// <summary>
/// Lớp phủ "đang xử lý": làm mờ cửa sổ, hiện thanh tiến trình và dòng chữ, chạy công việc nặng ở luồng nền
/// để giao diện không bị treo. Dùng cho mọi tác vụ có thể gây lag (mã hoá, nén, đọc/ghi file, xuất PDF/Excel).
/// </summary>
public static class Busy
{
    public static Task Run(Window w, string message, Action work)
        => Run(w, message, () => { work(); return true; });

    public static Task<T> Run<T>(Window w, string message, Func<T> work)
        => Run(w, message, _ => work());

    /// <summary>Công việc nhận một hàm để đổi dòng chữ tiến trình giữa chừng (gọi được từ luồng nền).</summary>
    public static async Task<T> Run<T>(Window w, string message, Func<Action<string>, T> work)
    {
        var text = new TextBlock
        {
            Text = message, Foreground = Brushes.White, FontSize = 15, FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
            MaxWidth = 360
        };
        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x0F, 0x17, 0x2A)), CornerRadius = new CornerRadius(16),
            Padding = new Thickness(28, 24, 28, 24), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 0, Opacity = 0.35 }
        };
        var stack = new StackPanel { MinWidth = 220 };
        stack.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 6, Width = 240, Margin = new Thickness(0, 0, 0, 14) });
        stack.Children.Add(text);
        card.Child = stack;
        var overlay = new Grid { Background = new SolidColorBrush(Color.FromArgb(0x66, 0x0F, 0x17, 0x2A)) };
        overlay.Children.Add(card);
        Panel.SetZIndex(overlay, 99999);

        var root = w.Content as Grid;
        if (root != null)
        {
            Grid.SetRowSpan(overlay, 999);
            Grid.SetColumnSpan(overlay, 999);
            root.Children.Add(overlay);
        }
        var ui = System.Threading.SynchronizationContext.Current;
        void Report(string m) => ui?.Post(_ => text.Text = m, null);
        // Trong lúc xử lý, chặn bàn phím (lớp phủ chỉ chặn được chuột) để nội dung không bị sửa giữa chừng.
        void Swallow(object s, System.Windows.Input.KeyEventArgs e) => e.Handled = true;
        w.PreviewKeyDown += Swallow;
        try
        {
            return await Task.Run(() => work(Report)).ConfigureAwait(true);
        }
        finally
        {
            w.PreviewKeyDown -= Swallow;
            root?.Children.Remove(overlay);
        }
    }
}
