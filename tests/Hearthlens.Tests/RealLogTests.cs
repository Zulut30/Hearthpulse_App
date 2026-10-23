using Hearthlens.Core.Cards;
using Hearthlens.Core.Data;
using Hearthlens.Core.Power;
using Hearthlens.Core.Tracking;
using Xunit.Abstractions;

namespace Hearthlens.Tests;

/// <summary>
/// Runs the parser over Power logs kept by the local Hearthstone client. Tests are no-ops on
/// machines without those logs.
/// </summary>
public sealed class RealLogTests(ITestOutputHelper output)
{
    private static readonly string LogRoot = @"D:\Hearthstone\Logs";

    private static CardDb Cards { get; } = LoadCards();

    private static CardDb LoadCards()
    {
        var path = Path.Combine(Path.GetTempPath(), "hl", "cards.json");
        if (!File.Exists(path))
        {
            return CardDb.Empty;
        }

        using var stream = File.OpenRead(path);
        return CardDb.Parse("itIT", stream);
    }

    private static IEnumerable<(string Path, DateOnly Date)> PowerLogs()
    {
        if (!Directory.Exists(LogRoot))
        {
            yield break;
        }

        foreach (var dir in Directory.EnumerateDirectories(LogRoot, "Hearthstone_*"))
        {
            var start = Core.Hearthstone.HearthstoneInstall.SessionStart(dir);
            foreach (var name in new[] { "Power_old.log", "Power.log" })
            {
                var file = Path.Combine(dir, name);
                if (start is not null && File.Exists(file))
                {
                    yield return (file, DateOnly.FromDateTime(start.Value));
                }
            }
        }
    }

    [Fact]
    public void Backfill_parses_every_kept_log_into_sane_records()
    {
        var total = 0;
        foreach (var (path, date) in PowerLogs())
        {
            var records = LogBackfill.Parse(path, date, Cards);
            foreach (var r in records)
            {
                output.WriteLine($"{r.StartedAt:g} {r.Mode,-13} {r.PlayerName,-14} hero={Cards.NameOf(r.HeroCardId),-22} place={r.Placement} result={r.Result} turns={r.Turns} tier={r.TavernTier} vs={r.OpponentClass}");
                Assert.False(string.IsNullOrEmpty(r.PlayerName));
                Assert.False(string.IsNullOrEmpty(r.HeroCardId));
                if (r.IsBattlegrounds)
                {
                    Assert.InRange(r.Placement, 1, 8);
                    Assert.InRange(r.TavernTier, 1, 7);
                    Assert.True(r.Turns >= 1);
                }
            }

            total += records.Count;
        }

        output.WriteLine($"{total} games");
    }

    [Fact]
    public void Battlegrounds_view_tracks_lobby_boards_and_combats()
    {
        var log = PowerLogs().FirstOrDefault(l => File.ReadLines(l.Path).Take(20_000).Any(x => x.Contains("GameType=GT_BATTLEGROUNDS")));
        if (log.Path is null)
        {
            return;
        }

        var parser = new PowerParser(log.Date);
        var tracker = new BattlegroundsTracker { Cards = Cards };
        parser.AddListener(tracker);
        BattlegroundsView? best = null;
        BattlegroundsView? heroPick = null;
        var lines = 0;
        foreach (var line in File.ReadLines(log.Path))
        {
            parser.Feed(line);
            if (++lines % 250 != 0 || parser.Game is not { IsBattlegrounds: true, LocalPlayerId: > 0 } game)
            {
                continue;
            }

            parser.Flush();
            var view = tracker.BuildView(game);
            if (view.IsHeroSelection && heroPick is null)
            {
                heroPick = view;
            }

            if (best is null || view.Opponents.Count(o => o.LastBoard.Count > 0) > best.Opponents.Count(o => o.LastBoard.Count > 0))
            {
                best = view;
            }
        }

        Assert.NotNull(heroPick);
        output.WriteLine("Hero options: " + string.Join(", ", heroPick!.HeroOptions.Select(h => $"{h.Name} ({h.Armor} armor)")));
        Assert.InRange(heroPick.HeroOptions.Count, 2, 4);

        Assert.NotNull(best);
        output.WriteLine($"Turn {best!.Turn} tier {best.TechLevel} hp {best.Health} place {best.Place} races [{string.Join(",", best.Races)}]");
        foreach (var o in best.Opponents)
        {
            output.WriteLine($"  #{o.Place} {o.HeroName,-22} hp={o.Health} tier={o.TechLevel} next={o.IsNextOpponent} last={o.LastResult} board(T{o.LastBoardTurn})=[{string.Join(" | ", o.LastBoard.Select(m => $"{m.Name} {m.Attack}/{m.Health}{(m.Golden ? " G" : "")}"))}]");
        }

        foreach (var c in best.Combats.TakeLast(5))
        {
            output.WriteLine($"  combat T{c.Turn} vs {Cards.NameOf(c.OpponentHeroCardId)}: {c.Result} taken={c.DamageTaken} dealt={c.DamageDealt}");
        }

        Assert.Equal(7, best.Opponents.Count);
        Assert.Contains(best.Opponents, o => o.LastBoard.Count > 0);
        Assert.NotEmpty(best.Combats);
        Assert.True(best.Races.Count >= 4, "tribes should be inferred from the shop");
    }
}
