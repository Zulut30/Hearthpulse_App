using Hearthpulse.Core;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Constructed;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Hosting;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Replay;
using Hearthpulse.Core.Stats;
using Hearthpulse.Core.Tracking;
using Hearthpulse.UI.Main;
using Hearthpulse.UI.Overlay;

namespace Hearthpulse.Tests;

/// <summary>
/// What both hosts used to keep themselves and now report into the main view model (<c>MainViewModel.Host.cs</c>): the
/// busy line and the update status follow a language switch, and the log import marks the history as imported.
/// </summary>
[Collection(nameof(GlobalLanguage))]
public sealed class MainViewModelHostTests : IDisposable
{
    private readonly string _db = TestDataFolder.Scratch("hp-host-vm") + ".db";
    private readonly HistoryStore _history;

    public MainViewModelHostTests()
    {
        _history = new HistoryStore(_db);
        Language(Loc.English);
    }

    public void Dispose()
    {
        Language(Loc.English);
        TestDataFolder.TryDelete(_db);
    }

    [Fact]
    public void The_busy_line_and_the_update_status_are_shown_again_in_the_new_language()
    {
        var vm = new MainViewModel(_history, new AppSettings(), TestDataFolder.App);
        vm.ShowBusy(() => Loc.T("Host.Busy.NothingToImport"));
        vm.SetUpdateStatus(() => Loc.T("Host.Update.Off"));
        Assert.Equal("Hearthstone install not found — nothing to import.", vm.Busy);
        Assert.Equal("Automatic updates are off.", vm.UpdateStatus);

        Language(Loc.Russian);

        Assert.Equal(Loc.T("Host.Busy.NothingToImport"), vm.Busy);
        Assert.StartsWith("Hearthstone не найден", vm.Busy, StringComparison.Ordinal);
        Assert.Equal("Автообновление выключено.", vm.UpdateStatus);
    }

