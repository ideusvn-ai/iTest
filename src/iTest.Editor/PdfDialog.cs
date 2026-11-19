using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ITest.Core;
using ITest.UI;

namespace ITest.Editor;

/// <summary>Chọn thông tin tiêu đề và các mã đề cần in.</summary>
public sealed class PdfDialog : Window
{
    private static readonly string Store = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "iTest-Editor", "pdf-header.txt");
    private readonly TextBox _dep = new(), _school = new();
    private readonly RadioButton _orig = new() { Content = "Chỉ đề gốc (mã 101, đúng thứ tự đã soạn)", IsChecked = true, Margin = new Thickness(0, 4, 0, 4) };
    private readonly RadioButton _all = new() { Margin = new Thickness(0, 4, 0, 4) };
    private readonly CheckBox _key = new() { Content = "Kèm trang đáp án và hướng dẫn chấm cho từng mã đề", IsChecked = true, Margin = new Thickness(0, 10, 0, 0) };
    private readonly ExamSet _exam;
    public PdfOptions Options { get; } = new();

    public PdfDialog(ExamSet exam)
    {
        _exam = exam;
        Style = (Style)Application.Current.FindResource("DialogWindow");
        Title = "Xuất đề thi PDF";
        Width = 560; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        try
        {
            var lines = File.Exists(Store) ? File.ReadAllLines(Store) : Array.Empty<string>();
            _dep.Text = lines.ElementAtOrDefault(0) ?? "";
            _school.Text = lines.ElementAtOrDefault(1) ?? exam.Unit;
        }
        catch { _school.Text = exam.Unit; }
        VietInput.Attach(_dep); VietInput.Attach(_school);

        var muted = (Brush)Application.Current.FindResource("Muted");
        var root = new StackPanel { Margin = new Thickness(24) };
        root.Children.Add(new TextBlock { Text = "Xuất đề thi PDF", FontSize = 20, FontWeight = FontWeights.Bold });
        root.Children.Add(new TextBlock { Text = $"{exam.Subject} · {exam.ExamName} · {exam.Questions.Count} câu · {exam.Minutes} phút", Foreground = muted, Margin = new Thickness(0, 4, 0, 14) });
        root.Children.Add(Label("Sở / Phòng Giáo dục (dòng trên cùng bên trái, có thể để trống)"));
        root.Children.Add(_dep);
        root.Children.Add(Label("Trường"));
        root.Children.Add(_school);
        root.Children.Add(Label("Mã đề cần in"));
        root.Children.Add(_orig);
        int codes = Math.Max(1, exam.Codes);
        _all.Content = exam.Codes > 2 ? $"Tất cả {codes} mã đề đã trộn (101 – {100 + codes})" : "Tất cả mã đề (bộ đề dưới 3 mã nên các mã giống đề gốc)";
        root.Children.Add(_all);
        root.Children.Add(_key);
        var miss = exam.Questions.Count(q => q.Missing().Count > 0);
        if (miss > 0)
            root.Children.Add(new TextBlock { Text = $"Lưu ý: còn {miss} câu chưa đủ thông tin; đề vẫn được xuất để xem thử.", Foreground = (Brush)Application.Current.FindResource("Warn"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var cancel = new Button { Content = "Huỷ", IsCancel = true, Margin = new Thickness(0, 0, 8, 0), MinWidth = 90 };
        var ok = new Button { Content = "Chọn nơi lưu…", IsDefault = true, Style = (Style)Application.Current.FindResource("BtnPrimary"), MinWidth = 140 };
        ok.Click += (_, _) => Accept();
        buttons.Children.Add(cancel); buttons.Children.Add(ok);
        root.Children.Add(buttons);
        Content = root;
    }

    private static TextBlock Label(string s) => new() { Text = s, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 4) };

    private void Accept()
    {
        Options.Department = _dep.Text.Trim();
        Options.School = _school.Text.Trim();
        Options.AnswerKey = _key.IsChecked == true;
        Options.Codes = _all.IsChecked == true
            ? Enumerable.Range(0, Math.Max(1, _exam.Codes)).Select(i => (101 + i).ToString()).ToList()
            : new List<string> { "101" };
        try { Directory.CreateDirectory(Path.GetDirectoryName(Store)!); File.WriteAllLines(Store, new[] { Options.Department, Options.School }); } catch { }
        DialogResult = true;
    }
}
