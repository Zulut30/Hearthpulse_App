using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Replay;
using static Hearthpulse.Tests.BattlegroundsReplayTests;

namespace Hearthpulse.Tests;

/// <summary>
/// Duos (plan R3, <c>TASK-2026-11-02-duos-replay</c>): our partner's table is written as side P while it is on the screen
/// in combat, never their tavern; a file with it is <c>HPR5</c>, every other stays <c>HPR4</c>; the import keeps P only
/// in fights of a version 5 file; the match page shows it. HDT's model (our HERO_ENTITY switches to the partner's hero,
/// their minions come under our controller) and the fallback of a partner player. Synthetic lines: no real Duos log was
/// checked.
/// </summary>
public sealed class DuosReplayTests
{
    private const int Partner = 5;

    /// <summary>The Battlegrounds setup as a Duos game in HDT's model: our player and the partner's hero are team 1, no partner player.</summary>
    private static string[] HdtSetup()
    {
        var lines = Setup.ToList();
        lines.Insert(lines.IndexOf("DebugPrintPower() -         tag=HERO_ENTITY value=10") + 1, "DebugPrintPower() -         tag=BACON_DUO_TEAM_ID value=1");
        lines[lines.IndexOf("DebugPrintGame() - GameType=GT_BATTLEGROUNDS")] = "DebugPrintGame() - GameType=GT_BATTLEGROUNDS_DUO";
        return [.. lines];
    }

    /// <summary>
    /// The partner's hero made before the combat, on the leaderboard; in the combat our hero entity turns to theirs,
    /// their hero power and minion come in under our controller, the minion attacks, then our hero is ours again for the end.
    /// </summary>
    private static ReplayRecorder HdtGame() => Run([.. HdtSetup(),
        .. Entity(121, "TB_BaconShop_HERO_08", Bob, "PLAY", "HERO", "PLAYER_ID=5", "HEALTH=30", "BACON_DUO_TEAM_ID=1", "PLAYER_LEADERBOARD_PLACE=3"),
        .. CombatSetup(),
        Player("HERO_ENTITY", 121),
        .. Entity(122, "TB_BaconShop_HP_008", Me, "PLAY", "HERO_POWER"),
        .. Entity(120, "BG25_011", Me, "PLAY", "MINION", "ATK=4", "HEALTH=4", "ZONE_POSITION=1"),
        Start("ATTACK", 120), Game("PROPOSED_DEFENDER", 111), Tag(111, "DAMAGE", 1), Tag(120, "DAMAGE", 1), End,
        Player("HERO_ENTITY", 10),
        .. Fight(win: true),
        ]);

    [Fact]
    public void Hdt_model_the_partners_minions_under_our_controller_are_their_table()
    {
        var events = HdtGame().Events;
        var minion = IdOf(events, "BG25_011");
        var fight = events.SkipWhile(e => !e.StartsWith("F ", StringComparison.Ordinal)).TakeWhile(e => !e.StartsWith("f ", StringComparison.Ordinal)).ToList();

        Assert.Contains(fight, e => e.StartsWith($"E {minion} P PLAY 1 BG25_011 MINION", StringComparison.Ordinal));
        Assert.Contains(fight, e => e.StartsWith($"M ATTACK {minion} ", StringComparison.Ordinal));
        // Review m2: their permanent entities (hero made before the fight, hero power) are never P, nor ours.
        Assert.DoesNotContain(events, e => e.Contains("TB_BaconShop_HERO_08", StringComparison.Ordinal) && e.StartsWith("E ", StringComparison.Ordinal));
        Assert.DoesNotContain(events, e => e.Contains("TB_BaconShop_HP_008", StringComparison.Ordinal) && e.StartsWith("E ", StringComparison.Ordinal));
        // Our minion stays ours all fight long.
        Assert.Contains(events, e => e.StartsWith("E ", StringComparison.Ordinal) && e.Contains(" L PLAY ", StringComparison.Ordinal) && e.Contains("BG26_801", StringComparison.Ordinal));
        Assert.DoesNotContain(events, e => e.Contains(" P PLAY ", StringComparison.Ordinal) && e.Contains("BG26_801", StringComparison.Ordinal));
        var after = events.SkipWhile(e => !e.StartsWith("f ", StringComparison.Ordinal)).ToList();
        Assert.Contains(after, e => e is var x && (x == $"X {minion}" || x == $"Y {minion}"));
    }

