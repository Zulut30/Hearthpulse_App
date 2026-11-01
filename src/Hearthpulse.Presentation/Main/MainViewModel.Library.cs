#if HEARTHPULSE_WPF
using Hearthpulse.App.Main.Match;
using Hearthpulse.App.Overlay;
#else
using Hearthpulse.UI.Main.Match;
using Hearthpulse.UI.Overlay;
#endif
using Hearthpulse.Core;
using Hearthpulse.Core.Constructed;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Memory;
using Hearthpulse.Core.Replay;
using Hearthpulse.Core.Stats;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Main;
#else
namespace Hearthpulse.UI.Main;
#endif

/// <summary>
/// The History page as the replay library (<c>docs/replay-library.md</c>): the cards of each tab, the star, Share, import
/// and delete, and opening a card's match page (a History game, or a file without a row).
/// </summary>
public sealed partial class MainViewModel
{
    private ReplayLibraryViewModel? _library;
    private IReadOnlyList<GameRecord> _libraryGames = [];
    private string _libraryFilter = "All";
    private int _libraryLoad;
    private Task _libraryTask = Task.CompletedTask;
    private Task _libraryAction = Task.CompletedTask;

    public ReplayLibraryViewModel Library => _library ??= new ReplayLibraryViewModel(this, LibraryFiles);

    /// <summary>The library's folders; tests give their own.</summary>
    internal ReplayLibrary LibraryFiles { get; init; } = ReplayLibrary.Default;

    /// <summary>History's games for the current filter (from <c>ApplyHistoryFilter</c>): the library shows its tab again.</summary>
    private void ShowLibrary(IReadOnlyList<GameRecord> games)
    {
        _libraryGames = games;
        if (_libraryFilter != HistoryFilter)
        {
            _libraryFilter = HistoryFilter;
            Library.ClearStatus();
        }

        ShowLibraryTab();
    }

    /// <summary>Builds the cards of the open tab: Recent at once from the History rows (their headers follow), Saved and Imported from their folders.</summary>
    internal void ShowLibraryTab(string? select = null)
    {
        var load = ++_libraryLoad;
        var library = Library;
        library.Refresh();
        if (library.IsRecent)
        {
            var cards = _libraryGames.Select(g => LibraryCardOf(g, ReplayShelf.Recent, g.Replay, g.Key)).ToList();
            library.SetCards(cards);
            _libraryTask = FillRecentAsync(load, cards);
            _libraryTask.Forget("Library headers");
        }
        else
        {
            library.BeginLoading();
            _libraryTask = LoadShelfAsync(load, library.IsSaved ? ReplayShelf.Saved : ReplayShelf.Imported, select);
            _libraryTask.Forget("Library folder");
        }
    }

    /// <summary>The headers of the Recent cards' files, off the UI thread: final board, hand and chips, whether a file is left, the stars.</summary>
    private async Task FillRecentAsync(int load, IReadOnlyList<LibraryCard> cards)
    {
        var files = LibraryFiles;
        var read = await Task.Run(() =>
        {
            var saved = files.SavedNames();
            var headers = cards.Select(c => c.FileName is { } name
                ? files.Header(ReplayShelf.Recent, name) ?? (saved.Contains(name) ? files.Header(ReplayShelf.Saved, name) : null)
                : null).ToList();
            return (Saved: saved, Headers: headers, Imported: files.Imported.Names().Count);
        }).ConfigureAwait(true);
        if (load != _libraryLoad)
        {
            return;
        }

        for (var i = 0; i < cards.Count; i++)
        {
            var card = cards[i];
            card.Starred = card.FileName is { } name && read.Saved.Contains(name);
            card.HasReplay = read.Headers[i] is not null;
            if (read.Headers[i]?.Summary is { } summary)
            {
                ApplySummary(card, summary);
            }
        }

        Library.SavedCount = read.Saved.Count;
        Library.ImportedCount = read.Imported;
    }

