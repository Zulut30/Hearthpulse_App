using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Hearthpulse.UI.Main.Share;

/// <summary>
/// Share pictures never cut a name or a number with an ellipsis or break it inside a word: the text gets smaller until it
/// fits its width (TASK-2026-11-09-deck-cover). <c>Line</c>: the whole text on one line (a card name in a deck row);
/// <c>Words</c>: the text still wraps at spaces, but its longest word fits a line (a stat value such as a class or a card).
/// Never below <see cref="MinFontSizeProperty"/> (inherited, so a list sets it once). Inside a <see cref="ScopeProperty"/>
/// element (a column of deck rows) every text takes the smallest size any of them needs, so the column reads evenly.
/// The text is fitted again when its width or its text changes. As WPF's Main/Share/ShareTextFit.cs (a sealed class with
/// no instances here, as Avalonia's attached properties need an owner type; a static class there).
/// </summary>
public sealed class ShareTextFit
{
    public const string Line = "Line";
    public const string Words = "Words";

    public static readonly AttachedProperty<string?> ModeProperty =
        AvaloniaProperty.RegisterAttached<ShareTextFit, TextBlock, string?>("Mode");

    public static readonly AttachedProperty<double> MinFontSizeProperty =
        AvaloniaProperty.RegisterAttached<ShareTextFit, Control, double>("MinFontSize", DeckPictureCard.NameMinFontSize, inherits: true);

    public static readonly AttachedProperty<bool> ScopeProperty =
        AvaloniaProperty.RegisterAttached<ShareTextFit, Control, bool>("Scope");

    /// <summary>The font size the text asked for, before any fitting.</summary>
    private static readonly AttachedProperty<double> BaseFontSizeProperty =
        AvaloniaProperty.RegisterAttached<ShareTextFit, TextBlock, double>("BaseFontSize", double.NaN);

    /// <summary>The size this text alone would take.</summary>
    private static readonly AttachedProperty<double> OwnSizeProperty =
        AvaloniaProperty.RegisterAttached<ShareTextFit, TextBlock, double>("OwnSize", double.NaN);

    static ShareTextFit()
    {
        ModeProperty.Changed.AddClassHandler<TextBlock>(OnModeChanged);
        TextBlock.TextProperty.Changed.AddClassHandler<TextBlock>((text, _) =>
        {
            if (GetMode(text) is not null && text.IsLoaded)
            {
                Apply(text);
            }
        });
    }

    private ShareTextFit()
    {
    }

    public static string? GetMode(TextBlock element) => element.GetValue(ModeProperty);

    public static void SetMode(TextBlock element, string? value) => element.SetValue(ModeProperty, value);

    public static double GetMinFontSize(Control element) => element.GetValue(MinFontSizeProperty);

    public static void SetMinFontSize(Control element, double value) => element.SetValue(MinFontSizeProperty, value);

    public static bool GetScope(Control element) => element.GetValue(ScopeProperty);

    public static void SetScope(Control element, bool value) => element.SetValue(ScopeProperty, value);

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
        var width = text.Bounds.Width;
        if (width <= 0 || string.IsNullOrEmpty(text.Text))
        {
            return;
        }

        var size = text.GetValue(BaseFontSizeProperty);
        if (double.IsNaN(size))
        {
            size = text.FontSize;
            text.SetValue(BaseFontSizeProperty, size);
        }

        var parts = GetMode(text) == Words ? text.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries) : [text.Text];
        var typeface = new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch);
        var own = Fit(size, GetMinFontSize(text), width, s => parts.Max(p =>
            new FormattedText(p, CultureInfo.CurrentCulture, text.FlowDirection, typeface, s, null).WidthIncludingTrailingWhitespace));
        text.SetValue(OwnSizeProperty, own);

        if (text.GetVisualAncestors().OfType<Control>().FirstOrDefault(GetScope) is not { } scope)
        {
            SetSize(text, own);
            return;
        }

        var members = scope.GetVisualDescendants().OfType<TextBlock>().Where(t => GetMode(t) is not null).ToList();
        var even = members.Select(t => t.GetValue(OwnSizeProperty)).Where(s => !double.IsNaN(s)).DefaultIfEmpty(own).Min();
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

    private static void OnModeChanged(TextBlock text, AvaloniaPropertyChangedEventArgs e)
    {
        text.SizeChanged -= OnSizeChanged;
        text.Loaded -= OnLoaded;
        if (e.NewValue is string)
        {
            text.SizeChanged += OnSizeChanged;
            text.Loaded += OnLoaded;
        }
    }

    private static void OnLoaded(object? sender, RoutedEventArgs e) => Apply((TextBlock)sender!);

    private static void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged)
        {
            Apply((TextBlock)sender!);
        }
    }
}