    [Fact]
    public void Hdt_model_page_shows_the_partner_fighting_but_not_as_the_fighter_on_the_leaderboard()
    {
        var text = HdtGame().Battlegrounds.Text(new ReplayHeader { Summary = new ReplaySummary { Mode = "Duos" } }, new BattlegroundsReplayData());
        Assert.StartsWith("HPR5\n", text, StringComparison.Ordinal);
        var page = MatchPage.Load(new Core.Data.GameRecord { Key = "duos-hdt", Mode = "Duos" }, text, CardDb.Empty, null);
        var steps = Enumerable.Range(0, page.FrameCount).Select(i => page.BattlegroundsStep(i)!).ToList();

        var fighting = steps.Where(s => s.PartnerFighting).ToList();
        Assert.NotEmpty(fighting);
        Assert.All(fighting, s => Assert.Contains(s.PartnerBoard, c => c.CardId == "BG25_011"));
        // Review m1: the partner's leaderboard row is never marked as the one fighting us.
        Assert.DoesNotContain(steps.SelectMany(s => s.Leaderboard), l => l.HeroCardId == "TB_BaconShop_HERO_08" && l.Fighting);
        // Review m2: the partner's hero alone turns nothing on.
        Assert.All(steps.Where(s => s.PartnerBoard.Count == 0), s => Assert.False(s.PartnerFighting));
    }

    /// <summary>
    /// The partner fights and Bob's hero hits the partner's hero (our player's hero entity then): the fight ends, and only
    /// afterwards our hero entity is ours again.
    /// </summary>
    private static ReplayRecorder HdtPartnerHit() => Run([.. HdtSetup(),
        .. Entity(121, "TB_BaconShop_HERO_08", Bob, "PLAY", "HERO", "PLAYER_ID=5", "HEALTH=30", "BACON_DUO_TEAM_ID=1", "PLAYER_LEADERBOARD_PLACE=3"),
        .. CombatSetup(),
        Player("HERO_ENTITY", 121),
        .. Entity(120, "BG25_011", Me, "PLAY", "MINION", "ATK=4", "HEALTH=4", "ZONE_POSITION=1"),
        Start("TRIGGER", 46), Start("ATTACK", 110), Tag(121, "DAMAGE", 5), End, End,
        Tag(111, "ZONE", "REMOVEDFROMGAME"), Tag(112, "ZONE", "REMOVEDFROMGAME"), Tag(110, "ZONE", "REMOVEDFROMGAME"),
        Game("BOARD_VISUAL_STATE", 1),
        Tag(30, "ZONE", "PLAY"),
        Game("TURN", 3),
        Game("STEP", "MAIN_READY"),
        Game("BACON_IN_COMBAT_PHASE", 0),
        Player("HERO_ENTITY", 10),
        ]);

    [Fact]
    public void A_hit_on_the_partners_hero_is_not_our_loss_and_their_row_is_not_ours()
    {
        // Review M2: our hero is the one noted in the tavern, not our player's hero entity during the partner's fight.
        var events = HdtPartnerHit().Events;
        Assert.Contains("f TIE 0 0", events);
        Assert.DoesNotContain(events, e => e.StartsWith("L ", StringComparison.Ordinal) && e.Split(' ') is [_, _, "L", "TB_BaconShop_HERO_08", ..]);
        Assert.Contains(events, e => e.StartsWith("L ", StringComparison.Ordinal) && e.Split(' ') is [_, _, "L", "TB_BaconShop_HERO_50", ..]);
    }

    [Fact]
    public void A_partner_minion_stays_theirs_after_our_hero_entity_turns_back()
    {
        // Review m5: our hero entity is ours again before the fight ends; their minion moves: still P, never on our board.
        var events = Run([.. HdtSetup(),
            .. Entity(121, "TB_BaconShop_HERO_08", Bob, "PLAY", "HERO", "PLAYER_ID=5", "HEALTH=30", "BACON_DUO_TEAM_ID=1"),
            .. CombatSetup(),
            Player("HERO_ENTITY", 121),
            .. Entity(120, "BG25_011", Me, "PLAY", "MINION", "ATK=4", "HEALTH=4", "ZONE_POSITION=1"),
            Player("HERO_ENTITY", 10),
            Tag(120, "ZONE_POSITION", 2), Tag(120, "ATK", 6),
            .. Fight(win: true),
            ]).Events;
        var minion = IdOf(events, "BG25_011");
        Assert.DoesNotContain(events, e => e.StartsWith($"E {minion} L ", StringComparison.Ordinal));
        Assert.Contains(events, e => e == $"V {minion} ATK 6" || e.StartsWith($"E {minion} P PLAY 2", StringComparison.Ordinal) || e == $"Z {minion} 2");
    }

