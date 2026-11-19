using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WpfMath.Controls;

namespace ITest.UI;

/// <summary>
/// Hiển thị văn bản có công thức toán viết bằng LaTeX đặt giữa hai dấu $ (ví dụ: "Tính $\frac{1}{2}+\sqrt{x}$").
/// Viết \$ để hiện một dấu $ thường.
/// </summary>
public static class RichMath
{
    public readonly record struct Segment(bool IsMath, string Text);

    public static bool HasMath(string? s) => !string.IsNullOrEmpty(s) && Split(s).Any(x => x.IsMath);

    public static List<Segment> Split(string s)
    {
        var list = new List<Segment>();
        var sb = new System.Text.StringBuilder();
        bool math = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\\' && i + 1 < s.Length && s[i + 1] == '$') { sb.Append('$'); i++; continue; }
            if (c == '$')
            {
                if (sb.Length > 0) list.Add(new Segment(math, sb.ToString()));
                sb.Clear();
                math = !math;
                continue;
            }
            sb.Append(c);
        }
        // Dấu $ không được đóng: coi phần còn lại là chữ thường.
        if (sb.Length > 0) list.Add(new Segment(false, (math ? "$" : "") + sb.ToString()));
        return list;
    }

    /// <summary>Tạo phần tử công thức; trả về null nếu công thức sai cú pháp.</summary>
    public static FrameworkElement? Formula(string latex, double fontSize, Brush? fg = null)
    {
        try
        {
            var f = new FormulaControl
            {
                Formula = latex,
                Scale = fontSize,
                Foreground = fg ?? Brushes.Black,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(1, 0, 1, 0),
                SnapsToDevicePixels = true
            };
            if (f.HasError) return null;
            return f;
        }
        catch { return null; }
    }

    /// <summary>Đổ nội dung có công thức vào một tập Inline (TextBlock.Inlines hoặc Paragraph.Inlines).</summary>
    public static void AddTo(InlineCollection inlines, string? text, double fontSize, Brush? fg = null)
    {
        if (string.IsNullOrEmpty(text)) return;
        foreach (var seg in Split(text))
        {
            if (!seg.IsMath) { inlines.Add(new Run(seg.Text)); continue; }
            var el = Formula(seg.Text, fontSize, fg);
            if (el == null) inlines.Add(new Run("$" + seg.Text + "$") { Foreground = Brushes.IndianRed });
            else inlines.Add(new InlineUIContainer(el) { BaselineAlignment = BaselineAlignment.Center });
        }
    }

    /// <summary>TextBlock chứa văn bản có công thức.</summary>
    public static TextBlock Block(string? text, double fontSize, Brush? fg = null)
    {
        var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = fontSize };
        if (fg != null) tb.Foreground = fg;
        AddTo(tb.Inlines, text, fontSize, fg);
        return tb;
    }

    /// <summary>Vẽ công thức thành ảnh PNG (dùng khi xuất PDF). dpi mặc định 300. Trả về null nếu lỗi.</summary>
    public static (byte[] png, double widthPt, double heightPt)? RenderPng(string latex, double fontSizePt, double dpi = 300)
    {
        try
        {
            var f = Formula(latex, fontSizePt * 96.0 / 72.0, Brushes.Black);
            if (f == null) return null;
            var host = new Border { Background = Brushes.Transparent, Child = f };
            host.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            host.Arrange(new Rect(host.DesiredSize));
            host.UpdateLayout();
            var w = host.ActualWidth; var h = host.ActualHeight;
            if (w < 1 || h < 1) return null;
            double s = dpi / 96.0;
            var bmp = new RenderTargetBitmap((int)Math.Ceiling(w * s), (int)Math.Ceiling(h * s), dpi, dpi, PixelFormats.Pbgra32);
            bmp.Render(host);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return (ms.ToArray(), w * 72.0 / 96.0, h * 72.0 / 96.0);
        }
        catch { return null; }
    }

    /// <summary>Mẫu công thức hay dùng cho hộp thoại soạn công thức.</summary>
    public static readonly (string label, string tex)[] Templates =
    {
        ("a/b", @"\frac{a}{b}"), ("√x", @"\sqrt{x}"), ("ⁿ√x", @"\sqrt[n]{x}"), ("xⁿ", "x^{n}"), ("xₙ", "x_{n}"),
        ("≤", @"\le"), ("≥", @"\ge"), ("≠", @"\ne"), ("≈", @"\approx"), ("±", @"\pm"), ("×", @"\times"), ("÷", @"\div"),
        ("π", @"\pi"), ("α", @"\alpha"), ("β", @"\beta"), ("Δ", @"\Delta"), ("∞", @"\infty"), ("∈", @"\in"), ("∉", @"\notin"),
        ("⊂", @"\subset"), ("∪", @"\cup"), ("∩", @"\cap"), ("∅", @"\emptyset"), ("→", @"\to"), ("⇒", @"\Rightarrow"), ("⇔", @"\Leftrightarrow"),
        ("∑", @"\sum_{i=1}^{n}"), ("∫", @"\int_{a}^{b} f(x)\,dx"), ("lim", @"\lim_{x\to 0}"), ("vec", @"\vec{AB}"), ("°", @"^{\circ}"),
        ("|x|", @"\left|x\right|"), ("( )", @"\left(x\right)"), ("hệ", @"\left\{\begin{matrix} x+y=1 \\ x-y=3 \end{matrix}\right."),
        ("log", @"\log_{a} b"), ("sin", @"\sin x"), ("cos", @"\cos x"), ("tan", @"\tan x")
    };
}
