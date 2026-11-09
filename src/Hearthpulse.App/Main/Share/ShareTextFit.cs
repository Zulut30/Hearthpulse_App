using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Hearthpulse.App.Main.Share;

/// <summary>
/// Share pictures never cut a name or a number with an ellipsis or break it inside a word: the text gets smaller until it
/// fits its width (TASK-2026-11-09-deck-cover). <c>Line</c>: the whole text on one line (a card name in a deck row);
/// <c>Words</c>: the text still wraps at spaces, but its longest word fits a line (a stat value such as a class or a card).
/// Never below <see cref="MinFontSizeProperty"/>. As Avalonia's Main/Share/ShareTextFit.cs.
/// </summary>
public static class ShareTextFit
{
    public const string Line = "Line";
    public const string Words = "Words";

    public static readonly DependencyProperty ModeProperty = DependencyProperty.RegisterAttached(
        "Mode", typeof(string), typeof(ShareTextFit), new PropertyMetadata(null, OnModeChanged));

    public static readonly DependencyProperty MinFontSizeProperty = DependencyProperty.RegisterAttached(
        "MinFontSize", typeof(double), typeof(ShareTextFit), new PropertyMetadata(9.0));

    /// <summary>The font size the text asked for, before any fitting.</summary>
    private static readonly DependencyProperty BaseFontSizeProperty = DependencyProperty.RegisterAttached(
        "BaseFontSize", typeof(double), typeof(ShareTextFit), new PropertyMetadata(double.NaN));

    public static string? GetMode(DependencyObject element) => (string?)element.GetValue(ModeProperty);

    public static void SetMode(DependencyObject element, string? value) => element.SetValue(ModeProperty, value);

    public static double GetMinFontSize(DependencyObject element) => (double)element.GetValue(MinFontSizeProperty);

    public static void SetMinFontSize(DependencyObject element, double value) => element.SetValue(MinFontSizeProperty, value);

    /// <summary>The largest size from <paramref name="size"/> down, in half points, at which <paramref name="width"/> holds the text.</summary>
    internal static double Fit(double size, double min, double width, Func<double, double> measure)
    {
        while (size > min && measure(size) > width + 0.5)
        {
            size = Math.Max(min, size - 0.5);
        }

        return size;
    }

    private static void OnModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock text)
        {
            return;
        }

        text.SizeChanged -= OnSizeChanged;
        text.Loaded -= OnLoaded;
        if (e.NewValue is string)
        {
            text.SizeChanged += OnSizeChanged;
            text.Loaded += OnLoaded;
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs e) => Apply((TextBlock)sender);

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged)
        {
            Apply((TextBlock)sender);
        }
    }

    /// <summary>Fits the text to its current width (also called by the tests).</summary>
    internal static void Apply(TextBlock text)
    {
        var width = text.ActualWidth;
        if (width <= 0 || string.IsNullOrEmpty(text.Text))
        {
            return;
        }

        var size = (double)text.GetValue(BaseFontSizeProperty);
        if (double.IsNaN(size))
        {
            size = text.FontSize;
            text.SetValue(BaseFontSizeProperty, size);
        }

        var parts = GetMode(text) == Words ? text.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries) : [text.Text];
        var typeface = new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch);
        var dip = VisualTreeHelper.GetDpi(text).PixelsPerDip;
        var fitted = Fit(size, GetMinFontSize(text), width, s => parts.Max(p =>
            new FormattedText(p, CultureInfo.CurrentCulture, text.FlowDirection, typeface, s, Brushes.Black, dip).WidthIncludingTrailingWhitespace));
        if (fitted != text.FontSize)
        {
            text.SetCurrentValue(TextBlock.FontSizeProperty, fitted);
        }
    }
}
