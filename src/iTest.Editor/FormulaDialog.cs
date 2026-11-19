using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ITest.UI;

namespace ITest.Editor;

/// <summary>Hộp thoại soạn công thức toán (LaTeX) có xem trước trực tiếp.</summary>
public sealed class FormulaDialog : Window
{
    private readonly TextBox _src = new()
    {
        FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 15, AcceptsReturn = false, MinHeight = 40,
        VerticalContentAlignment = VerticalAlignment.Center
    };
    private readonly ContentControl _preview = new() { MinHeight = 70, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _err = new() { Foreground = Brushes.IndianRed, Margin = new Thickness(0, 6, 0, 0) };
    public string Latex => _src.Text;

    public FormulaDialog(string initial = "")
    {
        Style = (Style)Application.Current.FindResource("DialogWindow");
        Title = "Soạn công thức toán";
        Width = 760; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new StackPanel { Margin = new Thickness(24) };
        root.Children.Add(new TextBlock { Text = "Soạn công thức toán", FontSize = 20, FontWeight = FontWeights.Bold });
        root.Children.Add(new TextBlock
        {
            Text = "Bấm mẫu để chèn, sau đó sửa a, b, x, n… thành giá trị cần dùng. Công thức được viết theo cú pháp LaTeX.",
            Foreground = (Brush)Application.Current.FindResource("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 12)
        });

        var tpl = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        foreach (var (label, tex) in RichMath.Templates)
        {
            var b = new Button { Content = label, MinWidth = 40, MinHeight = 32, Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(0, 0, 6, 6), ToolTip = tex };
            b.Click += (_, _) => Insert(tex);
            tpl.Children.Add(b);
        }
        root.Children.Add(tpl);
        root.Children.Add(new TextBlock { Text = "Mã công thức", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        root.Children.Add(_src);
        root.Children.Add(new TextBlock { Text = "Xem trước", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 4) });
        root.Children.Add(new Border
        {
            Background = Brushes.White, BorderBrush = (Brush)Application.Current.FindResource("Line"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(16), Child = _preview
        });
        root.Children.Add(_err);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var cancel = new Button { Content = "Huỷ", IsCancel = true, Margin = new Thickness(0, 0, 8, 0), MinWidth = 90 };
        var ok = new Button { Content = "Chèn công thức", IsDefault = true, Style = (Style)Application.Current.FindResource("BtnPrimary"), MinWidth = 140 };
        ok.Click += (_, _) => { DialogResult = true; };
        buttons.Children.Add(cancel); buttons.Children.Add(ok);
        root.Children.Add(buttons);
        Content = root;

        _src.TextChanged += (_, _) => Render();
        _src.Text = initial;
        Loaded += (_, _) => { _src.Focus(); Render(); };
    }

    private void Insert(string tex)
    {
        int c = _src.CaretIndex;
        _src.Text = _src.Text.Insert(c, tex);
        _src.CaretIndex = c + tex.Length;
        _src.Focus();
    }

    private void Render()
    {
        if (string.IsNullOrWhiteSpace(_src.Text)) { _preview.Content = null; _err.Text = ""; return; }
        var f = RichMath.Formula(_src.Text, 30);
        _preview.Content = f;
        _err.Text = f == null ? "Công thức chưa đúng cú pháp — kiểm tra các dấu ngoặc { }." : "";
    }
}
