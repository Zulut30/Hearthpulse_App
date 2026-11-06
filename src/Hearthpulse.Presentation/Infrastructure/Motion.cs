#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Infrastructure;
#else
namespace Hearthpulse.UI.Infrastructure;
#endif

/// <summary>
/// The motion tokens both UIs animate with, and whether they animate at all. The themes repeat these values as
/// resources (WPF Theme.xaml <c>MotionFast</c> / <c>MotionSettle</c> / <c>MotionEnter</c>, Avalonia Theme.axaml the
/// same keys); a test checks that every theme timing is one of them. Motion is opacity and render transforms only,
/// eased out (cubic for state feedback, quintic for travel and entrances), never linear, never a bounce.
/// </summary>
public static partial class Motion
{
    /// <summary>Hover in, press in: the first response to the pointer.</summary>
    public static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(120);

    /// <summary>Hover out, release, a toggle knob's travel, a tab underline, the selection pill's travel.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(180);

    /// <summary>A page or panel coming in: a fade with a short rise of <see cref="Rise"/>.</summary>
    public static readonly TimeSpan Enter = TimeSpan.FromMilliseconds(220);

    /// <summary>How far an entering page rises, in DIP.</summary>
    public const double Rise = 10;

    /// <summary>Every timing the themes may use (a zero timing is the reduced-motion build).</summary>
    public static IReadOnlyList<TimeSpan> Tokens { get; } = [Fast, Settle, Enter];

    /// <summary>The app's own "reduce motion" setting (<c>AppSettings.ReduceMotion</c>), set by the host.</summary>
    public static bool Reduced { get; set; }

    /// <summary>
    /// True when the UI animates: Windows' animation setting is on (and, for tests, the off switch is not set) and the
    /// player has not asked for less motion. With it off a slide or scale is an instant change.
    /// </summary>
    public static bool On => !Reduced && SystemAnimations();
}
