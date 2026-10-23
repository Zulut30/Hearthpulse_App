using System.IO.Compression;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Power;
using Hearthpulse.Core.Tracking;
using Xunit.Abstractions;

namespace Hearthpulse.Tests;

/// <summary>
/// Replays a real Battlegrounds game kept in the repository (Fixtures/BattlegroundsGame, 2026-10-23, build 254477):
/// hero pick, two shop turns and the first combat, then the client was closed. Only the GameState lines and the
/// on-screen board state lines the parser reads are kept; player names and account ids are replaced.
/// cards.json.gz holds the HearthstoneJSON (ruRU) entries of the cards in the log.
/// </summary>
public sealed class BattlegroundsFixtureTests(ITestOutputHelper output)
{
    private static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "Fixtures", "BattlegroundsGame");

    private static readonly Lazy<CardDb> Cards = new(() =>
    {
        using var gz = new GZipStream(File.OpenRead(Path.Combine(Folder, "cards.json.gz")), CompressionMode.Decompress);
        return CardDb.Parse("ruRU", gz);
    });

    internal static IEnumerable<string> Lines()
    {
        using var gz = new GZipStream(File.OpenRead(Path.Combine(Folder, "power.log.gz")), CompressionMode.Decompress);
        using var reader = new StreamReader(gz);
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }

    [Fact]
    public void Battlegrounds_view_tracks_lobby_boards_and_combats()
    {
        var replay = BattlegroundsReplay.Run(Lines(), new DateOnly(2026, 10, 23), Cards.Value);
        replay.Print(output, Cards.Value);

        Assert.NotNull(replay.HeroPick);
        Assert.Equal(4, replay.HeroPick!.HeroOptions.Count);

        var view = Assert.IsType<BattlegroundsView>(replay.Final);
        Assert.False(view.IsHeroSelection);
        Assert.Equal(7, view.Opponents.Count);
        var combat = Assert.Single(view.Combats);
        Assert.Equal(1, combat.Turn);
        Assert.Equal("Tie", combat.Result);
        Assert.Contains(view.Opponents, o => o.LastBoard.Count > 0);
        // Inferred from the shop: the two tier 1 shops showed a Dragon, a Murloc, a Pirate and a neutral minion.
        Assert.Equal(["DRAGON", "MURLOC", "PIRATE"], view.Races);
    }
}

/// <summary>Feeds a Power log through <see cref="PowerParser"/> + <see cref="BattlegroundsTracker"/> and keeps the views a test looks at.</summary>
internal sealed record BattlegroundsReplay(BattlegroundsView? HeroPick, BattlegroundsView? Best, BattlegroundsView? Final)
{
    public static BattlegroundsReplay Run(IEnumerable<string> lines, DateOnly date, CardDb cards, int every = 1)
    {
        var parser = new PowerParser(date);
        var tracker = new BattlegroundsTracker { Cards = cards };
        parser.AddListener(tracker);
        BattlegroundsView? heroPick = null;
        BattlegroundsView? best = null;
        var count = 0;
        foreach (var line in lines)
        {
            parser.Feed(line);
            // Views are built only after a complete one-line construct: flushing in the middle of a FULL_ENTITY's
            // tag lines would drop the rest of them.
            if (!line.Contains(" TAG_CHANGE ", StringComparison.Ordinal) && !line.Contains("BLOCK_END", StringComparison.Ordinal)
                || ++count % every != 0 || Sample(parser, tracker) is not { } view)
            {
                continue;
            }

            if (view.IsHeroSelection && view.HeroOptions.Count > (heroPick?.HeroOptions.Count ?? 0))
            {
                heroPick = view;
            }

            // The latest view with the most opponent boards, so a later one (after the combat) wins a tie.
            if (best is null || Boards(view) >= Boards(best))
            {
                best = view;
            }
        }

        var final = Sample(parser, tracker);
        if (final is not null && (best is null || Boards(final) >= Boards(best)))
        {
            best = final;
        }

        return new BattlegroundsReplay(heroPick, best, final);
    }

    private static BattlegroundsView? Sample(PowerParser parser, BattlegroundsTracker tracker)
    {
        if (parser.Game is not { IsBattlegrounds: true, LocalPlayerId: > 0 } game)
        {
            return null;
        }

        parser.Flush();
        return tracker.BuildView(game);
    }

    private static int Boards(BattlegroundsView view) => view.Opponents.Count(o => o.LastBoard.Count > 0);

    public void Print(ITestOutputHelper output, CardDb cards)
    {
        if (HeroPick is not null)
        {
            output.WriteLine("Hero options: " + string.Join(", ", HeroPick.HeroOptions.Select(h => $"{h.Name} ({h.Armor} armor)")));
        }

        if (Best is not { } best)
        {
            return;
        }

        output.WriteLine($"Turn {best.Turn} tier {best.TechLevel} hp {best.Health} place {best.Place} races [{string.Join(",", best.Races)}]");
        foreach (var o in best.Opponents)
        {
            output.WriteLine($"  #{o.Place} {o.HeroName,-22} hp={o.Health} tier={o.TechLevel} next={o.IsNextOpponent} last={o.LastResult} board(T{o.LastBoardTurn})=[{string.Join(" | ", o.LastBoard.Select(m => $"{m.Name} {m.Attack}/{m.Health}{(m.Golden ? " G" : "")}"))}]");
        }

        foreach (var c in best.Combats.TakeLast(5))
        {
            output.WriteLine($"  combat T{c.Turn} vs {cards.NameOf(c.OpponentHeroCardId)}: {c.Result} taken={c.DamageTaken} dealt={c.DamageDealt}");
        }
    }
}
