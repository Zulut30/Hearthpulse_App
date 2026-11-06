#if HEARTHPULSE_WPF
using Hearthpulse.App.Infrastructure;
using Brush = System.Windows.Media.Brush;
#else
using Hearthpulse.UI.Infrastructure;
using Brush = Avalonia.Media.IBrush;
#endif
using Hearthpulse.Core;
using Hearthpulse.Core.Localization;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Main;
#else
namespace Hearthpulse.UI.Main;
#endif

/// <summary>An accent swatch as Settings shows it: its colour in the current theme. An empty key is the theme's own accent.</summary>
public sealed record AccentChoice(string Key, string Name, Brush Fill, bool Selected);

/// <summary>A hero the page background can show instead of the one picked by page: its card id and name.</summary>
public sealed record BackgroundHeroChoice(string CardId, string Name, bool Selected);

/// <summary>
/// Settings → Appearance (hearthpulse-design skill, "Rule 1"): the theme gallery with a live preview of the tile under the pointer or the
/// keyboard, the accent swatch, the page background (by page, one hero, or none, and how strongly), corners, density and
/// the interface size. Colours and shape go through <see cref="ThemeChanged"/> (the host repaints); the background and
/// the size are bindings and change at once.
/// </summary>
public sealed partial class MainViewModel
{
    private static readonly string[] HeroClasses =
        ["WARRIOR", "SHAMAN", "ROGUE", "PALADIN", "HUNTER", "DRUID", "WARLOCK", "MAGE", "PRIEST", "DEMONHUNTER", "DEATHKNIGHT"];

    private ThemeChoice? _previewTheme;
    private RelayCommand? _previewThemeCommand;
    private RelayCommand? _selectAccent;
    private RelayCommand? _selectBackgroundHero;
    private RelayCommand? _chooseAppearance;

    /// <summary>The large preview over the gallery: the tile the pointer or focus is on, else the theme in use.</summary>
    public ThemeChoice PreviewTheme => _previewTheme
        ?? Themes.FirstOrDefault(t => t.Selected)
        ?? ThemeChoice.From(CurrentTheme, selected: true);

    /// <summary>Shows a preset (its key) in the preview without applying it; null or an unknown key goes back to the current theme.</summary>
    public RelayCommand PreviewThemeCommand => _previewThemeCommand ??= new RelayCommand(p =>
    {
        var preset = ThemePalette.Presets.FirstOrDefault(t => string.Equals(t.Name, p as string, StringComparison.OrdinalIgnoreCase));
        _previewTheme = preset is null ? null
            : ThemeChoice.From(ThemePalette.WithAccent(preset, Settings.ThemeAccent), string.Equals(preset.Name, Settings.Theme, StringComparison.OrdinalIgnoreCase));
        Raise(nameof(PreviewTheme));
    });

    public IReadOnlyList<AccentChoice> AccentChoices
    {
        get
        {
            var own = ThemePalette.Resolve(Settings.Theme, Settings.ThemeAccentHue, Settings.ThemeBaseHue, Settings.ThemeBaseTint);
            var list = new List<AccentChoice>
            {
                new(string.Empty, Loc.T("Settings.Accent.Theme"), ThemeEngine.Brush(0xFFE9B949, own), string.IsNullOrEmpty(Settings.ThemeAccent)),
            };
            list.AddRange(ThemePalette.Accents.Select(a => new AccentChoice(a.Key, Loc.Or("Settings.Accent." + a.Key, a.Key),
                ThemeEngine.Brush(0xFFE9B949, ThemePalette.WithAccent(own, a.Key)),
                string.Equals(a.Key, Settings.ThemeAccent, StringComparison.OrdinalIgnoreCase))));
            return list;
        }
    }

    /// <summary>Puts an accent swatch (its key; empty for the theme's own) over the theme.</summary>
    public RelayCommand SelectAccent => _selectAccent ??= new RelayCommand(p =>
    {
        var key = p as string ?? string.Empty;
        if (key.Length > 0 && !ThemePalette.Accents.Any(a => a.Key == key))
        {
            return;
        }

        if (!string.Equals(key, Settings.ThemeAccent, StringComparison.Ordinal))
        {
            Settings.ThemeAccent = key;
            RepaintTheme();
        }
    });

    // Page background: by page and mode (auto), one hero, or none. The three radios bind to these.
    public bool BackgroundAuto
    {
        get => Settings.PageHeroes && Settings.BackgroundHero.Length == 0;
        set => SetBackground(value, on: true, hero: string.Empty);
    }

    public bool BackgroundChosen
    {
        get => Settings.PageHeroes && Settings.BackgroundHero.Length > 0;
        set => SetBackground(value, on: true, hero: Settings.BackgroundHero.Length > 0 ? Settings.BackgroundHero : CurrentTheme.Hero);
    }

    public bool BackgroundOff
    {
        get => !Settings.PageHeroes;
        set => SetBackground(value, on: false, hero: Settings.BackgroundHero);
    }

