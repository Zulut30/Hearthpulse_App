#if HEARTHPULSE_WPF
using Hearthpulse.App.Infrastructure;
using Brush = System.Windows.Media.Brush;
using Geometry = System.Windows.Media.Geometry;
#else
using Hearthpulse.UI.Infrastructure;
using Brush = Avalonia.Media.IBrush;
using Geometry = Avalonia.Media.Geometry;
#endif
using Hearthpulse.Core;
using Hearthpulse.Core.Localization;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Main;
#else
namespace Hearthpulse.UI.Main;
#endif

/// <summary>
/// A theme as the picker shows it: a small window in the theme's own colours. <see cref="Key"/> is the preset name the
/// settings keep (<see cref="Name"/> is localized); Card is a plate on the page, Secondary the second accent, Motif the
/// backdrop line art (null where the theme's geometries are not loaded). The gallery also draws the theme's hero
/// (<see cref="Hero"/>, a card id), an overlay panel (<see cref="Panel"/>) and the plates' edge.
/// </summary>
public sealed record ThemeChoice(string Key, string Name, Brush Background, Brush Card, Brush Surface, Brush Accent, Brush Text,
    Brush Muted, Brush Secondary, Geometry? Motif, bool Selected) : IImmutableView
{
    public string Hero { get; init; } = ThemePalette.DefaultHero;

    public Brush? Panel { get; init; }

    public Brush? Edge { get; init; }

    /// <summary>The gallery tile's frame: the theme's accent on the theme in use, its plates' edge on the others.</summary>
    public Brush? Frame => Selected ? Accent : Edge;

    /// <summary>The accessible name of a gallery tile: the theme and whether it is on.</summary>
    public string Description => Selected ? Loc.F("Settings.Gallery.Current", Name) : Name;

    public static ThemeChoice From(ThemeSpec theme, bool selected) => new(
        theme.Name,
        Loc.Or("Main.Theme." + theme.Name, theme.Name),
        ThemeEngine.Brush(0xFF140D08, theme),
        ThemeEngine.Brush(0xFF2C1F13, theme),
        ThemeEngine.Brush(0xFF4A331E, theme),
        ThemeEngine.Brush(0xFFE9B949, theme),
        ThemeEngine.Brush(0xFFF4E7C9, theme),
        ThemeEngine.Brush(0xFFCDB894, theme),
        ThemeEngine.Brush(0xFF4FC2A8, theme),
        ThemeEngine.Motif(theme.Motif),
        selected)
    {
        Hero = theme.Hero,
        Panel = ThemeEngine.Brush(0xFA2C1F14, theme),
        Edge = ThemeEngine.Brush(0xFF3D2A19, theme),
    };
}

public sealed partial class MainViewModel
{
    private RelayCommand? _selectTheme;
    private RelayCommand? _applyCustomTheme;
    private ThemeChoice? _customPreview;

    /// <summary>Raised when the player picks a theme; the host saves it and repaints the app in the new colours.</summary>
    public event Action? ThemeChanged;

    public ThemeSpec CurrentTheme => ThemePalette.FromSettings(Settings);

    /// <summary>The presets as the gallery shows them, each with the player's accent swatch over its own.</summary>
    public IReadOnlyList<ThemeChoice> Themes =>
        ThemePalette.Presets.Select(p => ThemeChoice.From(ThemePalette.WithAccent(p, Settings.ThemeAccent),
            string.Equals(p.Name, Settings.Theme, StringComparison.OrdinalIgnoreCase))).ToList();

    public RelayCommand SelectTheme => _selectTheme ??= new RelayCommand(p =>
    {
        if (p is string name && ThemePalette.Presets.Any(t => t.Name == name) && !string.Equals(name, Settings.Theme, StringComparison.OrdinalIgnoreCase))
        {
            UseTheme(name);
        }
    });

    public RelayCommand ApplyCustomTheme => _applyCustomTheme ??= new RelayCommand(() => UseTheme(ThemePalette.CustomName));

    public bool CustomSelected => string.Equals(Settings.Theme, ThemePalette.CustomName, StringComparison.OrdinalIgnoreCase);

    public double AccentHue
    {
        get => Settings.ThemeAccentHue;
        set => SetCustom(() => Settings.ThemeAccentHue = value);
    }

    public double BaseHue
    {
        get => Settings.ThemeBaseHue;
        set => SetCustom(() => Settings.ThemeBaseHue = value);
    }

    public double BaseTint
    {
        get => Settings.ThemeBaseTint;
        set => SetCustom(() => Settings.ThemeBaseTint = value);
    }

    /// <summary>The custom theme as the sliders set it, before it is applied.</summary>
    public ThemeChoice CustomPreview => _customPreview ??= ThemeChoice.From(
        ThemePalette.Resolve(ThemePalette.CustomName, Settings.ThemeAccentHue, Settings.ThemeBaseHue, Settings.ThemeBaseTint), CustomSelected);

    private void UseTheme(string name)
    {
        Settings.Theme = name;
        RepaintTheme();
    }

    /// <summary>The theme, its accent or its shape changed: the pickers show it at once and the host repaints the app.</summary>
    private void RepaintTheme()
    {
        _customPreview = null;
        _previewTheme = null;
        // Avalonia repaints in place, so the picker shows the new selection at once; WPF rebuilds its windows anyway.
        Raise(nameof(Themes));
        Raise(nameof(CustomSelected));
        Raise(nameof(CustomPreview));
        RaiseAppearance();
        ThemeChanged?.Invoke();
    }

    private void SetCustom(Action write)
    {
        write();
        _customPreview = null;
        Raise(nameof(CustomPreview));
    }
}
