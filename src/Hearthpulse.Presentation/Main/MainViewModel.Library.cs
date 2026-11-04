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

    /// <summary>The library's folders (from the constructor's data folder); tests give their own.</summary>
    internal ReplayLibrary LibraryFiles { get; init; }

    /// <summary>Replays uploaded to the site, by file name (plan 2.2).</summary>
    internal ReplayUploads LibraryUploads { get; init; }

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

    /// <summary>The headers of the Recent cards' files, off the UI thread: final board and chips, whether a file is left, the stars.</summary>
    private async Task FillRecentAsync(int load, IReadOnlyList<LibraryCard> cards)
    {
        var files = LibraryFiles;
        var uploads = LibraryUploads;
        var read = await Task.Run(() =>
        {
            var saved = files.SavedNames();
            var headers = cards.Select(c => c.FileName is { } name
                ? files.Header(ReplayShelf.Recent, name) ?? (saved.Contains(name) ? files.Header(ReplayShelf.Saved, name) : null)
                : null).ToList();
            PruneBookmarks(files);
            return (Saved: saved, Headers: headers, Imported: files.Imported.Names().Count, Uploads: uploads.All());
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
            card.SiteUrl = SiteUrlOf(read.Uploads, card);
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
        var cardDb = _cards;
        var uploads = LibraryUploads;
        var read = await Task.Run(() =>
        {
            var saved = files.SavedNames();
            var names = shelf == ReplayShelf.Saved ? saved.ToList() : files.Imported.Names().ToList();
            // A file of these folders may have been put there by hand: its card shows only what the import filter keeps.
            var headers = names.Select(n => files.CheckedHeader(shelf, n, cardDb)).ToList();
            return (Saved: saved, Names: names, Headers: headers, Imported: shelf == ReplayShelf.Imported ? names.Count : files.Imported.Names().Count,
                Uploads: uploads.All());
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
            var (header, readable) = read.Headers[i];
            if (!readable)
            {
                // Refused by the filter: shown so that it can be deleted (Imported) or unstarred (Saved), never as a game.
                var damaged = new LibraryCard(shelf, name, null, battlegrounds: false)
                {
                    Title = Loc.T("Library.Damaged"),
                    Caption = name,
                    SortKey = name,
                };
                damaged.HasReplay = shelf == ReplayShelf.Saved;
                damaged.Starred = read.Saved.Contains(name);
                cards.Add(damaged);
                continue;
            }

            var row = shelf == ReplayShelf.Saved ? rows.GetValueOrDefault(name) : null;
            var game = row ?? Core.Replay.MatchPage.RecordOf(header, name);
            if (!FilterMatch(game, HistoryFilter))
            {
                continue;
            }

            var card = LibraryCardOf(game, shelf, name, row?.Key);
            card.Starred = read.Saved.Contains(name);
            card.SiteUrl = SiteUrlOf(read.Uploads, card);
            if (header?.Summary is { } summary)
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
                // The hero portrait and the board already say "Battlegrounds": only Duos (and any other mode) names itself,
                // so the tier and the turns fit the caption.
                Caption = g.Mode == "Battlegrounds" ? Loc.P("Main.Game.BgDetail", g.Turns, g.TavernTier)
                    : $"{Mode(g.Mode)} · {Loc.P("Main.Game.BgDetail", g.Turns, g.TavernTier)}",
                ModeText = Mode(g.Mode),
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
            ModeText = Mode(g.Mode),
            OpponentHeroCardId = g.OpponentHeroCardId,
            Opponent = g.OpponentClass is { Length: > 0 } opponent ? Loc.F("Main.Game.Vs", OverlayViewModel.ClassName(opponent)) : string.Empty,
            OpponentArchetype = g.OpponentArchetype is { Length: > 0 } archetype ? ArchetypeNames.Display(archetype) ?? archetype : string.Empty,
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

    /// <summary>A Battlegrounds card's parts from its replay's summary: hero powers and trinkets, the final board.</summary>
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
            }).ToList());
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

            // Saved and Imported files are opened through the import filter (MatchPage.LoadFile).
            var name = rest[(slash + 1)..];
            return Core.Replay.MatchPage.LoadFile(LibraryFiles.Load(shelf, name), name, cards, MetaOf, ArchetypeNames);
        }

        return _history.Game(key) is { } game
            ? Core.Replay.MatchPage.Load(game, LibraryFiles.Load(ReplayShelf.Recent, game.Replay), cards, MetaOf(game), ArchetypeNames)
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
        if (last)
        {
            ForgetBookmarks(name);
        }
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
            ForgetBookmarks(name);
            Library.Remove(card);
            Library.ImportedCount = Math.Max(0, Library.ImportedCount - 1);
        }
    }

    /// <summary>The replay's last file is gone: its bookmarks go too (off the UI thread).</summary>
    private void ForgetBookmarks(string name)
    {
        var history = _history;
        Task.Run(() => history.RemoveBookmarks(name)).Forget("Replay bookmarks");
    }

    /// <summary>
    /// Bookmarks of replays with no file left on any shelf (History's trim, a file deleted by hand) are taken off; on
    /// the pool thread while the library loads.
    /// </summary>
    private void PruneBookmarks(ReplayLibrary files)
    {
        try
        {
            foreach (var name in _history.BookmarkedReplays()
                .Where(n => !files.Recent.Contains(n) && !files.Saved.Contains(n) && !files.Imported.Contains(n)))
            {
                _history.RemoveBookmarks(name);
            }
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or System.IO.IOException or UnauthorizedAccessException)
        {
            Diag.Error("Tidying replay bookmarks failed", ex);
        }
    }

    /// <summary>The card's link on the site, from the uploads read once for the tab (only own games are uploaded).</summary>
    private static string SiteUrlOf(IReadOnlyDictionary<string, ReplayUploadResult> uploads, LibraryCard card) =>
        card is { CanUpload: true, FileName: { } name } && uploads.GetValueOrDefault(ReplayUploads.KeyOf(card.Shelf, name)) is { } upload
            ? upload.Url
            : string.Empty;

    /// <summary>
    /// Uploads the card's replay to the site (plan 2.2), after the confirmation in the card: the Share copy without the
    /// post-game part (<see cref="ReplayUpload"/>), with the account's sign-in. The link goes to the clipboard. While it
    /// runs the card's button cancels it; a second upload cannot start.
    /// </summary>
    internal void UploadLibraryCard(LibraryCard card, string name)
    {
        if (card.SiteWork != SiteWork.None)
        {
            return;
        }

        _libraryAction = UploadAsync(card, name);
        _libraryAction.Forget("Replay upload");
    }

    private async Task UploadAsync(LibraryCard card, string name)
    {
        if (!card.CanUpload || card.SiteWork != SiteWork.None)
        {
            return;
        }

        if (_account is not { SignedIn: true } account)
        {
            Library.Failed(Loc.T("Library.Site.SignIn"));
            return;
        }

        using var cancel = new CancellationTokenSource();
        card.SiteCancel = cancel;
        card.SiteWork = SiteWork.Uploading;
        try
        {
            var cards = _cards;
            var files = LibraryFiles;
            var uploads = LibraryUploads;
            var shelf = card.Shelf;
            var body = await Task.Run(() => files.Load(shelf, name) is { } text && ReplayUpload.Prepare(text, cards) is { } upload
                ? ReplayUpload.Encode(upload)
                : null, cancel.Token).ConfigureAwait(true);
            if (body is null)
            {
                Library.Failed(Loc.T("Library.ShareFailed"));
                return;
            }

            // The game's profile id links the replay in the site's «Последние игры»: only for a game the profile upload
            // sends, while it is on - the site then already has the game (the id is derived from its start time).
            var gameId = Settings.UploadGamesToProfile && card.HistoryKey is { } key
                && _libraryGames.Concat(_recentGames ?? []).FirstOrDefault(g => g.Key == key) is { } record
                ? Core.Accounts.ProfileEvents.ProfileGameId(record)
                : null;
            var (result, error) = await account.UploadReplayAsync(body, gameId, cancel.Token).ConfigureAwait(true);
            if (result is null)
            {
                Library.Failed(Loc.Or("Library.Site.Error." + error, Loc.T("Library.Site.Error.SERVER_ERROR")));
                return;
            }

            await Task.Run(() => uploads.Set(ReplayUploads.KeyOf(shelf, name), result), CancellationToken.None).ConfigureAwait(true);
            card.SiteUrl = result.Url;
            Library.Uploaded(result.Url);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            Library.Failed(Loc.T("Library.Site.Cancelled"));
        }
        finally
        {
            card.SiteCancel = null;
            card.SiteWork = SiteWork.None;
        }
    }

    /// <summary>Deletes the card's replay from the site with the token its upload handed out.</summary>
    internal void RemoveLibraryCardFromSite(LibraryCard card, string name)
    {
        if (card.SiteWork != SiteWork.None)
        {
            return;
        }

        _libraryAction = RemoveFromSiteAsync(card, name);
        _libraryAction.Forget("Replay removal from the site");
    }

    private async Task RemoveFromSiteAsync(LibraryCard card, string name)
    {
        if (card.SiteWork != SiteWork.None || _account is not { } account)
        {
            return;
        }

        card.SiteWork = SiteWork.Removing;
        try
        {
            var uploads = LibraryUploads;
            var key = ReplayUploads.KeyOf(card.Shelf, name);
            if (await Task.Run(() => uploads.Get(key)).ConfigureAwait(true) is not { } upload)
            {
                card.SiteUrl = string.Empty;
                return;
            }

            var error = await account.DeleteReplayAsync(upload.Id, upload.DeleteToken).ConfigureAwait(true);
            if (error is not (null or "NOT_FOUND"))
            {
                // Still on the site: the link and the token stay, so the delete can be tried again.
                Library.Failed(Loc.Or("Library.Site.DeleteError." + error, Loc.T("Library.Site.DeleteError.SERVER_ERROR")));
                return;
            }

            await Task.Run(() => uploads.Set(key, null)).ConfigureAwait(true);
            card.SiteUrl = string.Empty;
            Library.Succeeded(Loc.T(error is null ? "Library.Site.Removed" : "Library.Site.Gone"));
        }
        finally
        {
            card.SiteWork = SiteWork.None;
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

        // The game sent here must show: the search is cleared first.
        Library.ResetSearch();
        var card = Library.AllCards.FirstOrDefault(c => c.HistoryKey == key);
        if (card is null && _history.Game(key) is { } game)
        {
            card = LibraryCardOf(game, ReplayShelf.Recent, game.Replay, game.Key);
            Library.SetCards([card, .. Library.AllCards]);
            FillRecentAsync(_libraryLoad, [card]).Forget("Library headers");
        }

        Library.Select(card);
        return card;
    }
}
