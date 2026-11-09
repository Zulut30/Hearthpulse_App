using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Hearthpulse.App.Main.Share;

/// <summary>
/// Share pictures never cut a name or a number with an ellipsis or break it inside a word: the text gets smaller until it
/// fits its width (TASK-2026-11-09-deck-cover). <c>Line</c>: the whole text on one line (a card name in a deck row);
/// <c>Words</c>: the text still wraps at spaces, but its longest word fits a line (a stat value such as a class or a card).
/// Never below <see cref="MinFontSizeProperty"/> (inherited, so a list sets it once). Inside a <see cref="ScopeProperty"/>
/// element (a column of deck rows) every text takes the smallest size any of them needs, so the column reads evenly.
/// The text is fitted again when its width or its text changes. As Avalonia's Main/Share/ShareTextFit.cs (a static class
/// here, a sealed one there: Avalonia's attached properties need an owner type).
/// </summary>
public static class ShareTextFit
{
    public const string Line = "Line";
    public const string Words = "Words";

    public static readonly DependencyProperty ModeProperty = DependencyProperty.RegisterAttached(
        "Mode", typeof(string), typeof(ShareTextFit), new PropertyMetadata(null, OnModeChanged));

    public static readonly DependencyProperty MinFontSizeProperty = DependencyProperty.RegisterAttached(
        "MinFontSize", typeof(double), typeof(ShareTextFit),
        new FrameworkPropertyMetadata(DeckPictureCard.NameMinFontSize, FrameworkPropertyMetadataOptions.Inherits));

    public static readonly DependencyProperty ScopeProperty = DependencyProperty.RegisterAttached(
        "Scope", typeof(bool), typeof(ShareTextFit), new PropertyMetadata(false));

    /// <summary>The font size the text asked for, before any fitting.</summary>
    private static readonly DependencyProperty BaseFontSizeProperty = DependencyProperty.RegisterAttached(
        "BaseFontSize", typeof(double), typeof(ShareTextFit), new PropertyMetadata(double.NaN));

    /// <summary>The size this text alone would take.</summary>
    private static readonly DependencyProperty OwnSizeProperty = DependencyProperty.RegisterAttached(
        "OwnSize", typeof(double), typeof(ShareTextFit), new PropertyMetadata(double.NaN));

    private static readonly DependencyPropertyDescriptor TextDescriptor = DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));

    public static string? GetMode(DependencyObject element) => (string?)element.GetValue(ModeProperty);

    public static void SetMode(DependencyObject element, string? value) => element.SetValue(ModeProperty, value);

    public static double GetMinFontSize(DependencyObject element) => (double)element.GetValue(MinFontSizeProperty);

    public static void SetMinFontSize(DependencyObject element, double value) => element.SetValue(MinFontSizeProperty, value);

    public static bool GetScope(DependencyObject element) => (bool)element.GetValue(ScopeProperty);

    public static void SetScope(DependencyObject element, bool value) => element.SetValue(ScopeProperty, value);

    /// <summary>The largest size from <paramref name="size"/> down, in half points, at which <paramref name="width"/> holds the text.</summary>
    internal static double Fit(double size, double min, double width, Func<double, double> measure)
    {
        while (size > min && measure(size) > width + 0.5)
        {
            size = Math.Max(min, size - 0.5);
        }

        return size;
    }

    /// <summary>Fits the text to its current width, then evens its scope out (also called by the tests).</summary>
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
        var own = Fit(size, GetMinFontSize(text), width, s => parts.Max(p =>
            new FormattedText(p, CultureInfo.CurrentCulture, text.FlowDirection, typeface, s, Brushes.Black, dip).WidthIncludingTrailingWhitespace));
        text.SetValue(OwnSizeProperty, own);

        if (ScopeOf(text) is not { } scope)
        {
            SetSize(text, own);
            return;
        }

        var members = Descendants(scope).OfType<TextBlock>().Where(t => GetMode(t) is not null).ToList();
        var even = members.Select(t => (double)t.GetValue(OwnSizeProperty)).Where(s => !double.IsNaN(s)).DefaultIfEmpty(own).Min();
        foreach (var member in members)
        {
            SetSize(member, even);
        }
    }

    private static void SetSize(TextBlock text, double size)
    {
        if (size != text.FontSize)
        {
            text.SetCurrentValue(TextBlock.FontSizeProperty, size);
        }
    }

    private static DependencyObject? ScopeOf(DependencyObject element)
    {
        for (var node = VisualTreeHelper.GetParent(element); node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (GetScope(node))
            {
                return node;
            }
        }

        return null;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child))
            {
                yield return nested;
            }
        }
    }

    private static void OnModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock text)
        {
            return;
        }

        text.SizeChanged -= OnSizeChanged;
        text.Loaded -= OnLoaded;
        text.Unloaded -= OnUnloaded;
        TextDescriptor.RemoveValueChanged(text, OnTextChanged);
        if (e.NewValue is string)
        {
            text.SizeChanged += OnSizeChanged;
            text.Loaded += OnLoaded;
            text.Unloaded += OnUnloaded;
        }
    }

    /// <summary>Text changes are watched only while the text is on screen: the descriptor holds it strongly.</summary>
    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        var text = (TextBlock)sender;
        TextDescriptor.RemoveValueChanged(text, OnTextChanged);
        TextDescriptor.AddValueChanged(text, OnTextChanged);
        Apply(text);
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e) => TextDescriptor.RemoveValueChanged((TextBlock)sender, OnTextChanged);

    private static void OnTextChanged(object? sender, EventArgs e) => Apply((TextBlock)sender!);

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged)
        {
            Apply((TextBlock)sender);
        }
    }
}
