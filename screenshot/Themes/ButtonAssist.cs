using System.Windows;
using System.Windows.Media;

namespace screenshot;

/// <summary>
/// Attached properties for the preview action buttons: per-button hover tint.
/// </summary>
public static class ButtonAssist
{
    public static readonly DependencyProperty HoverBrushProperty =
        DependencyProperty.RegisterAttached(
            "HoverBrush",
            typeof(Brush),
            typeof(ButtonAssist),
            new FrameworkPropertyMetadata(Brushes.WhiteSmoke));

    public static Brush GetHoverBrush(DependencyObject obj) => (Brush)obj.GetValue(HoverBrushProperty);
    public static void SetHoverBrush(DependencyObject obj, Brush value) => obj.SetValue(HoverBrushProperty, value);

    public static readonly DependencyProperty HoverBorderBrushProperty =
        DependencyProperty.RegisterAttached(
            "HoverBorderBrush",
            typeof(Brush),
            typeof(ButtonAssist),
            new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0xE2, 0xE2, 0xE6))));

    public static Brush GetHoverBorderBrush(DependencyObject obj) => (Brush)obj.GetValue(HoverBorderBrushProperty);
    public static void SetHoverBorderBrush(DependencyObject obj, Brush value) => obj.SetValue(HoverBorderBrushProperty, value);
}
