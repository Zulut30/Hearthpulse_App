using System.Text;
using Hearthpulse.Core;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Discord;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Memory;
using Hearthpulse.Core.Tracking;
using Hearthpulse.UI.Main;

namespace Hearthpulse.Tests;

/// <summary>
/// What Discord shows (<c>docs/discord.md</c>, plan 3.4, AC2/AC4): the menu, Constructed with mode, format, classes and
/// turn, Battlegrounds with hero, turn and place, the Arena draft; nothing when Hearthstone is closed; no names or ranks,
/// so streamer mode changes nothing. The switch stays off and disabled without an app id.
/// </summary>
public sealed class DiscordStatusTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    private static readonly CardDb Cards = CardDb.Parse("enUS", new MemoryStream(Encoding.UTF8.GetBytes("""
        [{"id": "HERO_ME", "dbfId": 1, "name": "Reno Jackson", "cost": 0}]
        """)));

    private static LiveSnapshot Running => LiveSnapshot.Empty with { HearthstoneRunning = true, Scene = "HUB" };

    private static ConstructedView Ranked => new ConstructedView(7, "My secret deck", true, "HERO_09", "PRIEST", "HERO_08", "MAGE",
        "Rival#1234", [], 20, 5, [], [], 18, 4, 0, new RankInfo("FT_STANDARD", 5, 51, 0, 120), new RankInfo("FT_STANDARD", 2, 52, 1500, 120))
    {
        FormatType = "FT_STANDARD",
    };

    private static BattlegroundsView Bg(bool duos = false, bool heroSelection = false) =>
        new(7, 4, 6, "HERO_ME", 20, 0, 3, duos, false, heroSelection, [], [], false, [], [], [], [], null, 0, [], []);

    [Fact]
    public void Hearthstone_closed_clears_and_the_menu_has_no_timer()
    {
        Loc.Instance.SetLanguage(Loc.English);
        Assert.Null(MainViewModel.DiscordActivityFor(LiveSnapshot.Empty, Cards, Start));

        var menu = MainViewModel.DiscordActivityFor(Running, Cards, null)!;
        Assert.Equal("In the menu", menu.Details);
        Assert.Null(menu.State);
        Assert.Null(menu.Started);
        Assert.Equal("Hearthpulse, a Hearthstone tracker", menu.LargeText);
    }

    [Fact]
    public void Constructed_shows_mode_format_classes_and_turn_without_names_or_ranks()
    {
        Loc.Instance.SetLanguage(Loc.English);
        var s = Running with { InGame = true, GameType = "GT_RANKED", Constructed = Ranked };
        var game = MainViewModel.DiscordActivityFor(s, Cards, Start)!;

        Assert.Equal("Ranked · Standard", game.Details);
        Assert.Equal("Priest vs Mage · turn 7", game.State);
        Assert.Equal(Start, game.Started);
        var text = game.Details + game.State;
        Assert.DoesNotContain("Rival", text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", text, StringComparison.Ordinal);
        Assert.DoesNotContain("1500", text, StringComparison.Ordinal);

        // The mulligan (turn 0): classes only. Arena: no format.
        var mulligan = MainViewModel.DiscordActivityFor(s with { Constructed = Ranked with { Turn = 0 } }, Cards, Start)!;
        Assert.Equal("Priest vs Mage", mulligan.State);
        Assert.Equal("Arena", MainViewModel.DiscordActivityFor(s with { GameType = "GT_ARENA" }, Cards, Start)!.Details);
    }

    [Fact]
    public void Battlegrounds_shows_hero_turn_and_place_and_the_hero_choice()
    {
        Loc.Instance.SetLanguage(Loc.English);
        var s = Running with { InGame = true, GameType = "GT_BATTLEGROUNDS", Battlegrounds = Bg() };
        var game = MainViewModel.DiscordActivityFor(s, Cards, Start)!;
        Assert.Equal(Loc.T("Main.Mode.Battlegrounds"), game.Details);
        Assert.Equal("Reno Jackson · turn 7 · place 3", game.State);

        var duos = MainViewModel.DiscordActivityFor(s with { Battlegrounds = Bg(duos: true) }, Cards, Start)!;
        Assert.Equal(Loc.T("Main.Mode.Battlegrounds") + " · " + Loc.T("Main.Mode.Duos"), duos.Details);
        var choosing = MainViewModel.DiscordActivityFor(s with { Battlegrounds = Bg(heroSelection: true) }, Cards, Start)!;
        Assert.Equal(Loc.T("Main.Live.ChoosingHero"), choosing.State);
    }

    [Fact]
    public void The_arena_draft_says_drafting_without_a_timer()
    {
        Loc.Instance.SetLanguage(Loc.English);
        var draft = new Core.Arena.ArenaDraftView("MAGE", null, 3, [], false, [], Core.Arena.ArenaDeckScore.Empty, [], true);
        var s = Running with { ArenaDraft = draft };
        var shown = MainViewModel.DiscordActivityFor(s, Cards, Start)!;
        Assert.Equal("Arena", shown.Details);
        Assert.Equal("Drafting", shown.State);
        Assert.Null(shown.Started);
    }

    [Fact]
    public void The_timer_starts_with_the_game_and_ends_with_it()
    {
        var clock = new DiscordGameClock();
        Assert.Null(clock.Started(Running, Start));
        var playing = Running with { InGame = true, Constructed = Ranked };
        Assert.Equal(Start, clock.Started(playing, Start));
        Assert.Equal(Start, clock.Started(playing, Start.AddMinutes(5)));
        Assert.Null(clock.Started(Running, Start.AddMinutes(9)));
    }

    [Fact]
    public async Task Without_an_app_id_the_switch_is_off_and_says_why()
    {
        Loc.Instance.SetLanguage(Loc.English);
        await using var presence = new DiscordPresence(new DiscordIpc((_, _) => Task.FromResult<Stream?>(null)), applicationId: string.Empty);
        var vm = new MainViewModel(HistoryStore.InMemory(), new AppSettings { DiscordStatus = true }, TestDataFolder.App) { Discord = presence };

        Assert.False(vm.DiscordAvailable);
        Assert.False(vm.DiscordStatus);
        Assert.False(vm.DiscordConnected);
        Assert.Equal(Loc.T("Discord.Status.NotConfigured"), vm.DiscordStatusText);
    }

    [Fact]
    public async Task On_without_the_discord_app_it_waits_and_off_hides_the_line()
    {
        Loc.Instance.SetLanguage(Loc.English);
        await using var presence = new DiscordPresence(new DiscordIpc((_, _) => Task.FromResult<Stream?>(null)), applicationId: "123");
        var settings = new AppSettings();
        var vm = new MainViewModel(HistoryStore.InMemory(), settings, TestDataFolder.App) { Discord = presence };
        Assert.True(vm.DiscordAvailable);
        Assert.False(vm.DiscordStatusShown);

        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        presence.StateChanged += state =>
        {
            if (state == DiscordState.Waiting)
            {
                waiting.TrySetResult();
            }
        };
        vm.DiscordStatus = true;
        Assert.True(settings.DiscordStatus);
        vm.Apply(Running);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(Loc.T("Discord.Status.Waiting"), vm.DiscordStatusText);

        vm.DiscordStatus = false;
        Assert.False(vm.DiscordStatusShown);
    }
}
