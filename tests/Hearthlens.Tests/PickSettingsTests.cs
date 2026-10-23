using System.Text;
using Hearthlens.Core;
using Hearthlens.Core.Battlegrounds;
using Hearthlens.Core.Cards;
using Hearthlens.Core.Data;
using Hearthlens.Core.Memory;
using Hearthlens.Core.Power;
using Hearthlens.Core.Tracking;

namespace Hearthlens.Tests;

/// <summary>
/// "Pick suggestions" and "Game plan comp" settings: migration of older settings files, and the advisor never picking
/// a comp by itself in "I choose" mode. Offline: small hand-made card and community data.
/// </summary>
public sealed class PickSettingsTests
{
    // ---- settings ------------------------------------------------------------------------------

    [Fact]
    public void New_settings_default_to_stats_only_and_manual_comp()
    {
        var settings = new AppSettings();

        Assert.Equal(PickHints.StatsOnly, settings.PickHints);
        Assert.Equal(CompMode.Manual, settings.CompMode);
        Assert.Null(settings.ChosenCompId);
    }

    [Fact]
    public void Old_file_with_pick_hints_switched_off_loads_as_off()
    {
        var settings = AppSettings.FromJson("""{ "OverlayScale": 1.2, "ShowHeroPick": false }""");

        Assert.Equal(PickHints.Off, settings.PickHints);
        Assert.Equal(1.2, settings.OverlayScale);
        Assert.Null(settings.ShowHeroPick);
        Assert.DoesNotContain("ShowHeroPick", settings.ToJson());
    }

    [Fact]
    public void Old_file_with_pick_hints_switched_on_loads_as_stats_only()
    {
        var settings = AppSettings.FromJson("""{ "ShowHeroPick": true }""");

        Assert.Equal(PickHints.StatsOnly, settings.PickHints);
        Assert.Equal(CompMode.Manual, settings.CompMode);
        Assert.DoesNotContain("ShowHeroPick", settings.ToJson());
    }

    [Fact]
    public void File_without_any_pick_setting_loads_defaults()
    {
        var settings = AppSettings.FromJson("""{ "OverlayEnabled": false }""");

        Assert.False(settings.OverlayEnabled);
        Assert.Equal(PickHints.StatsOnly, settings.PickHints);
        Assert.Equal(CompMode.Manual, settings.CompMode);
        Assert.Null(settings.ChosenCompId);
    }

    [Theory]
    [InlineData(PickHints.Highlight, CompMode.Auto, null)]
    [InlineData(PickHints.StatsOnly, CompMode.Manual, "comp-123")]
    [InlineData(PickHints.Off, CompMode.Manual, null)]
    public void Pick_settings_round_trip(PickHints hints, CompMode mode, string? chosen)
    {
        var json = new AppSettings { PickHints = hints, CompMode = mode, ChosenCompId = chosen }.ToJson();
        var loaded = AppSettings.FromJson(json);

        Assert.Equal(hints, loaded.PickHints);
        Assert.Equal(mode, loaded.CompMode);
        Assert.Equal(chosen, loaded.ChosenCompId);
        // Written by name, without the legacy switch or the radio-button helpers.
        Assert.Contains($"\"PickHints\": \"{hints}\"", json);
        Assert.Contains($"\"CompMode\": \"{mode}\"", json);
        Assert.DoesNotContain("ShowHeroPick", json);
        Assert.DoesNotContain("PickHintsHighlight", json);
        Assert.DoesNotContain("CompModeManual", json);
    }

    [Fact]
    public void Pick_setting_in_the_file_is_kept_next_to_a_legacy_switch_left_on()
    {
        var settings = AppSettings.FromJson("""{ "PickHints": "Highlight", "ShowHeroPick": true }""");

        Assert.Equal(PickHints.Highlight, settings.PickHints);
    }

    [Fact]
    public void Radio_button_helpers_select_one_option_and_ignore_unchecks()
    {
        var settings = new AppSettings();

        settings.PickHintsHighlight = true;
        Assert.Equal(PickHints.Highlight, settings.PickHints);
        Assert.True(settings.PickHintsHighlight);
        Assert.False(settings.PickHintsStatsOnly);

        // The radio group unchecks the old option after checking the new one.
        settings.PickHintsOff = true;
        settings.PickHintsHighlight = false;
        Assert.Equal(PickHints.Off, settings.PickHints);

        settings.CompModeAuto = true;
        settings.CompModeManual = false;
        Assert.Equal(CompMode.Auto, settings.CompMode);
        settings.CompModeManual = true;
        Assert.Equal(CompMode.Manual, settings.CompMode);
    }

    // ---- advisor -------------------------------------------------------------------------------

    private const string Hero = "TEST_HERO";

    private static readonly string[] FullLobby = ["BEAST", "MURLOC", "DEMON", "PIRATE", "MECHANICAL"];

