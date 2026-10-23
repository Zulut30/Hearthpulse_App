using System.Windows;
using System.Windows.Media;

namespace Hearthpulse.App.Controls;

/// <summary>
/// A win rate as one bar: wins from the left, losses after them, so 0 % reads as "all lost" and not as an empty bar.
/// Without games only the track shows; with few games (<see cref="Few"/>) the bar is faded. Deck rows and versions.
/// </summary>
public sealed class WinBar : FrameworkElement
{
    public static readonly DependencyProperty FractionProperty = DependencyProperty.Register(
        nameof(Fraction), typeof(double), typeof(WinBar), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GamesProperty = DependencyProperty.Register(
        nameof(Games), typeof(int), typeof(WinBar), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FewProperty = DependencyProperty.Register(
        nameof(Few), typeof(bool), typeof(WinBar), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Share of games won, 0..1.</summary>
    public double Fraction { get => (double)GetValue(FractionProperty); set => SetValue(FractionProperty, value); }

    public int Games { get => (int)GetValue(GamesProperty); set => SetValue(GamesProperty, value); }

    public bool Few { get => (bool)GetValue(FewProperty); set => SetValue(FewProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var (w, h) = (ActualWidth, ActualHeight);
        if (w < 4 || h < 2)
        {
            return;
        }

        var radius = Math.Min(3, h / 2);
        dc.DrawRoundedRectangle(Brush("Surface3"), null, new Rect(0, 0, w, h), radius, radius);
        if (Games <= 0)
        {
            return;
        }

        dc.PushOpacity(Few ? 0.45 : 1);
        var won = Math.Round(w * Math.Clamp(Fraction, 0, 1));
        var gap = won > 0 && won < w ? 1 : 0;
        if (won < w)
        {
            dc.PushOpacity(0.7);
            dc.DrawRoundedRectangle(Brush("Negative"), null, new Rect(won + gap, 0, w - won - gap, h), radius, radius);
            dc.Pop();
        }

        if (won > 0)
        {
            dc.DrawRoundedRectangle(Brush("Positive"), null, new Rect(0, 0, won, h), radius, radius);
        }

        dc.Pop();
    }

    private Brush Brush(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;
}
