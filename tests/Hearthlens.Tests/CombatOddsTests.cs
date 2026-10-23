using Hearthlens.Core.Battlegrounds;
using Hearthlens.Core.Cards;
using Hearthlens.Core.Power;
using Hearthlens.Core.Tracking;
using Xunit.Abstractions;

namespace Hearthlens.Tests;

public sealed class CombatOddsTests(ITestOutputHelper output)
{
    private static SimMinion M(int attack, int health, bool taunt = false, bool shield = false, bool poison = false, bool reborn = false, int attacks = 1) =>
        new(attack, health, taunt, shield, poison, false, reborn, attacks, false, attack, false);

    [Fact]
    public void Bigger_board_wins_every_time()
    {
        var odds = CombatSimulator.Run([M(10, 10), M(10, 10)], [M(1, 1)], null);
        Assert.Equal(1.0, odds.Win, 3);
        Assert.False(odds.Approximate);
    }

    [Fact]
    public void Mirror_boards_are_symmetric()
    {
        // Equal boards mostly trade into ties; wins and losses must balance.
        var odds = CombatSimulator.Run([M(3, 3), M(2, 4)], [M(3, 3), M(2, 4)], null, 20_000);
        Assert.InRange(odds.Win - odds.Loss, -0.02, 0.02);
        Assert.Equal(1.0, odds.Win + odds.Tie + odds.Loss, 6);
    }

    [Fact]
    public void Divine_shield_and_poison_beat_raw_stats()
    {
        // A 1/1 poisonous divine shield kills one 20/20 and survives the first hit back.
        var odds = CombatSimulator.Run([M(1, 1, shield: true, poison: true)], [M(20, 20)], true);
        Assert.Equal(1.0, odds.Tie + odds.Win, 3);
        Assert.Equal(0, odds.Loss, 3);
    }

    [Fact]
    public void Taunt_is_attacked_first()
    {
        // Their 0/5 taunt absorbs our 4/1's attack, so their 4/4 survives and wins.
        var odds = CombatSimulator.Run([M(4, 1)], [M(0, 5, taunt: true), M(4, 4)], true);
        Assert.Equal(1.0, odds.Loss, 3);
    }

    [Fact]
    public void Next_attacker_stays_the_same_minion_when_one_to_its_left_dies()
    {
        // Their 1/6 taunt attacks first, then dies to our 5/10, so their 8/1 is next in line and kills us,
        // leaving their stealthed 1/1. Skipping the 8/1 (sending the 1/1 first) would turn this into a tie.
        SimMinion stealthed = new(1, 1, false, false, false, false, false, 1, true, 1, false);
        var odds = CombatSimulator.Run([M(5, 10)], [M(1, 6, taunt: true), M(8, 1), stealthed], firstAttackerIsOurs: false);
        Assert.Equal(1.0, odds.Loss, 3);
    }

    [Fact]
    public void Predictions_track_real_combat_results()
    {
        var cardsPath = Path.Combine(Path.GetTempPath(), "hl", "cards.json");
        if (!File.Exists(cardsPath) || !Directory.Exists(@"D:\Hearthstone\Logs"))
        {
            return;
        }

        CardDb cards;
        using (var stream = File.OpenRead(cardsPath))
        {
            cards = CardDb.Parse("itIT", stream);
        }

        int fights = 0, favouriteRight = 0, decided = 0;
        double brier = 0;
        foreach (var log in Directory.EnumerateFiles(@"D:\Hearthstone\Logs", "Power*.log", SearchOption.AllDirectories)
                     .OrderByDescending(File.GetLastWriteTime).Take(4))
        {
            var parser = new PowerParser(DateOnly.FromDateTime(File.GetLastWriteTime(log)));
            var tracker = new BattlegroundsTracker { Cards = cards };
            parser.AddListener(tracker);
            var seen = 0;
            foreach (var line in ReadShared(log))
            {
                parser.Feed(line);
                if (line.Contains("CREATE_GAME", StringComparison.Ordinal))
                {
                    seen = 0;
                }

                if (parser.Game is not { IsBattlegrounds: true, LocalPlayerId: > 0 } game || line.IndexOf("BLOCK_END", StringComparison.Ordinal) < 0)
                {
                    continue;
                }

                var combats = tracker.BuildView(game).Combats;
                for (; seen < combats.Count; seen++)
                {
                    if (combats[seen].Odds is not { } odds)
                    {
                        continue;
                    }

                    fights++;
                    var actual = combats[seen].Result;
                    var won = actual == "Win" ? 1.0 : 0;
                    brier += Math.Pow(odds.Win - won, 2);
                    if (actual is "Win" or "Loss" && Math.Abs(odds.Win - odds.Loss) > 0.2)
                    {
                        decided++;
                        if ((odds.Win > odds.Loss) == (actual == "Win")) favouriteRight++;
                    }
                }
            }
        }

        output.WriteLine($"fights={fights} brier={(fights == 0 ? 0 : brier / fights):0.000} favourite right {favouriteRight}/{decided}");
        Assert.True(fights == 0 || brier / fights < 0.35, "simulated odds are no better than a coin");
    }

    [Fact]
    public void Odds_stay_up_until_the_fight_finishes_on_screen()
    {
        var cardsPath = Path.Combine(Path.GetTempPath(), "hl", "cards.json");
        var log = Directory.Exists(@"D:\Hearthstone\Logs")
            ? Directory.EnumerateFiles(@"D:\Hearthstone\Logs", "Power*.log", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTime).FirstOrDefault()
            : null;
        if (!File.Exists(cardsPath) || log is null)
        {
            return;
        }

        CardDb cards;
        using (var stream = File.OpenRead(cardsPath))
        {
            cards = CardDb.Parse("itIT", stream);
        }

        var parser = new PowerParser(DateOnly.FromDateTime(File.GetLastWriteTime(log)));
        var tracker = new BattlegroundsTracker { Cards = cards };
        parser.AddListener(tracker);
        int endings = 0, shown = 0;
        var attacked = false;
        foreach (var line in ReadShared(log))
        {
            if (line.Contains("GameState.DebugPrintPower", StringComparison.Ordinal) && line.Contains("BLOCK_START BlockType=ATTACK", StringComparison.Ordinal))
            {
                attacked = true;
            }

            // Just before the client finishes showing a fight with attacks, the odds must still be up.
            if (line.Contains("PowerTaskList.", StringComparison.Ordinal)
                && line.Contains("tag=BOARD_VISUAL_STATE value=1", StringComparison.Ordinal)
                && parser.Game is { IsBattlegrounds: true, LocalPlayerId: > 0 } game)
            {
                if (attacked)
                {
                    parser.Flush();
                    endings++;
                    var view = tracker.BuildView(game);
                    if (view is { InCombat: true, CombatOdds: not null }) shown++;
                }

                attacked = false;
            }

            parser.Feed(line);
        }

        output.WriteLine($"fights ending on screen={endings} odds still shown={shown}");
        Assert.True(endings == 0 || shown >= endings * 0.8, $"odds gone before the fight ended on screen ({shown}/{endings})");
    }

    private static IEnumerable<string> ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }
}
