using Hearthpulse.UI.Infrastructure;
using Hearthpulse.Core;
using Hearthpulse.Core.Tracking;
using Hearthpulse.Core.Tracking.Counters;

namespace Hearthpulse.UI.Overlay;

/// <summary>Battlegrounds counters: a compact row of chips (Blood Gem, Tavern spells, Eternal Knight...).</summary>
public sealed partial class OverlayViewModel
{
    private bool _showBgCounters;
    private IReadOnlyList<CounterChip> _bgCounters = [];
    private string? _expandedBgCounter;
    private string _bgCounterDetailTitle = string.Empty;
    private IReadOnlyList<string> _bgCounterDetails = [];
    private CounterChip? _bgCounterOpen;
    private RelayCommand? _toggleBgCounter;
    private RelayCommand? _hideBgCounter;
    private BattlegroundsView? _bgCounterView;
    private AppSettings? _bgCounterSettings;

    public bool ShowBgCounters { get => _showBgCounters; private set => Set(ref _showBgCounters, value); }

    public IReadOnlyList<CounterChip> BgCounters { get => _bgCounters; private set => Set(ref _bgCounters, value); }

    /// <summary>Title of the chip the player clicked, shown above its details under the row.</summary>
    public string BgCounterDetailTitle { get => _bgCounterDetailTitle; private set => Set(ref _bgCounterDetailTitle, value); }

    /// <summary>What the clicked chip counted (empty when no chip is open).</summary>
    public IReadOnlyList<string> BgCounterDetails { get => _bgCounterDetails; private set => Set(ref _bgCounterDetails, value); }

    /// <summary>The chip opened by a click, for its cards under the row; null when none is open.</summary>
    public CounterChip? BgCounterOpen { get => _bgCounterOpen; private set => Set(ref _bgCounterOpen, value); }

    /// <summary>Click a chip: show or hide its details under the row.</summary>
    public RelayCommand ToggleBgCounter => _toggleBgCounter ??= new RelayCommand(p =>
    {
        _expandedBgCounter = p is string id && id != _expandedBgCounter ? id : null;
        RebuildBgCounters();
    });

    /// <summary>The cross on a chip: its mode becomes Off (Settings, Counters and mulligan brings it back).</summary>
    public RelayCommand HideBgCounter => _hideBgCounter ??= new RelayCommand(p =>
    {
        if (p is string id)
        {
            CounterModeRequested?.Invoke(id, CounterMode.Off);
        }
    });

    private void UpdateBgCounters(BattlegroundsView? view, AppSettings settings)
    {
        _bgCounterView = view;
        _bgCounterSettings = settings;
        RebuildBgCounters();
    }

    private void RebuildBgCounters()
    {
        var settings = _bgCounterSettings;
        var view = _bgCounterView;
        if (settings is null || !settings.ShowCounters || view is null || view.IsHeroSelection)
        {
            ShowBgCounters = false;
            return;
        }

        var shown = view.Counters.Where(c => Counters.ModeOf(settings.CounterModes, c.Id) switch
        {
            CounterMode.Always => true,
            CounterMode.Auto => c.Relevant,
            _ => false,
        }).ToList();

        var open = shown.FirstOrDefault(c => c.Id == _expandedBgCounter);
        BgCounters = shown.Select(c => CounterChip.From(c, c == open)).ToList();
        BgCounterOpen = BgCounters.FirstOrDefault(c => c.Expanded);
        BgCounterDetailTitle = open?.Title ?? string.Empty;
        BgCounterDetails = open is null ? [] : [open.Description, .. open.Details];
        ShowBgCounters = BgCounters.Count > 0;
    }
}