    [Fact]
    public void Manual_mode_never_picks_a_comp_by_itself()
    {
        var (advisor, cards) = Advisor(CompMode.Manual);

        // Two beast core cards on board: Auto would aim for the beast comp, Manual must not.
        for (var turn = 3; turn <= 6; turn++)
        {
            var view = advisor.Enrich(View(FullLobby, turn), null, cards);

            Assert.NotEmpty(view.Comps);
            Assert.DoesNotContain(view.Comps, c => c.IsTarget || c.IsPinned);
            // No comp, so no comp bonus or comp tag on any tavern minion.
            Assert.DoesNotContain(view.ShopAdvice, s => s.Reason.Contains("Core") || s.Reason.Contains("Fits tribe"));
        }

        Assert.Null(advisor.PinnedCompId);
    }

    [Fact]
    public void Manual_mode_aims_only_for_the_chosen_comp()
    {
        var (advisor, cards) = Advisor(CompMode.Manual);
        advisor.PinnedCompId = "murlocs";

        var view = advisor.Enrich(View(FullLobby, 5), null, cards);

        var target = Assert.Single(view.Comps, c => c.IsTarget);
        Assert.Equal("murlocs", target.Id);
        Assert.True(target.IsPinned);
        Assert.StartsWith("Your choice", target.Why);
        // The tavern is scored for the chosen comp, even with beast core cards on board.
        var murloc = Assert.Single(view.ShopAdvice, s => s.CardId == "MURLOC_CORE");
        Assert.Contains("Core", murloc.Reason);
        Assert.True(murloc.Best);
    }

    [Fact]
    public void Manual_choice_missing_from_the_lobby_leaves_no_target_and_is_kept()
    {
        var (advisor, cards) = Advisor(CompMode.Manual);
        advisor.PinnedCompId = "demons";

        var view = advisor.Enrich(View(["BEAST", "MURLOC", "PIRATE", "MECHANICAL", "NAGA"], 5), null, cards);

        Assert.NotEmpty(view.Comps);
        Assert.DoesNotContain(view.Comps, c => c.IsTarget);
        Assert.DoesNotContain(view.ShopAdvice, s => s.Reason.Contains("Core"));
        Assert.Equal("demons", advisor.PinnedCompId);

        // Next lobby has demons: the stored choice is the target again.
        var next = advisor.Enrich(View(FullLobby, 5), null, cards);
        Assert.Equal("demons", Assert.Single(next.Comps, c => c.IsTarget).Id);
    }

    [Fact]
    public void Auto_mode_still_picks_the_comp_the_player_started_and_a_lock_overrides_it()
    {
        var (advisor, cards) = Advisor(CompMode.Auto);

        var view = advisor.Enrich(View(FullLobby, 5), null, cards);
        var target = Assert.Single(view.Comps, c => c.IsTarget);
        Assert.Equal("beasts", target.Id);
        Assert.False(target.IsPinned);

        advisor.PinnedCompId = "murlocs";
        var locked = advisor.Enrich(View(FullLobby, 6), null, cards);
        var lockedTarget = Assert.Single(locked.Comps, c => c.IsTarget);
        Assert.Equal("murlocs", lockedTarget.Id);
        Assert.StartsWith("Locked by you", lockedTarget.Why);
    }

    [Fact]
    public void Manual_choice_survives_a_new_game_and_an_auto_lock_does_not()
    {
        var db = Path.Combine(Path.GetTempPath(), $"hl-test-{Guid.NewGuid():N}.db");
        try
        {
            CompChoiceAcrossGames(db);
        }
        finally
        {
            try
            {
                File.Delete(db);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A pooled SQLite connection can still hold the file on Windows; it is only a temp file.
            }
        }
    }

    private static void CompChoiceAcrossGames(string db)
    {
        using var memory = new HearthstoneMemory();
        var session = new GameSession(new HistoryStore(db), memory);

        session.SetCompMode(CompMode.Manual, "murlocs");
        Assert.Equal("murlocs", session.Advisor.PinnedCompId);
        session.OnGameStarted(new GameState());
        Assert.Equal("murlocs", session.Advisor.PinnedCompId);

        // Switching to Auto lets the advisor choose; a lock then lasts one game.
        session.SetCompMode(CompMode.Auto, "murlocs");
        Assert.Equal(CompMode.Auto, session.Advisor.CompMode);
        Assert.Null(session.Advisor.PinnedCompId);
        session.PinComp("beasts");
        session.SetCompMode(CompMode.Auto, "murlocs");
        Assert.Equal("beasts", session.Advisor.PinnedCompId);
        session.OnGameStarted(new GameState());
        Assert.Null(session.Advisor.PinnedCompId);

        // Back to Manual: the stored choice returns.
        session.SetCompMode(CompMode.Manual, "murlocs");
        Assert.Equal("murlocs", session.Advisor.PinnedCompId);
    }

