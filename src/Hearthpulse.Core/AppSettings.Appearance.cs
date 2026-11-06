using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Hearthpulse.Core;

/// <summary>
/// Settings → Appearance: the colour theme, its accent swatch, the page background, corners, density and the interface
/// size (ThemePalette, ThemeLook; the Appearance tab in both UIs).
/// </summary>
public sealed partial class AppSettings
{
    /// <summary>Colour theme: a preset name, or "Custom" with the three values below.</summary>
    public string Theme { get; set; } = "Tavern";

    public double ThemeAccentHue { get; set; } = 42;

    public double ThemeBaseHue { get; set; } = 28;

    public double ThemeBaseTint { get; set; } = 1.0;

    /// <summary>A swatch of <see cref="ThemePalette.Accents"/> over the theme's own accent; empty keeps the theme's.</summary>
    public string ThemeAccent { get; set; } = "";

    /// <summary>A hero's art behind each main-window page ("Background characters"), from the card art cache.</summary>
    public bool PageHeroes { get; set; } = true;

    /// <summary>One hero's art (a card id) behind every page instead of the one picked by page and mode; empty picks by page.</summary>
    public string BackgroundHero { get; set; } = "";

    public const double MinBackgroundIntensity = 0.25;

    /// <summary>
    /// How strongly the page art shows, as a share of its built-in opacity: the contrast tests are run at full strength,
    /// so lowering it can only make text easier to read.
    /// </summary>
    public double BackgroundIntensity { get; set; } = 1.0;

    /// <summary>Card and button corners: <see cref="ThemeLook.Rounded"/> or <see cref="ThemeLook.Sharp"/>.</summary>
    public string CornerStyle { get; set; } = ThemeLook.Rounded;

    /// <summary>Room inside cards: <see cref="ThemeLook.Comfortable"/> or <see cref="ThemeLook.Compact"/>.</summary>
    public string Density { get; set; } = ThemeLook.Comfortable;

    public const double MinInterfaceScale = 0.9;
    public const double MaxInterfaceScale = 1.2;

    /// <summary>The main window's text and controls drawn larger or smaller (0.9-1.2; the overlay has its own size).</summary>
    public double InterfaceScale { get; set; } = 1.0;

    [JsonIgnore]
    public bool CornersRounded { get => CornerStyle != ThemeLook.Sharp; set => SetIf(value, () => CornerStyle = ThemeLook.Rounded); }

    [JsonIgnore]
    public bool CornersSharp { get => CornerStyle == ThemeLook.Sharp; set => SetIf(value, () => CornerStyle = ThemeLook.Sharp); }

    [JsonIgnore]
    public bool DensityComfortable { get => Density != ThemeLook.Compact; set => SetIf(value, () => Density = ThemeLook.Comfortable); }

    [JsonIgnore]
    public bool DensityCompact { get => Density == ThemeLook.Compact; set => SetIf(value, () => Density = ThemeLook.Compact); }

    /// <summary>Repairs the appearance values of a file written by hand or by a newer version (part of Normalize).</summary>
    private void NormalizeAppearance()
    {
        ThemeAccent ??= "";
        BackgroundHero ??= "";
        BackgroundIntensity = Bounded(BackgroundIntensity, MinBackgroundIntensity, 1, 1);
        InterfaceScale = Bounded(InterfaceScale, MinInterfaceScale, MaxInterfaceScale, 1);
        if (CornerStyle is not (ThemeLook.Rounded or ThemeLook.Sharp)) CornerStyle = ThemeLook.Rounded;
        if (Density is not (ThemeLook.Comfortable or ThemeLook.Compact)) Density = ThemeLook.Comfortable;
    }
}
