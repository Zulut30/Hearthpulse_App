using System.Globalization;
using Hearthpulse.App.Infrastructure;
using Hearthpulse.Core;
using Hearthpulse.Core.Tracking;
using Hearthpulse.Core.Tracking.Counters;

namespace Hearthpulse.App.Overlay;

/// <summary>Constructed extras: the counters panel (the mulligan panel is in OverlayViewModel.Mulligan.cs).</summary>
public sealed partial class OverlayViewModel
{
    private bool _showCounters;
    private IReadOnlyList<CounterChip> _myCounters = [];
    private IReadOnlyList<CounterChip> _opponentCounters = [];
    private string? _expandedCounter;
    private RelayCommand? _toggleCounter;
    private RelayCommand? _hideCounter;
    private ConstructedView? _liveView;
    private AppSettings? _liveSettings;

    /// <summary>The player hid a counter from the overlay (its mode becomes Off; Settings → Counters brings it back).</summary>
    public event Action<string, CounterMode>? CounterModeRequested;

    public bool ShowCounters { get => _showCounters; private set => Set(ref _showCounters, value); }

    public IReadOnlyList<CounterChip> MyCounters { get => _myCounters; private set => Set(ref _myCounters, value); }

    public IReadOnlyList<CounterChip> OpponentCounters { get => _opponentCounters; private set => Set(ref _opponentCounters, value); }

    /// <summary>Click a chip: show or hide what it counted right under it.</summary>
    public RelayCommand ToggleCounter => _toggleCounter ??= new RelayCommand(p =>
    {
        _expandedCounter = p is string id && id != _expandedCounter ? id : null;
        RebuildCounters();
    });

    public RelayCommand HideCounter => _hideCounter ??= new RelayCommand(p =>
    {
        if (p is string id)
        {
            CounterModeRequested?.Invoke(id, CounterMode.Off);
        }
    });

    private void UpdateLive(ConstructedView? view, AppSettings settings)
    {
        _liveView = view;
        _liveSettings = settings;
        RebuildCounters();

        UpdateMulligan(view?.Mulligan, settings);
    }

    private void RebuildCounters()
    {
        var settings = _liveSettings;
        var counters = _liveView?.Counters ?? [];
        if (settings is null || !settings.ShowCounters || counters.Count == 0)
        {
            ShowCounters = false;
            return;
        }

        var shown = counters.Where(c => Counters.ModeOf(settings.CounterModes, c.Id) switch
        {
            CounterMode.Always => true,
            CounterMode.Auto => c.Relevant,
            _ => false,
        }).ToList();

        MyCounters = shown.Where(c => !c.Opponent).Select(Chip).ToList();
        OpponentCounters = settings.ShowOpponentCounters ? shown.Where(c => c.Opponent).Select(Chip).ToList() : [];
        ShowCounters = MyCounters.Count + OpponentCounters.Count > 0;
    }

    private CounterChip Chip(CounterView c) => CounterChip.From(c, c.Id == _expandedCounter);
}