    private async Task LoadShelfAsync(int load, ReplayShelf shelf, string? select)
    {
        var files = LibraryFiles;
        var read = await Task.Run(() =>
        {
            var saved = files.SavedNames();
            var names = shelf == ReplayShelf.Saved ? saved.ToList() : files.Imported.Names().ToList();
            var headers = names.Select(n => files.Header(shelf, n)).ToList();
            return (Saved: saved, Names: names, Headers: headers, Imported: shelf == ReplayShelf.Imported ? names.Count : files.Imported.Names().Count);
        }).ConfigureAwait(true);
        if (load != _libraryLoad)
        {
            return;
        }

        // Newest game first; a starred History game keeps its row (and opens by it).
        var rows = (_recentGames ?? []).Where(g => g.Replay is not null).GroupBy(g => g.Replay!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var cards = new List<LibraryCard>();
        LibraryCard? selected = null;
        for (var i = 0; i < read.Names.Count; i++)
        {
            var name = read.Names[i];
            var row = shelf == ReplayShelf.Saved ? rows.GetValueOrDefault(name) : null;
            var game = row ?? Core.Replay.MatchPage.RecordOf(read.Headers[i], name);
            if (!FilterMatch(game, HistoryFilter))
            {
                continue;
            }

            var card = LibraryCardOf(game, shelf, name, row?.Key);
            card.Starred = read.Saved.Contains(name);
            if (read.Headers[i]?.Summary is { } summary)
            {
                ApplySummary(card, summary);
            }

            cards.Add(card);
            if (name == select)
            {
                selected = card;
            }
        }

        cards.Sort((a, b) => string.CompareOrdinal(b.SortKey, a.SortKey));
        Library.SavedCount = read.Saved.Count;
        Library.ImportedCount = read.Imported;
        Library.SetCards(cards, selected);
    }

    /// <summary>A card from a History row or a file's game: the same texts as the History row, plus when and how long.</summary>
    private LibraryCard LibraryCardOf(GameRecord g, ReplayShelf shelf, string? fileName, string? historyKey)
    {
        var bg = g.IsBattlegrounds;
        var top = g.Mode == "Duos" ? 2 : 4;
        var resultKey = g.IsAbandoned ? g.Result : bg ? (g.Placement is > 0 && g.Placement <= top ? "Win" : "Loss") : g.Result;
        var minutes = (int)Math.Round(g.Duration.TotalMinutes);
        var when = g.StartedAt == DateTime.MinValue ? string.Empty
            : minutes > 0 ? $"{When(g.StartedAt)} · {Loc.F("Library.Minutes", minutes)}" : When(g.StartedAt);
        var rating = g.RatingDelta is { } d ? (d >= 0 ? $"+{d}" : $"−{-d}") : string.Empty;
        if (bg)
        {
            return new LibraryCard(shelf, fileName, historyKey, battlegrounds: true)
            {
                HeroCardId = g.HeroCardId,
                Title = _cards.NameOf(g.HeroCardId),
                Caption = $"{Mode(g.Mode)} · {Loc.P("Main.Game.BgDetail", g.Turns, g.TavernTier)}",
                Result = g.IsAbandoned ? ResultText(g.Result) : Ordinal(g.Placement),
                ResultKey = resultKey,
                Rating = rating,
                RatingValue = g.RatingDelta ?? 0,
                When = when,
                SortKey = SortKeyOf(g, fileName),
            };
        }

        var deck = g.DeckName is { Length: > 0 } name ? DeckName.Display(name) : null;
        var playerClass = OverlayViewModel.ClassName(g.PlayerClass);
        var format = g.Format is { Length: > 0 } f ? Loc.Or("Match.Format." + f, string.Empty) : string.Empty;
        return new LibraryCard(shelf, fileName, historyKey, battlegrounds: false)
        {
            HeroCardId = g.HeroCardId,
            Title = deck ?? (playerClass.Length > 0 ? playerClass : Mode(g.Mode)),
            Caption = string.Join(" · ", new[]
            {
                deck is null ? null : playerClass,
                Mode(g.Mode),
                format,
                RankInfo.Localize(g.Rank),
                g.Turns > 0 ? Loc.P("Main.Game.Turns", g.Turns) : null,
            }.Where(x => !string.IsNullOrEmpty(x))),
            OpponentHeroCardId = g.OpponentHeroCardId,
            Opponent = g.OpponentClass is { Length: > 0 } opponent ? Loc.F("Main.Game.Vs", OverlayViewModel.ClassName(opponent)) : string.Empty,
            OpponentArchetype = g.OpponentArchetype is { Length: > 0 } archetype ? ArchetypeTranslations.Current.Display(archetype) ?? archetype : string.Empty,
            Result = ResultText(g.Result),
            ResultKey = resultKey,
            Rating = rating,
            RatingValue = g.RatingDelta ?? 0,
            When = when,
            SortKey = SortKeyOf(g, fileName),
        };
    }

    private static string SortKeyOf(GameRecord g, string? fileName) =>
        g.StartedAt != DateTime.MinValue ? g.StartedAt.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture) : fileName ?? string.Empty;

