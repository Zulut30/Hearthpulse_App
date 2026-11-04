using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Replay;
using static Hearthpulse.Tests.BattlegroundsReplayTests;

namespace Hearthpulse.Tests;

/// <summary>
/// Duos (plan R3, <c>TASK-2026-11-02-duos-replay</c>): our partner's table is written as side P while it is on the screen
/// in combat, never their tavern; a file with it is <c>HPR5</c>, every other stays <c>HPR4</c>; the import keeps P only
/// in fights of a version 5 file; the match page shows it. Synthetic lines: no real Duos log was checked.
/// </summary>
public sealed class DuosReplayTests
{
    private const int Partner = 5;

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
        var hero = IdOf(events, "TB_BaconShop_HERO_08");
        var fight = events.SkipWhile(e => !e.StartsWith("F ", StringComparison.Ordinal)).TakeWhile(e => !e.StartsWith("f ", StringComparison.Ordinal)).ToList();

        Assert.Contains(fight, e => e.StartsWith($"E {minion} P PLAY 1 BG25_011 MINION", StringComparison.Ordinal));
        Assert.Contains(fight, e => e.StartsWith($"E {hero} P PLAY", StringComparison.Ordinal));
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
        Assert.Contains(fighting, s => s.PartnerBoard.Any(c => c.CardId == "BG25_011") && s.Partner?.HeroCardId == "TB_BaconShop_HERO_08");
        // Our board stays ours: the partner's minion never joins it.
        Assert.DoesNotContain(steps, s => s.Board.Any(c => c.CardId == "BG25_011"));
        Assert.All(steps.Where(s => !s.InCombat), s => Assert.Empty(s.PartnerBoard));
    }
}