    [Theory]
    [InlineData("BACON_DUO_TEAM_ID=2")]
    [InlineData("HEALTH=30")]
    public void A_hero_of_another_team_or_without_one_is_no_partner(string tag)
    {
        // Review M1 negatives: our hero entity points at a hero not of our team (or with no team tag): their minions are not P.
        var events = Run([.. HdtSetup(),
            .. Entity(121, "TB_BaconShop_HERO_08", Bob, "PLAY", "HERO", "PLAYER_ID=5", tag),
            .. CombatSetup(),
            Player("HERO_ENTITY", 121),
            .. Entity(120, "BG25_011", Me, "PLAY", "MINION", "ATK=4", "HEALTH=4", "ZONE_POSITION=2"),
            Player("HERO_ENTITY", 10),
            .. Fight(win: true),
            ]).Events;
        Assert.DoesNotContain(events, e => e.Contains(" P PLAY ", StringComparison.Ordinal));
    }

    [Fact]
    public void The_partners_hero_outside_a_fight_turns_nothing_into_theirs()
    {
        // Review M1 negative: in the tavern our hero entity pointing at the partner's hero is no partner's fight.
        var events = Run([.. HdtSetup(),
            .. Entity(121, "TB_BaconShop_HERO_08", Bob, "PLAY", "HERO", "PLAYER_ID=5", "BACON_DUO_TEAM_ID=1"),
            Player("HERO_ENTITY", 121),
            .. Entity(120, "BG25_011", Me, "PLAY", "MINION", "ATK=4", "HEALTH=4", "ZONE_POSITION=2"),
            Player("HERO_ENTITY", 10),
            .. CombatSetup(),
            .. Fight(win: true),
            ]).Events;
        Assert.DoesNotContain(events, e => e.Contains(" P PLAY ", StringComparison.Ordinal));
    }

    [Fact]
    public void The_share_copy_of_an_hdt_game_goes_as_version_4_without_the_partner()
    {
        var text = HdtGame().Battlegrounds.Text(new ReplayHeader { Summary = new ReplaySummary { Mode = "Duos" } }, new BattlegroundsReplayData());
        var shared = ReplayUpload.Prepare(text, CardDb.Empty);
        Assert.NotNull(shared);
        Assert.StartsWith("HPR4\n", shared, StringComparison.Ordinal);
        Assert.DoesNotContain(" P PLAY ", shared, StringComparison.Ordinal);
        Assert.Equal(ReplayImportError.None, ReplayImport.Sanitize(shared!, CardDb.Empty, out var error) is null ? ReplayImportError.Damaged : error);
    }

    [Fact]
    public void A_partner_player_and_bobs_dummy_on_our_team_keep_bob_the_opponent()
    {
        // Review m7: every other player shares our team: Bob's dummy is the opponent, the partner player the partner.
        var lines = DuosSetup().ToList();
        lines.Insert(lines.IndexOf("DebugPrintPower() -         tag=BACON_DUMMY_PLAYER value=1") + 1, "DebugPrintPower() -         tag=BACON_DUO_TEAM_ID value=1");
        var game = Play([.. lines, .. CombatSetup()]).Game;
        Assert.Equal(Bob, game.OpponentPlayerId);
        Assert.Equal(Partner, game.PartnerPlayerId);
    }

    [Fact]
    public void Two_players_without_a_team_and_bobs_dummy_on_our_team_fall_back_to_the_first_player_but_us()
    {
        // Review m3: no team ids at all: the opponent is the first player but us, there is no partner.
        var solo = Play([.. Setup, .. CombatSetup()]).Game;
        Assert.Equal(Bob, solo.OpponentPlayerId);
        Assert.Equal(0, solo.PartnerPlayerId);

        // Bob's dummy player carrying our team id: still the opponent, never the partner.
        var lines = HdtSetup().ToList();
        lines.Insert(lines.IndexOf("DebugPrintPower() -         tag=BACON_DUMMY_PLAYER value=1") + 1, "DebugPrintPower() -         tag=BACON_DUO_TEAM_ID value=1");
        var dummy = Play([.. lines, .. CombatSetup()]).Game;
        Assert.Equal(Bob, dummy.OpponentPlayerId);
        Assert.Equal(0, dummy.PartnerPlayerId);
    }

