using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Hearthpulse.App.Infrastructure;

/// <summary>WPF side of <see cref="Motion"/>: the Windows setting and the animations views start from code.</summary>
public static partial class Motion
{
    /// <summary>Cubic ease-out for state feedback (hover, press, toggles), shared and frozen.</summary>
    public static readonly IEasingFunction Ease = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });

    /// <summary>Quintic ease-out for travel and entrances (page rise, the selection pill).</summary>
    public static readonly IEasingFunction Travel = Frozen(new QuinticEase { EasingMode = EasingMode.EaseOut });

    private static bool SystemAnimations() => SystemParameters.ClientAreaAnimation;

    /// <summary>
    /// The page coming in fades in and rises <see cref="Rise"/> DIP over <see cref="Enter"/>: opacity and a render
    /// transform only, so nothing is laid out again. With motion off the page is simply shown.
    /// </summary>
    public static void Reveal(UIElement page, double rise = Rise)
    {
        if (!On)
        {
            page.BeginAnimation(UIElement.OpacityProperty, null);
            page.Opacity = 1;
            if (page.RenderTransform is TranslateTransform still)
            {
                still.BeginAnimation(TranslateTransform.YProperty, null);
                still.Y = 0;
            }

            return;
        }

        if (page.RenderTransform is not TranslateTransform shift || shift.IsFrozen)
        {
            shift = new TranslateTransform();
            page.RenderTransform = shift;
        }

        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(rise, 0, Enter) { EasingFunction = Travel });
        page.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Enter) { EasingFunction = Ease });
    }

    /// <summary>Moves <paramref name="shift"/> to <paramref name="y"/> over <see cref="Settle"/>, or at once with motion off.</summary>
    public static void Glide(TranslateTransform shift, double y, bool animate = true)
    {
        if (!animate || !On)
        {
            shift.BeginAnimation(TranslateTransform.YProperty, null);
            shift.Y = y;
            return;
        }

        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(y, Settle) { EasingFunction = Travel });
    }

    private static IEasingFunction Frozen(EasingFunctionBase ease)
    {
        ease.Freeze();
        return ease;
    }
}