    [Fact]
    public void Comps_follow_the_cards_on_your_board()
    {
        var (advisor, cards) = Advisor(CompMode.Manual);
        var comps = advisor.Enrich(View(["BEAST", "MURLOC", "DEMON"], 6), null, cards).Comps;

        // Both beast core cards are on the board, so beasts lead although murlocs place better on average.
        Assert.Equal("beasts", comps[0].Id);
    }

    [Fact]
    public void A_chosen_tribe_puts_its_comps_first()
    {
        var (advisor, cards) = Advisor(CompMode.Manual);
        advisor.CompFocus = "DEMON";
        var comps = advisor.Enrich(View(["BEAST", "MURLOC", "DEMON"], 6), null, cards).Comps.Select(c => c.Id).ToList();
        Assert.True(comps.IndexOf("demons") < comps.IndexOf("murlocs"), string.Join(", ", comps));

        // A tribe that is not in this lobby changes nothing.
        advisor.CompFocus = "PIRATE";
        var plain = advisor.Enrich(View(["BEAST", "MURLOC", "DEMON"], 6), null, cards).Comps.Select(c => c.Id).ToList();
        Assert.True(plain.IndexOf("murlocs") < plain.IndexOf("demons"), string.Join(", ", plain));
    }

    private static (BattlegroundsAdvisor Advisor, CardDb Cards) Advisor(CompMode mode)
    {
        const string json = """
            [
              { "id": "TEST_HERO", "dbfId": 1, "name": "Test Hero", "type": "HERO", "battlegroundsHero": true },
              { "id": "BEAST_CORE", "dbfId": 10, "name": "Beast Core", "type": "MINION", "races": ["BEAST"], "techLevel": 2, "isBattlegroundsPoolMinion": true },
              { "id": "BEAST_CORE_2", "dbfId": 11, "name": "Beast Core Two", "type": "MINION", "races": ["BEAST"], "techLevel": 3, "isBattlegroundsPoolMinion": true },
              { "id": "MURLOC_CORE", "dbfId": 20, "name": "Murloc Core", "type": "MINION", "races": ["MURLOC"], "techLevel": 2, "isBattlegroundsPoolMinion": true },
              { "id": "MURLOC_CORE_2", "dbfId": 21, "name": "Murloc Core Two", "type": "MINION", "races": ["MURLOC"], "techLevel": 3, "isBattlegroundsPoolMinion": true },
              { "id": "DEMON_CORE", "dbfId": 30, "name": "Demon Core", "type": "MINION", "races": ["DEMON"], "techLevel": 2, "isBattlegroundsPoolMinion": true }
            ]
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var cards = CardDb.Parse("enUS", stream);

        var meta = new BattlegroundsMeta(
            new Dictionary<int, IReadOnlyDictionary<string, MetaHero>> { [100] = new Dictionary<string, MetaHero>() },
            [],
            [
                Comp("beasts", "Beasts", "BEAST", 4.0, "BEAST_CORE", "BEAST_CORE_2"),
                Comp("murlocs", "Murlocs", "MURLOC", 3.8, "MURLOC_CORE", "MURLOC_CORE_2"),
                Comp("demons", "Demons", "DEMON", 4.2, "DEMON_CORE"),
            ],
            new Dictionary<string, MetaTrinket>(),
            new Dictionary<int, IReadOnlyDictionary<string, MetaCard>>(),
            new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));

        return (new BattlegroundsAdvisor { Meta = meta, CompMode = mode }, cards);
    }

    private static MetaComp Comp(string id, string name, string tribe, double placement, params string[] core) => new(
        id,
        name,
        "A",
        "Easy",
        [tribe],
        core.Select(c => new MetaCompCard(c, "CORE")).ToList(),
        null,
        null,
        placement,
        new Dictionary<int, double>(),
        new Dictionary<string, (int DataPoints, double Placement)>());

    /// <summary>Recruit phase with two beast core cards on board and a murloc and a demon core card in the tavern.</summary>
    private static BattlegroundsView View(IReadOnlyList<string> races, int turn) => new(
        Turn: turn,
        TechLevel: 3,
        MaxTechLevel: 6,
        HeroCardId: Hero,
        Health: 30,
        Armor: 0,
        Place: 1,
        IsDuos: false,
        InCombat: false,
        IsHeroSelection: false,
        HeroOptions: [],
        Races: races,
        RacesConfirmed: true,
        Opponents: [],
        Combats: [],
        TierHistory: [],
        Board: [Minion("BEAST_CORE", 2), Minion("BEAST_CORE_2", 3)],
        AnomalyCardId: null,
        Triples: 0,
        Shop: [Minion("MURLOC_CORE", 2), Minion("DEMON_CORE", 2)],
        Hand: []);

    private static BgMinion Minion(string cardId, int tier) =>
        new(CardId: cardId, Name: cardId, Attack: 2, Health: 2, Golden: false, TechLevel: tier, Keywords: []);
}