    /// <summary>The Battlegrounds setup as a Duos game: we and player 5 are team 1, Bob's dummy player is not on it.</summary>
    private static string[] DuosSetup()
    {
        var lines = Setup.ToList();
        lines.Insert(lines.IndexOf("DebugPrintPower() -         tag=HERO_ENTITY value=10") + 1, "DebugPrintPower() -         tag=BACON_DUO_TEAM_ID value=1");
        var game = lines.IndexOf("DebugPrintGame() - GameType=GT_BATTLEGROUNDS");
        lines[game] = "DebugPrintGame() - GameType=GT_BATTLEGROUNDS_DUO";
        lines.InsertRange(game,
        [
            $"DebugPrintPower() -     Player EntityID=4 PlayerID={Partner} GameAccountId=[hi=144115198130930504 lo=12]",
            $"DebugPrintPower() -         tag=CONTROLLER value={Partner}",
            "DebugPrintPower() -         tag=CARDTYPE value=PLAYER",
            "DebugPrintPower() -         tag=BACON_DUO_TEAM_ID value=1",
        ]);
        lines.Insert(lines.IndexOf("DebugPrintGame() - GameType=GT_BATTLEGROUNDS_DUO") + 1, $"DebugPrintGame() - PlayerID={Partner}, PlayerName=Pal#5555");
        return [.. lines];
    }

    /// <summary>
    /// A partner minion in their tavern (never on our screen), then the combat: their table comes in, their minion attacks,
    /// the fight ends as usual.
    /// </summary>
    private static ReplayRecorder DuosGame() => Run([.. DuosSetup(),
        .. Entity(95, "BGS_100", Partner, "PLAY", "MINION", "ATK=3", "HEALTH=3", "ZONE_POSITION=1"),
        .. CombatSetup(),
        .. Entity(120, "BG25_011", Partner, "PLAY", "MINION", "ATK=4", "HEALTH=4", "ZONE_POSITION=1"),
        .. Entity(121, "TB_BaconShop_HERO_08", Partner, "PLAY", "HERO", "PLAYER_ID=5", "HEALTH=30"),
        Start("ATTACK", 120), Game("PROPOSED_DEFENDER", 111), Tag(111, "DAMAGE", 1), Tag(120, "DAMAGE", 1), End,
        .. Fight(win: true),
        ]);

    [Fact]
    public void The_partners_table_is_written_in_combat_and_leaves_with_it()
    {
        var events = DuosGame().Events;
        var minion = IdOf(events, "BG25_011");
        var fight = events.SkipWhile(e => !e.StartsWith("F ", StringComparison.Ordinal)).TakeWhile(e => !e.StartsWith("f ", StringComparison.Ordinal)).ToList();

        Assert.Contains(fight, e => e.StartsWith($"E {minion} P PLAY 1 BG25_011 MINION", StringComparison.Ordinal));
        // The fallback writes the partner player's minions only, never their hero (review m2).
        Assert.DoesNotContain(events, e => e.Contains("TB_BaconShop_HERO_08", StringComparison.Ordinal) && e.StartsWith("E ", StringComparison.Ordinal));
        Assert.Contains(fight, e => e.StartsWith($"M ATTACK {minion} ", StringComparison.Ordinal));
        // Gone from view once the fight is over; the partner's tavern never shows.
        var after = events.SkipWhile(e => !e.StartsWith("f ", StringComparison.Ordinal)).ToList();
        Assert.Contains(after, e => e is var x && (x == $"X {minion}" || x == $"Y {minion}"));
        Assert.DoesNotContain(events, e => e.Contains("BGS_100", StringComparison.Ordinal));
        Assert.Contains(events, e => e.StartsWith("f WIN ", StringComparison.Ordinal));
    }

    [Fact]
    public void A_file_with_the_partner_is_version_5_and_others_stay_version_4()
    {
        var duos = DuosGame().Battlegrounds.Text(new ReplayHeader(), new BattlegroundsReplayData());
        Assert.StartsWith("HPR5\n", duos, StringComparison.Ordinal);
        Assert.DoesNotContain("Pal", duos, StringComparison.Ordinal);

        var solo = Run([.. Setup, .. CombatSetup(), .. Fight(win: true)]).Battlegrounds.Text(new ReplayHeader(), new BattlegroundsReplayData());
        Assert.StartsWith("HPR4\n", solo, StringComparison.Ordinal);
        Assert.DoesNotContain(" P PLAY ", solo, StringComparison.Ordinal);
    }

