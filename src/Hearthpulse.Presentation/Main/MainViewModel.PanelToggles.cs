#if HEARTHPULSE_WPF
using Hearthpulse.App.Infrastructure;
#else
using Hearthpulse.UI.Infrastructure;
#endif
using Hearthpulse.Core;
using Hearthpulse.Core.Localization;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Main;
#else
namespace Hearthpulse.UI.Main;
#endif

/// <summary>
/// One overlay panel's show/hide button in the live bar. It reads and writes the panel's existing setting, the one
/// Settings -> Overlay / Tracker switches, so both stay the same setting.
/// </summary>
public sealed class PanelToggle : Observable
{
    private readonly AppSettings _settings;
    private readonly Func<AppSettings, bool> _get;
    private readonly Action<AppSettings, bool> _set;
    private readonly Action<PanelToggle> _changed;
    private readonly string _nameKey;
    private RelayCommand? _toggle;

    internal PanelToggle(AppSettings settings, string id, string icon, string nameKey, bool quick,
        Func<AppSettings, bool> get, Action<AppSettings, bool> set, Action<PanelToggle> changed)
    {
        _settings = settings;
        Id = id;
        Icon = icon;
        _nameKey = nameKey;
        Quick = quick;
        _get = get;
        _set = set;
        _changed = changed;
    }

    /// <summary>The <see cref="AppSettings"/> property it switches, e.g. "ShowTurnTimer".</summary>
    public string Id { get; }

    /// <summary>Material Design Icons glyph.</summary>
    public string Icon { get; }

    /// <summary>A button of its own in the bar; the rest are only in the bar's "more" menu (which lists every panel).</summary>
    public bool Quick { get; }

    public string Name => Loc.T(_nameKey);

    public bool IsOn
    {
        get => _get(_settings);
        set
        {
            if (value == IsOn)
            {
                return;
            }

            _set(_settings, value);
            Refresh();
            _changed(this);
        }
    }

    /// <summary>"Turn timer — shown, click to hide".</summary>
    public string Tip => Loc.F(IsOn ? "Main.Live.PanelShown" : "Main.Live.PanelHidden", Name);

    /// <summary>Flips <see cref="IsOn"/> (the menu items, which only show the state).</summary>
    public RelayCommand Toggle => _toggle ??= new RelayCommand(_ => IsOn = !IsOn);

    /// <summary>Re-reads the setting (changed on the settings page) and the texts (language change).</summary>
    public void Refresh() => Raise(string.Empty);
}

public sealed partial class MainViewModel
{
    private IReadOnlyList<PanelToggle>? _panelToggles;

    /// <summary>Raised when a live-bar button switched a panel, so the view syncs the settings page and the host saves.</summary>
    public event Action<PanelToggle>? PanelToggled;

    /// <summary>Every overlay panel with a visibility setting, in the order of the bar and its menu.</summary>
    public IReadOnlyList<PanelToggle> PanelToggles => _panelToggles ??= BuildPanelToggles();

    /// <summary>The ones with a button of their own in the bar.</summary>
    public IReadOnlyList<PanelToggle> QuickPanelToggles => PanelToggles.Where(t => t.Quick).ToList();

    /// <summary>Call after the settings page changed a setting: the bar's buttons show it at once.</summary>
    public void RefreshPanelToggles()
    {
        foreach (var toggle in PanelToggles)
        {
            toggle.Refresh();
        }
    }

    private IReadOnlyList<PanelToggle> BuildPanelToggles()
    {
        PanelToggle Make(string id, string icon, string name, bool quick, Func<AppSettings, bool> get, Action<AppSettings, bool> set) =>
            new(Settings, id, icon, "Main.Live.Panel." + name, quick, get, set, t => PanelToggled?.Invoke(t));

        return
        [
            Make(nameof(AppSettings.ShowDeckTracker), "\U000F0639", "Deck", true, s => s.ShowDeckTracker, (s, v) => s.ShowDeckTracker = v),
            Make(nameof(AppSettings.ShowOpponentTracker), "\U000F127B", "OpponentDeck", true, s => s.ShowOpponentTracker, (s, v) => s.ShowOpponentTracker = v),
            Make(nameof(AppSettings.ShowOpponentArchetype), "\U000F0B2F", "Archetype", true, s => s.ShowOpponentArchetype, (s, v) => s.ShowOpponentArchetype = v),
            Make(nameof(AppSettings.ShowTurnTimer), "\U000F051B", "TurnTimer", true, s => s.ShowTurnTimer, (s, v) => s.ShowTurnTimer = v),
            Make(nameof(AppSettings.ShowCounters), "\U000F0199", "Counters", true, s => s.ShowCounters, (s, v) => s.ShowCounters = v),
            Make(nameof(AppSettings.ShowCardPool), "\U000F1075", "CardPool", true, s => s.ShowCardPool, (s, v) => s.ShowCardPool = v),
            Make(nameof(AppSettings.ShowDrawChance), "\U000F1278", "DrawChance", false, s => s.ShowDrawChance, (s, v) => s.ShowDrawChance = v),
            Make(nameof(AppSettings.ShowOpponentCounters), "\U000F0199", "OpponentCounters", false, s => s.ShowOpponentCounters, (s, v) => s.ShowOpponentCounters = v),
            Make(nameof(AppSettings.ShowSecrets), "\U000F08EF", "Secrets", false, s => s.ShowSecrets, (s, v) => s.ShowSecrets = v),
            Make(nameof(AppSettings.ShowMulligan), "\U000F182C", "Mulligan", false, s => s.ShowMulligan, (s, v) => s.ShowMulligan = v),
            Make(nameof(AppSettings.ShowLadderSession), "\U000F0538", "LadderSession", false, s => s.ShowLadderSession, (s, v) => s.ShowLadderSession = v),
            Make(nameof(AppSettings.ShowBattlegroundsBar), "\U000F0A1D", "BgBar", false, s => s.ShowBattlegroundsBar, (s, v) => s.ShowBattlegroundsBar = v),
            Make(nameof(AppSettings.ShowOpponentBoards), "\U000F0B58", "Boards", false, s => s.ShowOpponentBoards, (s, v) => s.ShowOpponentBoards = v),
            Make(nameof(AppSettings.ShowCombatOdds), "\U000F0787", "Odds", false, s => s.ShowCombatOdds, (s, v) => s.ShowCombatOdds = v),
            Make(nameof(AppSettings.ShowLobbyPanel), "\U000F126F", "Lobby", false, s => s.ShowLobbyPanel, (s, v) => s.ShowLobbyPanel = v),
            Make(nameof(AppSettings.ShowSessionPanel), "\U000F012A", "BgSession", false, s => s.ShowSessionPanel, (s, v) => s.ShowSessionPanel = v),
        ];
    }
}
