using Hearthpulse.App.Infrastructure;
using Hearthpulse.App.Main.Match;
using Hearthpulse.Core;
using Hearthpulse.Core.Replay;

namespace Hearthpulse.App.Main;

/// <summary>The match page: a History game opened for its replay and details (<c>docs/match-replay.md</c>).</summary>
public sealed partial class MainViewModel
{
    private MatchViewModel? _matchPage;
    private bool _matchOpen;
    private int _matchLoad;
    private RelayCommand? _openMatch;

    public MatchViewModel MatchPage => _matchPage ??= new MatchViewModel(this);

    /// <summary>True while the match page replaces the History list.</summary>
    public bool MatchOpen { get => _matchOpen; private set => Set(ref _matchOpen, value); }

    /// <summary>Opens the match page of a <see cref="GameRow"/> (or a game key).</summary>
    public RelayCommand OpenMatch => _openMatch ??= new RelayCommand(p =>
    {
        var key = p is GameRow row ? row.Key : p as string;
        if (!string.IsNullOrEmpty(key))
        {
            _ = OpenMatchPageAsync(key);
        }
    });

    /// <summary>Opens a game's match page by its key (also used by the UI render check). False when there is no such game.</summary>
    public async Task<bool> OpenMatchPageAsync(string key)
    {
        var load = ++_matchLoad;
        MatchPage.BeginLoading();
        MatchOpen = true;
        var cards = _cards;
        var history = _history;
        Core.Replay.MatchPage? page;
        try
        {
            page = await Task.Run(() => history.Game(key) is { } game
                ? Core.Replay.MatchPage.Load(game, ReplayStore.Default, cards, ConstructedMetaFor(game.Format))
                : null).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Diag.Error("Opening the match page failed", ex);
            page = null;
        }

        if (load != _matchLoad)
        {
            return false;
        }

        if (page is null)
        {
            CloseMatch();
            return false;
        }

        MatchPage.Show(page);
        return true;
    }

    internal void CloseMatch()
    {
        _matchLoad++;
        MatchOpen = false;
        _matchPage?.Clear();
    }
}
