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
        if (VietIme.Process(box.Text, box.CaretIndex, e.Text[0], Mode, out var text, out var caret))
        {
            box.Text = text;
            box.CaretIndex = caret;
            e.Handled = true;
        }
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
    public static void FadeIn(UIElement el, double seconds = 0.35)
    {
        el.Opacity = 0;
        el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(seconds))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    public static void Show(FrameworkElement el)
    {
        el.Visibility = Visibility.Visible;
        FadeIn(el);
    }
}