    [Fact]
    public void The_import_keeps_the_partner_only_in_fights_of_a_version_5_file()
    {
        string[] events =
        [
            "T 1 30",
            "E 1 L PLAY 0 TB_BaconShop_HERO_50 HERO HEALTH=30",
            "E 7 P PLAY 1 BGS_100 MINION ATK=3 HEALTH=3",
            "F 1 75",
            "E 5 O PLAY 0 TB_BaconShop_HERO_08 HERO HEALTH=30",
            "E 8 P PLAY 1 BG25_011 MINION ATK=4 HEALTH=4",
            "M ATTACK 8 -",
            "f WIN 0 3",
            "T 2 90",
            "V 8 ATK 5",
        ];
        var header = new ReplayHeader { Summary = new ReplaySummary { Mode = "Duos", HeroCardId = "TB_BaconShop_HERO_50", Placement = 1 } };
        var text = ReplayFile.WriteBattlegrounds(header, new BattlegroundsReplayData(), events);
        Assert.StartsWith("HPR5\n", text, StringComparison.Ordinal);

        var clean = ReplayImport.Sanitize(text, CardDb.Empty, out var error);
        Assert.Equal(ReplayImportError.None, error);
        var body = ReplayFile.Read(clean!)!.Value.Body;
        Assert.DoesNotContain(body, l => l.Contains("BGS_100", StringComparison.Ordinal));
        Assert.Contains("E 8 P PLAY 1 BG25_011 MINION ATK=4 HEALTH=4", body);
        // The partner's table leaves view by the next tavern: a later tag of it is dropped.
        Assert.DoesNotContain("V 8 ATK 5", body);

        // The same lines under a version 4 format line: P is not of that version.
        var old = "HPR4" + text[4..];
        var cleanOld = ReplayImport.Sanitize(old, CardDb.Empty, out var oldError);
        Assert.Equal(ReplayImportError.None, oldError);
        Assert.DoesNotContain(ReplayFile.Read(cleanOld!)!.Value.Body, l => l.Contains(" P PLAY ", StringComparison.Ordinal));
    }

    [Fact]
    public void The_share_copy_goes_as_version_4_without_the_partner_until_the_site_reads_version_5()
    {
        var text = DuosGame().Battlegrounds.Text(new ReplayHeader { Summary = new ReplaySummary { Mode = "Duos" } }, new BattlegroundsReplayData());
        var minion = IdOf(DuosGame().Events, "BG25_011");

        var shared = ReplayUpload.Prepare(text, CardDb.Empty);
        Assert.NotNull(shared);
        Assert.StartsWith("HPR4\n", shared, StringComparison.Ordinal);
        Assert.DoesNotContain(" P PLAY ", shared, StringComparison.Ordinal);
        Assert.DoesNotContain($"M ATTACK {minion} ", shared, StringComparison.Ordinal);
        Assert.Contains("\nF ", shared, StringComparison.Ordinal);
        // Still a file the import (and the site's same rules) takes.
        Assert.NotNull(ReplayImport.Sanitize(shared!, CardDb.Empty, out var error));
        Assert.Equal(ReplayImportError.None, error);
    }

    [Fact]
    public void The_match_page_shows_the_partners_table_in_combat()
    {
        var text = DuosGame().Battlegrounds.Text(new ReplayHeader { Summary = new ReplaySummary { Mode = "Duos" } }, new BattlegroundsReplayData());
        var page = MatchPage.Load(new Core.Data.GameRecord { Key = "duos", Mode = "Duos" }, text, CardDb.Empty, null);
        Assert.True(page.HasBattlegroundsSteps);
        var steps = Enumerable.Range(0, page.FrameCount).Select(i => page.BattlegroundsStep(i)!).ToList();

        var fighting = steps.Where(s => s.PartnerFighting).ToList();
        Assert.NotEmpty(fighting);
        Assert.All(fighting, s => Assert.True(s.InCombat));
        Assert.Contains(fighting, s => s.PartnerBoard.Any(c => c.CardId == "BG25_011"));
        // Our board stays ours: the partner's minion never joins it.
        Assert.DoesNotContain(steps, s => s.Board.Any(c => c.CardId == "BG25_011"));
        Assert.All(steps.Where(s => !s.InCombat), s => Assert.Empty(s.PartnerBoard));
    }
}
