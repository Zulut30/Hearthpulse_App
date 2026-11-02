#if HEARTHPULSE_WPF
using Hearthpulse.App.Infrastructure;
using Hearthpulse.App.Main.Match;
#else
using Hearthpulse.UI.Infrastructure;
using Hearthpulse.UI.Main.Match;
#endif
using Hearthpulse.Core;

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
        // A copy for the pool thread: the loaded metas are added on this thread while the page builds.
        var metas = new Dictionary<string, Core.Constructed.ConstructedMeta>(_constructedMetas, StringComparer.Ordinal);
        Core.Replay.MatchPage? page;
        try
        {
            // A History game's file, else its starred copy; or a library file without a History row (MainViewModel.Library.cs).
            page = await Task.Run(() => LoadMatch(key, cards, metas)).ConfigureAwait(true);
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
        var (replay, card, built) = MatchReplayOf(key);
        MatchPage.SetReplay(replay, card, _history);
        if (built && card is { FileName: { } file })
        {
            // A card made for the header (the library shows another tab or filter): whether its file is there and starred.
            await FillHeaderCardAsync(card, file).ConfigureAwait(true);
        }

        return true;
    }

    /// <summary>The file and the star of a card made for the match header, read off the UI thread.</summary>
    private async Task FillHeaderCardAsync(LibraryCard card, string file)
    {
        var files = LibraryFiles;
        var (hasReplay, starred) = await Task.Run(() =>
        {
            var saved = files.SavedNames().Contains(file);
            return (saved || files.Header(Core.Replay.ReplayShelf.Recent, file) is not null, saved);
        }).ConfigureAwait(true);
        card.HasReplay = hasReplay;
        card.Starred = starred;
    }

    /// <summary>
    /// The opened game's replay file name (its bookmarks are kept by it) and its library card (the header's star and
    /// Share): a History game's file, or a Saved or Imported file opened without a History row.
    /// </summary>
    /// <returns>Built: the card was made here, not found among the library's (its file and star are still to read).</returns>
    private (string? Replay, LibraryCard? Card, bool Built) MatchReplayOf(string key)
    {
        if (key.StartsWith(Core.Replay.MatchPage.FileKeyPrefix, StringComparison.Ordinal))
        {
            var rest = key[Core.Replay.MatchPage.FileKeyPrefix.Length..];
            var slash = rest.IndexOf('/', StringComparison.Ordinal);
            if (slash < 0)
            {
                return (null, null, false);
            }

            var name = rest[(slash + 1)..];
            return (name, Library.AllCards.FirstOrDefault(c => c.FileName == name && c.HistoryKey is null), false);
        }

        if (_history.Game(key) is not { Replay: { Length: > 0 } replay } game)
        {
            return (null, null, false);
        }

        return Library.AllCards.FirstOrDefault(c => c.HistoryKey == key) is { } card
            ? (replay, card, false)
            : (replay, LibraryCardOf(game, Core.Replay.ReplayShelf.Recent, replay, key), true);
    }

    internal void CloseMatch()
    {
        _matchLoad++;
        _openMatchKey = null;
        MatchOpen = false;
        _matchPage?.Clear();
    }
}