    /// <summary>A Battlegrounds card's parts from its replay's summary: hero powers and trinkets, the final board, the hand.</summary>
    private void ApplySummary(LibraryCard card, ReplaySummary summary)
    {
        if (!card.IsBattlegrounds)
        {
            return;
        }

        var cards = _cards;
        card.SetSummary(
            summary.HeroPowers.Concat(summary.Trinkets).Select(id => new LibraryChip(id, cards.NameOf(id))).ToList(),
            summary.FinalBoard.Select(m =>
            {
                var stats = $"{m.Attack}/{m.Health}";
                var tip = string.Join(" · ", new[] { cards.NameOf(m.CardId), stats }
                    .Concat(m.Golden ? [Loc.T("Match.Round.Golden")] : [])
                    .Concat(m.Keywords.Select(Keyword)));
                return new LibraryMinion(m.CardId, stats, m.Golden, m.Keywords.Contains("TAUNT"), m.Keywords.Contains("DIVINE_SHIELD"), tip);
            }).ToList(),
            summary.FinalHand.Select(id => new LibraryChip(id, cards.NameOf(id))).ToList());
    }

    /// <summary>A keyword as the match page names it; Venomous has its own Battlegrounds string.</summary>
    private static string Keyword(string keyword) =>
        keyword == "VENOMOUS" ? Loc.T("Match.Bg.Keyword.Venomous") : Loc.Or("Match.Keyword." + keyword, keyword);

    /// <summary>Opens a card's match page: by its History row when it has one, else from its file. True when the page opened.</summary>
    internal Task<bool> OpenLibraryCard(LibraryCard card) =>
        card.HistoryKey is { } key ? OpenMatchPageAsync(key)
        : card.FileName is { } name ? LoadMatchAsync(LibraryFileKey(card.Shelf, name), keepPosition: false)
        : Task.FromResult(false);

    /// <summary>The key of a file opened without a History row: its shelf and name.</summary>
    private static string LibraryFileKey(ReplayShelf shelf, string name) => $"{Core.Replay.MatchPage.FileKeyPrefix}{shelf}/{name}";

    /// <summary>Builds the match page for a key: a History game (its file, else its starred copy) or a library file.</summary>
    private Core.Replay.MatchPage? LoadMatch(string key, Core.Cards.CardDb cards, IReadOnlyDictionary<string, ConstructedMeta> metas)
    {
        ConstructedMeta? MetaOf(GameRecord game) =>
            ConstructedMeta.FormatKeyOf(game.Format) is { } format && metas.TryGetValue(format, out var meta) ? meta : null;

        if (key.StartsWith(Core.Replay.MatchPage.FileKeyPrefix, StringComparison.Ordinal))
        {
            var rest = key[Core.Replay.MatchPage.FileKeyPrefix.Length..];
            var slash = rest.IndexOf('/', StringComparison.Ordinal);
            if (slash < 0 || !Enum.TryParse<ReplayShelf>(rest[..slash], out var shelf))
            {
                return null;
            }

            var name = rest[(slash + 1)..];
            var text = LibraryFiles.Load(shelf, name);
            var record = Core.Replay.MatchPage.RecordOf(text is null ? null : ReplayFile.Read(text)?.Header, name);
            return Core.Replay.MatchPage.Load(record, text, cards, MetaOf(record));
        }

        return _history.Game(key) is { } game
            ? Core.Replay.MatchPage.Load(game, LibraryFiles.Load(ReplayShelf.Recent, game.Replay), cards, MetaOf(game))
            : null;
    }

