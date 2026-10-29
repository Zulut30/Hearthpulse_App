#if HEARTHPULSE_WPF
using Hearthpulse.App.Infrastructure;
using Hearthpulse.App.Main.Match;
#else
using Hearthpulse.UI.Infrastructure;
using Hearthpulse.UI.Main.Match;
#endif
using Hearthpulse.Core;
using Hearthpulse.Core.Replay;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Main;
#else
namespace Hearthpulse.UI.Main;
#endif

/// <summary>The match page: a History game opened for its replay and details (<c>docs/match-replay.md</c>).</summary>
public sealed partial class MainViewModel
{
    private MatchViewModel? _matchPage;
    private bool _matchOpen;
    private int _matchLoad;
    private string? _openMatchKey;
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
    public Task<bool> OpenMatchPageAsync(string key) => LoadMatchAsync(key, keepPosition: false);

    /// <summary>The open match built again in the new language, at the step on screen.</summary>
    private void RebuildMatch()
    {
        if (MatchOpen && _openMatchKey is { } key)
        {
            LoadMatchAsync(key, keepPosition: true).Forget("Match page rebuild");
        }
    }

    private async Task<bool> LoadMatchAsync(string key, bool keepPosition)
    {
        var load = ++_matchLoad;
        _openMatchKey = key;
        if (!keepPosition)
        {
            MatchPage.BeginLoading();
        }

        MatchOpen = true;
        var cards = _cards;
        var history = _history;
        // A copy for the pool thread: the loaded metas are added on this thread while the page builds.
        var metas = new Dictionary<string, Core.Constructed.ConstructedMeta>(_constructedMetas, StringComparer.Ordinal);
        Core.Replay.MatchPage? page;
        try
        {
            page = await Task.Run(() => history.Game(key) is { } game
                ? Core.Replay.MatchPage.Load(game, ReplayStore.Default, cards,
                    Core.Constructed.ConstructedMeta.FormatKeyOf(game.Format) is { } format && metas.TryGetValue(format, out var meta) ? meta : null)
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

        MatchPage.Show(page, keepPosition);
        return true;
    }

    internal void CloseMatch()
    {
        _matchLoad++;
        _openMatchKey = null;
        MatchOpen = false;
        _matchPage?.Clear();
    }
}
