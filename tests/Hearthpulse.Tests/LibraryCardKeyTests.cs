using Hearthpulse.Core;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Replay;
using Hearthpulse.UI.Main;
using Hearthpulse.UI.Main.Match;

namespace Hearthpulse.Tests;

/// <summary>
/// Review m3: the keys on a focused History card, in the view model both UIs share. The views add the moves (arrows,
/// Esc); these act on the game: S star, Shift+S Share, Delete an imported game (with the question), O its page.
/// </summary>
public sealed class LibraryCardKeyTests
{
    [Theory]
    [InlineData("S", false, CardKey.Star)]
    [InlineData("S", true, CardKey.Share)]
    [InlineData("Delete", false, CardKey.Delete)]
    [InlineData("O", false, CardKey.OpenOnSite)]
    [InlineData("Delete", true, null)]
    [InlineData("O", true, null)]
    [InlineData("F", false, null)]
    [InlineData("V", false, null)]
    [InlineData("Enter", false, null)]
    public void Each_key_has_one_action_on_a_card(string key, bool shift, CardKey? action) =>
        Assert.Equal(action, ReplayLibraryViewModel.CardKeyFor(key, shift));

    [Fact]
    public void Delete_asks_first_and_only_an_imported_game_is_deleted()
    {
        var library = Library();
        var imported = new LibraryCard(ReplayShelf.Imported, "a.hpr", "a", battlegrounds: false);
        var recent = new LibraryCard(ReplayShelf.Recent, "b.hpr", "b", battlegrounds: false);

        Assert.True(library.HandleCardKey(imported, "Delete", shift: false));
        Assert.True(imported.Confirming);
        Assert.Equal(Hearthpulse.Core.Localization.Loc.T("Library.ConfirmDelete"), imported.ConfirmText);

        Assert.False(library.HandleCardKey(recent, "Delete", shift: false));
        Assert.False(recent.Confirming);
    }

    [Fact]
    public void O_opens_the_page_of_an_uploaded_game_and_does_nothing_on_another()
    {
        var library = Library();
        var opened = new List<Uri>();
        library.OpenBrowser = url =>
        {
            opened.Add(url);
            return true;
        };
        var uploaded = new LibraryCard(ReplayShelf.Recent, "a.hpr", "a", battlegrounds: false) { SiteUrl = "https://hearthpulse.net/replay/abc" };
        var local = new LibraryCard(ReplayShelf.Recent, "b.hpr", "b", battlegrounds: false);

        Assert.True(library.HandleCardKey(uploaded, "O", shift: false));
        Assert.False(library.HandleCardKey(local, "O", shift: false));
        Assert.Equal([new Uri("https://hearthpulse.net/replay/abc")], opened);
    }

    [Fact]
    public void Star_and_share_need_a_replay_and_other_keys_go_on()
    {
        var library = Library();
        var noReplay = new LibraryCard(ReplayShelf.Recent, null, "c", battlegrounds: false);

        Assert.False(library.HandleCardKey(noReplay, "S", shift: false));
        Assert.False(library.HandleCardKey(noReplay, "S", shift: true));
        Assert.False(library.HandleCardKey(noReplay, "F", shift: false));
    }

    private static ReplayLibraryViewModel Library() =>
        new MainViewModel(new HistoryStore(Path.Combine(AppPaths.Root, $"hp-card-keys-{Guid.NewGuid():N}.db")), new AppSettings(), TestDataFolder.App).Library;
}