    internal void ToggleLibraryStar(LibraryCard card, string name)
    {
        if (!card.Starred)
        {
            if (LibraryFiles.Star(card.Shelf, name))
            {
                card.Starred = true;
                Library.SavedCount++;
            }
            else
            {
                Library.Failed(Loc.T("Library.Star.Failed"));
            }
        }
        else if (LibraryFiles.IsLastCopy(name))
        {
            ReplayLibraryViewModel.Ask(card, Loc.T("Library.Star.ConfirmLast"), () => Unstar(card, name));
        }
        else
        {
            Unstar(card, name);
        }
    }

    private void Unstar(LibraryCard card, string name)
    {
        var last = LibraryFiles.IsLastCopy(name);
        if (!LibraryFiles.Unstar(name))
        {
            return;
        }

        card.Starred = false;
        card.HasReplay = !last;
        Library.SavedCount = Math.Max(0, Library.SavedCount - 1);
        if (Library.IsSaved)
        {
            Library.Remove(card);
        }
    }

    internal void DeleteImported(LibraryCard card, string name)
    {
        if (LibraryFiles.DeleteImported(name))
        {
            Library.Remove(card);
            Library.ImportedCount = Math.Max(0, Library.ImportedCount - 1);
        }
    }

    internal void ShareLibraryCard(LibraryCard card, string name)
    {
        _libraryAction = ShareAsync(card.Shelf, name);
        _libraryAction.Forget("Replay share");
    }

    private async Task ShareAsync(ReplayShelf shelf, string name)
    {
        var cards = _cards;
        var files = LibraryFiles;
        var path = await Task.Run(() => files.ShareCopy(shelf, name, cards)).ConfigureAwait(true);
        if (path is null)
        {
            Library.Failed(Loc.T("Library.ShareFailed"));
        }
        else
        {
            Library.RequestShare(path);
        }
    }

    internal void ImportLibraryFile(string path)
    {
        _libraryAction = ImportAsync(path);
        _libraryAction.Forget("Replay import");
    }

    /// <summary>Ends when the library's last load, import and share have (tests wait on it).</summary>
    internal async Task LibraryIdleAsync()
    {
        while (true)
        {
            var (load, action) = (_libraryTask, _libraryAction);
            await Task.WhenAll(load, action).ConfigureAwait(true);
            if (load == _libraryTask && action == _libraryAction)
            {
                return;
            }
        }
    }

    private async Task ImportAsync(string path)
    {
        var cards = _cards;
        var files = LibraryFiles;
        ReplayImportResult result;
        try
        {
            result = await Task.Run(() => files.Import(path, cards)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // Anything the import did not expect: the player gets "could not be read", the log the reason.
            Diag.Error("Replay import failed", ex);
            result = ReplayImportResult.Refused(ReplayImportError.Unreadable);
        }

        Library.ImportFinished();
        if (!result.Ok)
        {
            Library.Failed(Loc.F("Library.ImportError", Loc.T("Library.ImportError." + result.Error)));
            return;
        }

        var text = result.Added.Count > 0
            ? result.FromLog ? Loc.P("Library.Imported.Log", result.Added.Count) : Loc.T("Library.Imported.One")
            : result.Already > 0 ? Loc.T("Library.Imported.Already") : Loc.T("Library.Imported.None");
        Library.ShowTab("Imported");
        ShowLibraryTab(select: result.Added.FirstOrDefault());
        Library.Succeeded(text);
    }

    /// <summary>
    /// Shows a History game's card selected (from a deck page): Recent tab, the game added on top when it is older than
    /// the cards listed. Null when there is no such game.
    /// </summary>
    public LibraryCard? SelectLibraryCard(string key)
    {
        if (!Library.IsRecent)
        {
            Library.ShowTab("Recent");
            ShowLibraryTab();
        }

        var card = Library.Cards.FirstOrDefault(c => c.HistoryKey == key);
        if (card is null && _history.Game(key) is { } game)
        {
            card = LibraryCardOf(game, ReplayShelf.Recent, game.Replay, game.Key);
            Library.SetCards([card, .. Library.Cards]);
            FillRecentAsync(_libraryLoad, [card]).Forget("Library headers");
        }

        Library.Select(card);
        return card;
    }
}
