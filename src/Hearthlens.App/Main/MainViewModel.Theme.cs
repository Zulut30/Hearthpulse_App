using System.Windows.Media;
using Hearthlens.App.Infrastructure;
using Hearthlens.Core;

namespace Hearthlens.App.Main;

/// <summary>A theme as the picker shows it: its name and four of its colours.</summary>
public sealed record ThemeChoice(string Name, Brush Background, Brush Surface, Brush Accent, Brush Text, bool Selected)
{
    public static ThemeChoice From(ThemeSpec theme, bool selected) => new(
        theme.Name,
        ThemeEngine.Brush(0xFF140D08, theme),
        ThemeEngine.Brush(0xFF4A331E, theme),
        ThemeEngine.Brush(0xFFE9B949, theme),
        ThemeEngine.Brush(0xFFF4E7C9, theme),
        selected);
}

public sealed partial class MainViewModel
{
    private RelayCommand? _selectTheme;
    private RelayCommand? _applyCustomTheme;
    private ThemeChoice? _customPreview;

    /// <summary>Raised when the player picks a theme; the host rebuilds the windows in the new colours.</summary>
    public event Action? ThemeChanged;

    public ThemeSpec CurrentTheme => ThemePalette.Resolve(Settings.Theme, Settings.ThemeAccentHue, Settings.ThemeBaseHue, Settings.ThemeBaseTint);

    public IReadOnlyList<ThemeChoice> Themes =>
        ThemePalette.Presets.Select(p => ThemeChoice.From(p, string.Equals(p.Name, Settings.Theme, StringComparison.OrdinalIgnoreCase))).ToList();

    public RelayCommand SelectTheme => _selectTheme ??= new RelayCommand(p =>
    {
        if (p is string name && !string.Equals(name, Settings.Theme, StringComparison.OrdinalIgnoreCase))
        {
            Settings.Theme = name;
            ThemeChanged?.Invoke();
        }
    });

    public RelayCommand ApplyCustomTheme => _applyCustomTheme ??= new RelayCommand(() =>
    {
        Settings.Theme = ThemePalette.CustomName;
        ThemeChanged?.Invoke();
    });

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

    /// <summary>Your most recent hero, shown as faded art behind the page titles.</summary>
    public string? BannerHero => Recent.FirstOrDefault(g => g.HeroCardId.Length > 0)?.HeroCardId;

    private void SetCustom(Action write)
    {
        write();
        _customPreview = null;
        Raise(nameof(CustomPreview));
    }
}