    [Fact]
    public void A_cleared_busy_line_stays_clear_and_a_ready_update_replaces_the_status()
    {
        var vm = new MainViewModel(_history, new AppSettings(), TestDataFolder.App);
        vm.ShowBusy(() => Loc.T("Host.Busy.Importing"));
        vm.ShowBusy(null);
        vm.SetUpdateStatus(() => Loc.T("Host.Update.Off"));
        vm.SetUpdateReady("9.9.9");

        Language(Loc.Russian);

        Assert.Equal(string.Empty, vm.Busy);
        Assert.DoesNotContain("Автообновление", vm.UpdateStatus, StringComparison.Ordinal);
        Assert.Contains("9.9.9", vm.UpdateStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_the_game_folder_nothing_is_imported_and_the_flags_stay()
    {
        var settings = new AppSettings();
        var vm = new MainViewModel(_history, settings, TestDataFolder.App);

        await vm.ImportLogsAsync(null, force: true);

        Assert.Equal(Loc.T("Host.Busy.NothingToImport"), vm.Busy);
        Assert.False(settings.HistoryImported);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_import_marks_the_history_imported_and_says_so_when_asked_or_games_were_added(bool force)
    {
        // An empty game folder: no log sessions, so no progress report can arrive after the result.
        var install = TestDataFolder.Scratch("hp-host-install");
        Directory.CreateDirectory(install);
        try
        {
            var settings = new AppSettings();
            var vm = new MainViewModel(_history, settings, TestDataFolder.App);

            await vm.ImportLogsAsync(install, force);

            Assert.Equal((true, true, true), (settings.HistoryImported, settings.HistoryStatsFilled, settings.HistoryCardsFilled));
            // "Import again" always reports; the import at start says nothing when no games were added.
            Assert.Equal(force ? Loc.F("Host.Busy.Imported", 0, 0) : string.Empty, vm.Busy);
        }
        finally
        {
            TestDataFolder.TryDelete(install);
        }
    }

    [Fact]
    public void The_archetype_names_passed_in_label_a_saved_archetype_and_none_passed_keeps_the_built_in_label()
    {
        // What ArchetypeTranslations.Current used to supply: now the loader hands the names to the view models, which
        // pass them on (here DeckDetails, as the deck page does).
        var names = ArchetypeTranslations.Parse("""{"items": [{"name_en": "Quiet Test Mage", "name_ru": "Тихий тестовый маг"}]}""");
        var game = new GameRecord { Key = "quiet-test", OpponentArchetype = "Quiet Test Mage", OpponentClass = "MAGE", Format = "FT_STANDARD" };
        Language(Loc.Russian);

        Assert.Equal("Тихий тестовый маг", DeckDetails.OpponentArchetype(game, null, names));
        Assert.NotEqual("Тихий тестовый маг", DeckDetails.OpponentArchetype(game, null));
        Assert.Same(ArchetypeTranslations.Empty, new MainViewModel(_history, new AppSettings(), TestDataFolder.App).ArchetypeNames);
    }

    [Fact]
    public void The_data_folder_and_the_library_come_from_the_data_passed_in()
    {
        // What AppPaths.Root, ReplayLibrary.Default and ReplayUploads.Default used to supply (Q3b).
        var root = TestDataFolder.Scratch("hp-host-data");
        var replays = new ReplayStore(Path.Combine(root, "replays"));
        var data = new AppData(root, replays, new ReplayLibrary(replays, Path.Combine(root, "saved"), Path.Combine(root, "imported")),
            new ReplayUploads(Path.Combine(root, "uploads.json")));

        var vm = new MainViewModel(_history, new AppSettings(), data);

        Assert.Equal(root, vm.DataFolder);
        Assert.Same(data.Library, vm.LibraryFiles);
        Assert.Same(data.Uploads, vm.LibraryUploads);
    }

    [Fact]
    public void In_russian_the_overlay_names_the_opponent_s_archetype_from_the_names_it_was_given()
    {
        var overlay = new OverlayViewModel { ArchetypeNames = QuietMage() };
        var odds = new OpponentArchetypeView([new ArchetypeChance("Quiet Test Mage", "Quiet Test Mage", 0.8, 0.5, 100)], [], 3, "test");
        var view = new ConstructedView(5, null, false, "HERO_09", "PRIEST", "HERO_08", "MAGE", null, [], 20, 4, [], [], 18, 5, 0, null, null)
        {
            OpponentArchetype = odds,
        };
        Language(Loc.Russian);

        overlay.Update(LiveSnapshot.Empty with { Constructed = view, InGame = true, HearthstoneRunning = true }, CardDb.Empty,
            new AppSettings { ShowOpponentArchetype = true }, null, null);

        Assert.Equal("Тихий тестовый маг", overlay.ArchetypeName);
    }

    [Fact]
    public async Task In_russian_a_library_card_names_the_archetype_its_replay_saved_from_the_names_the_main_view_model_was_given()
    {
        // History lists read games without the card columns: a card shows the archetype the replay file's header saved
        // (an Imported or Saved file), labelled with the names the main view model holds.
        var root = TestDataFolder.Scratch("hp-host-library");
        var files = new ReplayLibrary(new ReplayStore(Path.Combine(root, "replays")), Path.Combine(root, "saved"), Path.Combine(root, "imported"));
        Directory.CreateDirectory(files.Imported.Directory);
        ReplayImportTests.Write(Path.Combine(files.Imported.Directory, "20261028184107-quietmage001.hpr"),
            ReplayImportTests.Constructed(summary: ReplayImportTests.ConstructedSummary with { OpponentArchetype = "Quiet Test Mage" }));
        try
        {
            Language(Loc.Russian);
            var vm = new MainViewModel(_history, new AppSettings(), TestDataFolder.App) { LibraryFiles = files, ArchetypeNames = QuietMage() };
            await vm.RefreshAsync();
            await vm.LibraryIdleAsync();

            vm.Library.SetTab.Execute("Imported");
            await vm.LibraryIdleAsync();

            Assert.Equal("Тихий тестовый маг", Assert.Single(vm.Library.Cards).OpponentArchetype);
        }
        finally
        {
            TestDataFolder.TryDelete(root);
        }
    }

    private static ArchetypeTranslations QuietMage() =>
        ArchetypeTranslations.Parse("""{"items": [{"name_en": "Quiet Test Mage", "name_ru": "Тихий тестовый маг"}]}""");

    /// <summary>
    /// Switches the language on the headless UI thread: views the Avalonia binding tests leave alive follow every
    /// switch, and they may only be touched from that thread.
    /// </summary>
    private static void Language(string language) => LocBindingTests.Headless.Run(() => Loc.Instance.SetLanguage(language));
}
