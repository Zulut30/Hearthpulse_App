using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Hearthpulse.UI.Infrastructure;

namespace Hearthpulse.UI.Controls;

/// <summary>
/// A win rate as one bar: wins from the left, losses after them, so 0 % reads as "all lost" and not as an empty bar.
/// Without games only the track shows; with few games (<see cref="Few"/>) the bar is faded. Deck rows and versions.
/// </summary>
public sealed class WinBar : Control
{
    public static readonly StyledProperty<double> FractionProperty = AvaloniaProperty.Register<WinBar, double>(nameof(Fraction));

    public static readonly StyledProperty<int> GamesProperty = AvaloniaProperty.Register<WinBar, int>(nameof(Games));

    public static readonly StyledProperty<bool> FewProperty = AvaloniaProperty.Register<WinBar, bool>(nameof(Few));

    static WinBar()
    {
        AffectsRender<WinBar>(FractionProperty, GamesProperty, FewProperty);
    }

    /// <summary>Share of games won, 0..1.</summary>
    public double Fraction { get => GetValue(FractionProperty); set => SetValue(FractionProperty, value); }

    public int Games { get => GetValue(GamesProperty); set => SetValue(GamesProperty, value); }

    public bool Few { get => GetValue(FewProperty); set => SetValue(FewProperty, value); }

    public override void Render(DrawingContext context)
    {
        var (w, h) = (Bounds.Width, Bounds.Height);
        if (w < 4 || h < 2)
        {
            return;
        }

        var radius = Math.Min(3, h / 2);
        context.DrawRectangle(ThemeBrushes.Get("Surface3"), null, new Rect(0, 0, w, h), radius, radius);
        if (Games <= 0)
        {
            return;
        }

        using (context.PushOpacity(Few ? 0.45 : 1))
        {
            var won = Math.Round(w * Math.Clamp(Fraction, 0, 1));
            var gap = won > 0 && won < w ? 1 : 0;
            if (won < w)
            {
                using (context.PushOpacity(0.7))
                {
                    context.DrawRectangle(ThemeBrushes.Get("Negative"), null, new Rect(won + gap, 0, w - won - gap, h), radius, radius);
                }
            }

            if (won > 0)
            {
                context.DrawRectangle(ThemeBrushes.Get("Positive"), null, new Rect(0, 0, won, h), radius, radius);
            }
        }
    }
}
