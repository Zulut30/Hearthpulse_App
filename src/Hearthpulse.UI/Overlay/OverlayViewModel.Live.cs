using System.Globalization;
using Hearthpulse.UI.Infrastructure;
using Hearthpulse.Core;
using Hearthpulse.Core.Tracking;
using Hearthpulse.Core.Tracking.Counters;

namespace Hearthpulse.UI.Overlay;

/// <summary>Constructed extras: the counters panel and the mulligan panel.</summary>
public sealed partial class OverlayViewModel
{
    private bool _showCounters;
    private bool _showMulligan;
    private IReadOnlyList<CounterChip> _myCounters = [];
    private IReadOnlyList<CounterChip> _opponentCounters = [];
    private IReadOnlyList<MulliganTile> _mulliganCards = [];
    private string _mulliganTitle = string.Empty;
    private string _mulliganNote = string.Empty;
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

    public bool ShowMulligan { get => _showMulligan; private set => Set(ref _showMulligan, value); }

    public string MulliganTitle { get => _mulliganTitle; private set => Set(ref _mulliganTitle, value); }

    public string MulliganNote { get => _mulliganNote; private set => Set(ref _mulliganNote, value); }

    public IReadOnlyList<MulliganTile> MulliganCards { get => _mulliganCards; private set => Set(ref _mulliganCards, value); }

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

        var mulligan = view?.Mulligan;
        ShowMulligan = settings.ShowMulligan && mulligan is not null && mulligan.Cards.Any(c => c.KeepRate is not null);
        if (mulligan is null)
        {
            return;
        }

        MulliganTitle = $"Mulligan · {mulligan.Context}";
        MulliganNote = $"Deck win rate {mulligan.BaseWinRate:P1} · {mulligan.Source}";
        Replace(nameof(MulliganCards), mulligan.Cards, c => $"{c.CardId}{c.KeepRate:0.000}{c.WinRate:0.000}", () => MulliganCards = mulligan.Cards
            .Select(c => new MulliganTile(
                c.CardId,
                c.Name,
                c.KeepRate is { } keep ? $"Kept {keep:P0}" : "No data",
                c.WinRate is { } win ? $"WR {win:P1}" : string.Empty,
                c.Delta is { } delta ? (delta * 100).ToString("+0.0;−0.0;0.0", CultureInfo.CurrentCulture) : string.Empty,
                c.Delta > 0.005,
                c.Delta < -0.005,
                c.KeepRate is not null))
            .ToList());
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

    private CounterChip Chip(CounterView c) => new(
        c.Id,
        c.Title,
        c.Value,
        c.Progress ?? 0,
        c.Progress is not null,
        c.IconCardId,
        c.Details.Count == 0 ? c.Description : c.Description + Environment.NewLine + string.Join(Environment.NewLine, c.Details),
        c.Id == _expandedCounter && c.Details.Count > 0,
        c.Details);
}
