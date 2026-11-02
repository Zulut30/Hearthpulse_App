using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hearthpulse.Core.Battlegrounds;
using Hearthpulse.Core.Battlegrounds.Effects;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Power;
using Hearthpulse.Core.Replay;
using Hearthpulse.Core.Tracking;
using Xunit.Abstractions;

namespace Hearthpulse.Tests;

/// <summary>
/// Characterization of the Battlegrounds giants split in Etalon Q4 (`Fight`, `BattlegroundsTracker`,
/// `BattlegroundsReplayRecorder`): fingerprints of everything they produce on fixed inputs, taken before the split. A
/// move of code that changes behaviour changes a fingerprint. `PoolClient` is pinned by `PoolTests`' exact values.
/// </summary>
public sealed class BgGiantsCharacterizationTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false, IncludeFields = true };

    private static string Fingerprint(StringBuilder text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..16];

    private static CardDb FixtureCards()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "Fixtures", "BattlegroundsGame");
        using var gz = new GZipStream(File.OpenRead(Path.Combine(folder, "cards.json.gz")), CompressionMode.Decompress);
        return CardDb.Parse("ruRU", gz);
    }

    [Fact]
    public void The_tracker_and_the_replay_recorder_produce_what_they_did_before_the_split()
    {
        var cards = FixtureCards();
        var parser = new PowerParser(new DateOnly(2026, 10, 23));
        var tracker = new BattlegroundsTracker { Cards = cards };
        var recorder = new ReplayRecorder();
        parser.AddListener(tracker);
        parser.AddListener(recorder);
        var views = new StringBuilder();
        foreach (var line in BattlegroundsFixtureTests.Lines())
        {
            parser.Feed(line);
            if (line.Contains("BLOCK_END", StringComparison.Ordinal) && parser.Game is { IsBattlegrounds: true, LocalPlayerId: > 0 } game)
            {
                parser.Flush();
                // Combat odds are Monte Carlo on the thread pool: pinned by the simulator's own fingerprint instead.
                var view = tracker.BuildView(game);
                view = view with { CombatOdds = null, Combats = [.. view.Combats.Select(c => c with { Odds = null })] };
                views.Append(JsonSerializer.Serialize(view, Json)).Append('\n');
            }
        }

        parser.Flush();
        var replay = new StringBuilder(recorder.Battlegrounds.Text(new ReplayHeader(), tracker.ReplayData(parser.Game!)));
        var data = new StringBuilder(JsonSerializer.Serialize(tracker.ReplayData(parser.Game!), Json));

        var actual = $"views {Fingerprint(views)} replay {Fingerprint(replay)} data {Fingerprint(data)}";
        output.WriteLine(actual);
        Assert.Equal("views 353E1634374395D9 replay 3DB931AD881A2307 data 2C492810FA8A5101", actual);
    }

    [Fact]
    public void The_simulator_fights_as_it_did_before_the_split()
    {
        var registry = EffectRegistry.Default;
        var ids = registry.NormalCardIds.Order(StringComparer.Ordinal).ToArray();
        var keywords = new[] { Keyword.None, Keyword.Taunt, Keyword.DivineShield, Keyword.Reborn, Keyword.Windfury, Keyword.Venomous, Keyword.Poisonous };
        var tribes = new[] { Tribe.None, Tribe.Beast, Tribe.Demon, Tribe.Dragon, Tribe.Elemental, Tribe.Mech, Tribe.Murloc, Tribe.Naga, Tribe.Pirate, Tribe.Quilboar, Tribe.Undead };
        var text = new StringBuilder();
        for (var i = 0; i < 400; i++)
        {
            var random = new Random(i * 7919 + 17);
            SimSide Board() => new(Enumerable.Range(0, random.Next(1, 8)).Select(_ => SimMinion.Create(
                ids[random.Next(ids.Length)], random.Next(1, 30), random.Next(1, 30), tribes[random.Next(tribes.Length)],
                keywords[random.Next(keywords.Length)], random.Next(5) == 0)).ToArray());
            var ours = Board();
            var theirs = Board();
            var result = CombatSimulator.RunSingle(ours, theirs, random.Next(2) == 0, new Random(i), registry, trace: true);
            text.Append(CultureInfo.InvariantCulture, $"{i} {result.Outcome} ")
                .Append(string.Join(',', result.Ours.Select(m => $"{m.CardId}:{m.Attack}/{m.Health}")))
                .Append(" | ")
                .Append(string.Join(',', result.Theirs.Select(m => $"{m.CardId}:{m.Attack}/{m.Health}")))
                .Append('\n')
                .AppendJoin('\n', result.Log)
                .Append('\n');
        }

        var actual = $"fights {Fingerprint(text)}";
        output.WriteLine(actual);
        Assert.Equal("fights 6C3E80D601C5A873", actual);
    }
}
