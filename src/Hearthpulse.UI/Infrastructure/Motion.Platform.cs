using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.Styling;
using Hearthpulse.Core;
using Hearthpulse.UI.Overlay;

namespace Hearthpulse.UI.Infrastructure;

/// <summary>Avalonia side of <see cref="Motion"/>: the Windows setting, the theme's transitions and the page reveal.</summary>
public static partial class Motion
{
    private static Styles? _still;

    private static bool SystemAnimations() => PoolInput.AnimationsOn();

    /// <summary>
    /// Takes the app's reduce-motion setting and, with motion off, turns the theme's transitions off: one style per
    /// element type that carries them, at style-trigger priority (the <c>:not</c> makes it one) so it outranks the
    /// templates' own <c>Transitions</c>. With motion on nothing is added, so styling costs nothing extra.
    /// </summary>
    public static void Apply(bool reduced)
    {
        Reduced = reduced;
        if (Application.Current is not { } app)
        {
            return;
        }

        if (On)
        {
            if (_still is not null)
            {
                app.Styles.Remove(_still);
                _still = null;
            }

            return;
        }

        if (_still is not null)
        {
            return;
        }

        _still = [Still<Border>(), Still<Panel>(), Still<ContentPresenter>()];
        app.Styles.Add(_still);
    }

    /// <summary>
    /// The page coming in fades in and rises <see cref="Rise"/> DIP over <see cref="Enter"/> (opacity and a render
    /// transform, nothing laid out again); with motion off it is simply shown.
    /// </summary>
    public static void Reveal(Visual page, double rise = Rise)
    {
        if (!On)
        {
            return;
        }

        var enter = new Animation
        {
            Duration = Enter,
            Easing = new QuinticEaseOut(),
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(Visual.OpacityProperty, 0d), new Setter(TranslateTransform.YProperty, rise) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(Visual.OpacityProperty, 1d), new Setter(TranslateTransform.YProperty, 0d) } },
            },
        };
        enter.RunAsync(page).Forget("Page reveal");
    }

    private static Style Still<T>() where T : Visual =>
        new(x => x.OfType<T>().Not(y => y.Class("motion-keep")))
        {
            Setters = { new Setter(Animatable.TransitionsProperty, null) },
        };
}
