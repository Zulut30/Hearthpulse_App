using System.Windows;
using System.Windows.Media;

namespace Hearthlens.App.Controls;

/// <summary>
/// Clips an element to a rounded rectangle that follows its size. WPF's ClipToBounds clips to the square
/// layout box and ignores Border.CornerRadius, so card art inside a rounded row would draw square corners.
/// </summary>
public static class RoundClip
{
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached(
        "Radius", typeof(double), typeof(RoundClip), new PropertyMetadata(0.0, OnRadiusChanged));

    public static double GetRadius(DependencyObject element) => (double)element.GetValue(RadiusProperty);

    public static void SetRadius(DependencyObject element, double value) => element.SetValue(RadiusProperty, value);

    private static void OnRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        element.SizeChanged -= OnSizeChanged;
        if ((double)e.NewValue > 0)
        {
            element.SizeChanged += OnSizeChanged;
            Apply(element);
        }
        else
        {
            element.ClearValue(UIElement.ClipProperty);
        }
    }

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e) => Apply((FrameworkElement)sender);

    private static void Apply(FrameworkElement element)
    {
        var width = element.ActualWidth;
        var height = element.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var radius = GetRadius(element);
        var clip = new RectangleGeometry(new Rect(0, 0, width, height), radius, radius);
        clip.Freeze();
        element.Clip = clip;
    }
}
