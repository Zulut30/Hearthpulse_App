using Hearthpulse.Core;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Replay;
using Hearthpulse.UI.Main;
using Hearthpulse.UI.Main.Match;

namespace Hearthpulse.Tests;

/// <summary>
/// The History page as the replay library (shared view model, Avalonia build): cards from History rows and replay
/// headers, the star into Saved, import into Imported, delete with a question, the mode filter on every tab.
/// </summary>
public sealed class LibraryViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"hp-library-vm-{Guid.NewGuid():N}");
    private readonly HistoryStore _history;
    private readonly ReplayLibrary _files;
    private readonly string _bgReplay;

    public LibraryViewModelTests()
    {
        Directory.CreateDirectory(_dir);
        _history = new HistoryStore(Path.Combine(_dir, "history.db"));
        _files = new ReplayLibrary(new ReplayStore(Path.Combine(_dir, "replays")), Path.Combine(_dir, "saved"), Path.Combine(_dir, "imported"));
        var start = new DateTime(2026, 10, 28, 12, 0, 0);
        _bgReplay = _files.Recent.Save("20261028090000|me#1|GT_BATTLEGROUNDS", ReplayFile.WriteBattlegrounds(
            new ReplayHeader
            {
                Summary = new ReplaySummary
                {
                    Mode = "Battlegrounds",
                    HeroCardId = "TB_BaconShop_HERO_50",
                    Placement = 2,
                    HeroPowers = ["TB_BaconShop_HP_061"],
                    Trinkets = ["BG30_MagicItem_544", "BG30_MagicItem_545", "BG30_MagicItem_546"],
                    FinalBoard = [new ReplayBgMinion("BG21_001", 5, 7, true, ["TAUNT", "DIVINE_SHIELD"])],
                    FinalHand = ["BGS_004", "BGS_039", "BGS_061", "BG_CS2_065"],
                },
            },
            new BattlegroundsReplayData(), ["T 1 30"]))!;
        _history.Add(new GameRecord
        {
            Key = "20261028090000|me#1|GT_BATTLEGROUNDS",
            StartedAt = start,
            EndedAt = start.AddMinutes(18),
            Mode = "Battlegrounds",
            PlayerName = "Me#1234",
            HeroCardId = "TB_BaconShop_HERO_50",
            Placement = 2,
            Turns = 14,
            TavernTier = 6,
            RatingBefore = 6100,
            RatingAfter = 6132,
            Replay = _bgReplay,
        });
        _history.Add(new GameRecord
        {
            Key = "20261028100000|me#1|GT_RANKED",
            StartedAt = start.AddHours(1),
            EndedAt = start.AddHours(1).AddMinutes(7),
            Mode = "Ranked",
            PlayerName = "Me#1234",
            OpponentName = "Rival#4321",
            HeroCardId = "HERO_08",
            PlayerClass = "MAGE",
            OpponentHeroCardId = "HERO_01",
            OpponentClass = "WARRIOR",
            Result = "Win",
            Turns = 9,
        });
    }

    public void Dispose()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(_dir, recursive: true);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(50);
            }
        }
    }

    private async Task<MainViewModel> Open()
    {
        var vm = new MainViewModel(_history, new AppSettings()) { LibraryFiles = _files };
        await vm.RefreshAsync();
        await vm.LibraryIdleAsync();
        return vm;
    }

    private string ImportFile()
    {
        var path = Path.Combine(_dir, "shared.hpr");
        ReplayImportTests.Write(path, ReplayImportTests.Constructed());
        return path;
    }

    [Fact]
    public async Task Recent_cards_come_from_history_with_the_board_and_hand_from_the_replay_header()
    {
        var vm = await Open();

        Assert.True(vm.Library.IsRecent);
        Assert.Equal(2, vm.Library.Cards.Count);
        var bg = vm.Library.Cards.Single(c => c.IsBattlegrounds);
        Assert.Equal("20261028090000|me#1|GT_BATTLEGROUNDS", bg.HistoryKey);
        Assert.True(bg.HasReplay);
        Assert.False(bg.Starred);
        Assert.Equal(3, bg.Chips.Count);
        Assert.Equal("+1", bg.ChipsMore);
        var minion = Assert.Single(bg.Board);
        Assert.Equal("5/7", minion.Stats);
        Assert.True(minion.Golden && minion.Taunt && minion.DivineShield);
        Assert.Equal(3, bg.Hand.Count);
        Assert.Equal("+1", bg.HandMore);
        Assert.Equal("+32", bg.Rating);
        Assert.Contains(Loc.F("Library.Minutes", 18), bg.When, StringComparison.Ordinal);

        // A game without a replay file: the card opens, but there is nothing to star or share.
        var ranked = vm.Library.Cards.Single(c => !c.IsBattlegrounds);
        Assert.False(ranked.HasReplay);
        Assert.True(ranked.NoReplay);
        Assert.Equal("HERO_01", ranked.OpponentHeroCardId);
        Assert.DoesNotContain("Rival", ranked.Opponent + ranked.Caption + ranked.Title, StringComparison.Ordinal);
        Assert.Equal(Loc.F("Main.Game.Vs", Hearthpulse.UI.Overlay.OverlayViewModel.ClassName("WARRIOR")), ranked.Opponent);
    }

    [Fact]
    public async Task The_star_copies_into_saved_and_the_last_copy_asks_before_it_goes()
    {
        var vm = await Open();
        var bg = vm.Library.Cards.Single(c => c.IsBattlegrounds);

        vm.Library.ToggleStar.Execute(bg);

        Assert.True(bg.Starred);
        Assert.Equal(1, vm.Library.SavedCount);
        Assert.True(_files.Saved.Contains(_bgReplay));

        vm.Library.SetTab.Execute("Saved");
        await vm.LibraryIdleAsync();
        var saved = Assert.Single(vm.Library.Cards);
        Assert.Equal(bg.HistoryKey, saved.HistoryKey);
        Assert.Equal(Loc.P("Library.Subtitle.Saved", 1), vm.Library.Subtitle);

        // The History file is trimmed away: the starred copy is the last one, so taking the star off asks first.
        _files.Recent.Delete(_bgReplay);
        vm.Library.ToggleStar.Execute(saved);
        Assert.True(saved.Confirming);
        Assert.Equal(Loc.T("Library.Star.ConfirmLast"), saved.ConfirmText);
        Assert.True(_files.Saved.Contains(_bgReplay));

        vm.Library.Confirm.Execute(saved);

        Assert.False(saved.Confirming);
        Assert.False(_files.Saved.Contains(_bgReplay));
        Assert.Empty(vm.Library.Cards);
        Assert.Equal(0, vm.Library.SavedCount);
    }

    [Fact]
    public async Task A_trimmed_history_game_opens_from_its_starred_copy()
    {
        var vm = await Open();
        var bg = vm.Library.Cards.Single(c => c.IsBattlegrounds);
        vm.Library.ToggleStar.Execute(bg);
        _files.Recent.Delete(_bgReplay);

        Assert.True(await vm.OpenLibraryCard(bg));

        Assert.True(vm.MatchOpen);
        Assert.Equal(string.Empty, vm.MatchPage.Missing);
    }

    [Fact]
    public async Task An_import_lands_in_imported_selected_with_a_status()
    {
        var vm = await Open();

        vm.Library.ImportPath(ImportFile());
        await vm.LibraryIdleAsync();

        Assert.True(vm.Library.IsImported);
        var card = Assert.Single(vm.Library.Cards);
        Assert.Same(card, vm.Library.SelectedCard);
        Assert.Null(card.HistoryKey);
        Assert.True(card.CanDelete);
        Assert.Equal(Loc.T("Library.Imported.One"), vm.Library.Status);
        Assert.Equal("Win", vm.Library.StatusKey);
        Assert.Equal(1, vm.Library.ImportedCount);
        // Not in History and not in the stats.
        Assert.Equal(2, _history.GameCount());

        // The imported game opens from its file, with "You" and "Opponent" for names.
        Assert.True(await vm.OpenLibraryCard(card));
        Assert.Equal(string.Empty, vm.MatchPage.Missing);
        Assert.Equal(Loc.T("Match.You"), vm.MatchPage.Header!.Local.Name);

        vm.CloseMatch();
        vm.Library.ImportPath(ImportFile());
        await vm.LibraryIdleAsync();
        Assert.Equal(Loc.T("Library.Imported.Already"), vm.Library.Status);
        Assert.Single(vm.Library.Cards);
    }

    [Fact]
    public async Task A_refused_import_says_why_and_keeps_nothing()
    {
        var vm = await Open();
        var path = Path.Combine(_dir, "bad.hpr");
        ReplayImportTests.Write(path, "HPR4\n{\"kind\":\"constructed\"}\nE 1 L PLAY 1 http://evil MINION\n");

        vm.Library.ImportPath(path);
        await vm.LibraryIdleAsync();

        Assert.Equal(Loc.F("Library.ImportError", Loc.T("Library.ImportError.Damaged")), vm.Library.Status);
        Assert.Equal("Loss", vm.Library.StatusKey);
        Assert.True(vm.Library.CanImport);
        Assert.True(vm.Library.IsRecent);
        Assert.Empty(_files.Imported.Names());

        vm.Library.Paste(null);
        Assert.Equal(Loc.F("Library.ImportError", Loc.T("Library.ImportError.Clipboard")), vm.Library.Status);

        // The next tab clears the status.
        vm.Library.SetTab.Execute("Saved");
        Assert.Equal(string.Empty, vm.Library.Status);
    }

    [Fact]
    public async Task Deleting_an_imported_game_asks_and_the_mode_filter_applies_to_imported()
    {
        var vm = await Open();
        vm.Library.ImportPath(ImportFile());
        await vm.LibraryIdleAsync();
        var card = Assert.Single(vm.Library.Cards);

        vm.SetHistoryFilter.Execute("Battlegrounds");
        await vm.LibraryIdleAsync();
        Assert.Empty(vm.Library.Cards);
        Assert.Equal(Loc.T("Library.Empty.Filter"), vm.Library.EmptyText);

        vm.SetHistoryFilter.Execute("All");
        await vm.LibraryIdleAsync();
        card = Assert.Single(vm.Library.Cards);
        vm.Library.Delete.Execute(card);
        Assert.Equal(Loc.T("Library.ConfirmDelete"), card.ConfirmText);
        vm.Library.Cancel.Execute(card);
        Assert.False(card.Confirming);
        Assert.Single(_files.Imported.Names());

        vm.Library.Delete.Execute(card);
        vm.Library.Confirm.Execute(card);

        Assert.Empty(vm.Library.Cards);
        Assert.Empty(_files.Imported.Names());
        Assert.Equal(Loc.T("Library.Empty.Imported"), vm.Library.EmptyText);
    }

    [Fact]
    public async Task Share_makes_the_neutral_copy_and_asks_the_view_for_the_clipboard()
    {
        var vm = await Open();
        string? shared = null;
        vm.Library.ShareFileRequested += path => shared = path;
        var bg = vm.Library.Cards.Single(c => c.IsBattlegrounds);

        vm.Library.Share.Execute(bg);
        await vm.LibraryIdleAsync();

        Assert.NotNull(shared);
        Assert.StartsWith("hearthpulse-replay-", Path.GetFileName(shared), StringComparison.Ordinal);
        Assert.EndsWith("-battlegrounds.hpr", shared, StringComparison.Ordinal);
        vm.Library.ShareDone(copied: true, pathOnly: true, folder: Path.GetDirectoryName(shared));
        Assert.Equal(Loc.T("Library.SharedPath"), vm.Library.Status);
        Assert.True(vm.Library.HasStatusFolder);
        vm.Library.ShareDone(copied: false);
        Assert.Equal(Loc.T("Main.Meta.ClipboardBusy"), vm.Library.Status);
    }

    [Fact]
    public async Task A_deck_page_link_selects_the_game_on_recent()
    {
        var vm = await Open();
        vm.Library.SetTab.Execute("Imported");

        var card = vm.SelectLibraryCard("20261028100000|me#1|GT_RANKED");

        Assert.True(vm.Library.IsRecent);
        Assert.NotNull(card);
        Assert.Same(card, vm.Library.SelectedCard);
    }
}