    /// <summary>The heroes "one hero" can show: every class's default hero, the innkeeper and Bob.</summary>
    public IReadOnlyList<BackgroundHeroChoice> BackgroundHeroes =>
        HeroClasses.Select(c => (Id: ClassHero(c), Name: Loc.T("Core.Class." + c)))
            .Append((Id: PageHeroes.Innkeeper, Name: Loc.T("Settings.Background.Innkeeper")))
            .Append((Id: PageHeroes.Bob, Name: Loc.T("Settings.Background.Bob")))
            .Select(h => new BackgroundHeroChoice(h.Id, h.Name, string.Equals(h.Id, Settings.BackgroundHero, StringComparison.Ordinal)))
            .ToList();

    public RelayCommand SelectBackgroundHero => _selectBackgroundHero ??= new RelayCommand(p =>
    {
        if (p is string id && BackgroundHeroes.Any(h => h.CardId == id))
        {
            SetBackground(true, on: true, hero: id);
        }
    });

    /// <summary>How strongly the page art shows (a share of its built-in opacity).</summary>
    public double BackgroundIntensity
    {
        get => Settings.BackgroundIntensity;
        set
        {
            var bounded = Math.Clamp(value, AppSettings.MinBackgroundIntensity, 1);
            if (Math.Abs(bounded - Settings.BackgroundIntensity) > 0.001)
            {
                Settings.BackgroundIntensity = bounded;
                Raise();
                Raise(nameof(BackgroundIntensityText));
            }
        }
    }

    public string BackgroundIntensityText => Percent(Settings.BackgroundIntensity);

    /// <summary>The main window's size, for its layout transform (0.9-1.2).</summary>
    public double InterfaceScale
    {
        get => Settings.InterfaceScale;
        set
        {
            var bounded = Math.Round(Math.Clamp(value, AppSettings.MinInterfaceScale, AppSettings.MaxInterfaceScale), 2);
            if (Math.Abs(bounded - Settings.InterfaceScale) > 0.001)
            {
                Settings.InterfaceScale = bounded;
                Raise();
                Raise(nameof(InterfaceScaleText));
            }
        }
    }

    public string InterfaceScaleText => Percent(Settings.InterfaceScale);

    public bool CornersRounded { get => Settings.CornersRounded; set => SetShape(value, () => Settings.CornerStyle = ThemeLook.Rounded); }

    public bool CornersSharp { get => Settings.CornersSharp; set => SetShape(value, () => Settings.CornerStyle = ThemeLook.Sharp); }

    public bool InterfaceComfortable { get => Settings.DensityComfortable; set => SetShape(value, () => Settings.Density = ThemeLook.Comfortable); }

    public bool InterfaceCompact { get => Settings.DensityCompact; set => SetShape(value, () => Settings.Density = ThemeLook.Compact); }

    /// <summary>
    /// One choice of the Appearance tab's segmented rows by name ("Corners.Sharp", "Density.Compact", "Background.Off"...):
    /// the Avalonia radios use it, as a two-way binding there stops writing back once the group unchecks an option.
    /// </summary>
    public RelayCommand ChooseAppearance => _chooseAppearance ??= new RelayCommand(p =>
    {
        switch (p as string)
        {
            case "Corners.Rounded": CornersRounded = true; break;
            case "Corners.Sharp": CornersSharp = true; break;
            case "Density.Comfortable": InterfaceComfortable = true; break;
            case "Density.Compact": InterfaceCompact = true; break;
            case "Background.Auto": BackgroundAuto = true; break;
            case "Background.Chosen": BackgroundChosen = true; break;
            case "Background.Off": BackgroundOff = true; break;
        }
    });

    /// <summary>The look the theme engines build: corners and density.</summary>
    public ThemeLook CurrentLook => ThemeLook.FromSettings(Settings);

    private static string Percent(double share) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{Math.Round(share * 100):0} %");

    private void SetBackground(bool value, bool on, string hero)
    {
        if (!value || (Settings.PageHeroes == on && Settings.BackgroundHero == hero))
        {
            return;
        }

        Settings.PageHeroes = on;
        Settings.BackgroundHero = hero;
        Raise(nameof(BackgroundAuto));
        Raise(nameof(BackgroundChosen));
        Raise(nameof(BackgroundOff));
        Raise(nameof(BackgroundHeroes));
        Raise(nameof(SetupPageHeroes));
        RaisePageHero();
    }

    private void SetShape(bool value, Action write)
    {
        if (!value)
        {
            return;
        }

        var before = CurrentLook;
        write();
        if (CurrentLook != before)
        {
            RepaintTheme();
        }
    }

    /// <summary>Everything the Appearance tab shows besides the theme lists.</summary>
    private void RaiseAppearance()
    {
        foreach (var name in (string[])[nameof(PreviewTheme), nameof(AccentChoices), nameof(CornersRounded), nameof(CornersSharp),
                     nameof(InterfaceComfortable), nameof(InterfaceCompact), nameof(BackgroundAuto), nameof(BackgroundChosen), nameof(BackgroundOff),
                     nameof(BackgroundHeroes), nameof(BackgroundIntensity), nameof(BackgroundIntensityText), nameof(InterfaceScale),
                     nameof(InterfaceScaleText), nameof(PageHero)])
        {
            Raise(name);
        }
    }
}
