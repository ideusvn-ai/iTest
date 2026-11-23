using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using ITest.Core;

namespace ITest.UI;

/// <summary>Gắn bộ gõ TELEX/VNI vào một TextBox.</summary>
public static class VietInput
{
    public static ImeMode Mode { get; set; } = ImeMode.Telex;
    public static bool Enabled { get; set; } = true;

    public static void Attach(TextBox box)
    {
        box.PreviewTextInput -= OnInput;
        box.PreviewTextInput += OnInput;
    }

    private static void OnInput(object sender, TextCompositionEventArgs e)
    {
        if (!Enabled || sender is not TextBox box || e.Text.Length != 1) return;
        if (box.SelectionLength > 0) return;
        if (InsideFormula(box.Text, box.CaretIndex)) return;   // đang gõ công thức LaTeX giữa hai dấu $: không bỏ dấu
        if (VietIme.Process(box.Text, box.CaretIndex, e.Text[0], Mode, out var text, out var caret))
        {
            box.Text = text;
            box.CaretIndex = caret;
            e.Handled = true;
        }
    }

    /// <summary>Con trỏ đang nằm trong một công thức $...$ chưa đóng (số dấu $ đứng trước là số lẻ; \$ không tính).</summary>
    public static bool InsideFormula(string text, int caret)
    {
        bool inside = false;
        for (int i = 0; i < caret && i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length && text[i + 1] == '$') { i++; continue; }
            if (text[i] == '$') inside = !inside;
        }
        return inside;
    }

    /// <summary>Chèn một ký hiệu tại vị trí con trỏ.</summary>
    public static void Insert(TextBox box, string symbol)
    {
        int c = box.CaretIndex;
        box.Text = box.Text.Insert(c, symbol);
        box.CaretIndex = c + symbol.Length;
        box.Focus();
    }
}

public static class Anim
{
    private static readonly IEasingFunction Ease = new CubicEase { EasingMode = EasingMode.EaseOut };

    public static void FadeIn(UIElement el, double seconds = 0.35)
    {
        el.Opacity = 0;
        el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(seconds)) { EasingFunction = Ease });
    }

    /// <summary>Hiện một khung: mờ dần lên và trượt nhẹ từ dưới lên.</summary>
    public static void Show(FrameworkElement el, double seconds = 0.35)
    {
        el.Visibility = Visibility.Visible;
        var tt = el.RenderTransform as System.Windows.Media.TranslateTransform;
        if (tt == null || tt.IsFrozen) { tt = new System.Windows.Media.TranslateTransform(); el.RenderTransform = tt; }
        tt.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, new DoubleAnimation(14, 0, TimeSpan.FromSeconds(seconds + 0.1)) { EasingFunction = Ease });
        FadeIn(el, seconds);
    }
}

/// <summary>
/// Cuộn mượt bằng bánh xe chuột cho mọi ScrollViewer cuộn theo pixel trong ứng dụng.
/// Gọi <see cref="Enable"/> một lần khi khởi động.
/// </summary>
public static class SmoothScroll
{
    private sealed class State { public double Target; public bool Running; }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ScrollViewer, State> States = new();
    private static bool _enabled;

    public static void Enable()
    {
        if (_enabled) return;
        _enabled = true;
        EventManager.RegisterClassHandler(typeof(ScrollViewer), UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnWheel), true);
    }

    private static ScrollViewer? Innermost(DependencyObject? d, int delta)
    {
        while (d != null)
        {
            if (d is ScrollViewer sv && sv.ScrollableHeight > 0 && !sv.CanContentScroll)
            {
                bool canMove = delta < 0 ? sv.VerticalOffset < sv.ScrollableHeight - 0.5 : sv.VerticalOffset > 0.5;
                if (canMove) return sv;
            }
            d = d is System.Windows.Media.Visual || d is System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    private static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not ScrollViewer sv || sv.CanContentScroll) return;
        var target = Innermost(e.OriginalSource as DependencyObject, e.Delta);
        if (target != sv) return;
        var st = States.GetValue(sv, _ => new State { Target = sv.VerticalOffset });
        if (!st.Running) st.Target = sv.VerticalOffset;
        st.Target = Math.Max(0, Math.Min(sv.ScrollableHeight, st.Target - e.Delta * 0.9));
        e.Handled = true;
        if (st.Running) return;
        st.Running = true;
        EventHandler? tick = null;
        tick = (_, _) =>
        {
            double cur = sv.VerticalOffset;
            double next = cur + (st.Target - cur) * 0.22;
            if (Math.Abs(st.Target - next) < 0.5) next = st.Target;
            sv.ScrollToVerticalOffset(next);
            if (next == st.Target || !sv.IsLoaded)
            {
                System.Windows.Media.CompositionTarget.Rendering -= tick;
                st.Running = false;
            }
        };
        System.Windows.Media.CompositionTarget.Rendering += tick;
    }
}
