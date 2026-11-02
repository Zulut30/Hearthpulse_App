using System.IO.Compression;
using System.Text.Json.Nodes;
using CsCheck;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Replay;

namespace Hearthpulse.Tests.Properties;

/// <summary>
/// Property tests of the Battlegrounds copy to share (Etalon T4, review M3): whatever a format 4 file holds, the copy
/// <see cref="ReplayShare.Prepare"/> writes shows Bob's offer only outside a combat and the combat opponent only from
/// the combat to the next tavern, refers only to cards in view, keeps no foreign field and is made of grammar lines only.
/// </summary>
public sealed class BattlegroundsShareProperties : IDisposable
{
    private static readonly string[] Cards = ["BGS_004", "BGS_039", "BG21_001", "BGS_119", "BG26_801"];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"hp-share-props-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    /// <summary>
    /// A game of 1–4 rounds: a tavern with Bob's offer and our minions, a combat with the opponent's board. Some lines
    /// break the order on purpose: Bob's card shown in a combat or the opponent's in a tavern, and later tags of a card
    /// that has left view.
    /// </summary>
    private static List<string> Game(int seed)
    {
        var random = new Random(seed);
        var lines = new List<string>();
        var id = 100;
        string Card() => Cards[random.Next(Cards.Length)];
        string Shown(char side, string zone = "PLAY") =>
            $"E {++id} {side} {zone} {random.Next(1, 8)} {Card()} MINION ATK={random.Next(0, 20)} HEALTH={random.Next(1, 20)}";
        var rounds = random.Next(1, 5);
        for (var turn = 1; turn <= rounds; turn++)
        {
            lines.Add($"T {turn} {30 - turn}");
            var offer = new List<int>();
            for (var i = random.Next(1, 4); i > 0; i--)
            {
                lines.Add(Shown('B'));
                offer.Add(id);
            }

            lines.Add(Shown('L'));
            lines.Add($"V {offer[0]} ATK {random.Next(1, 30)}");
            if (random.Next(3) == 0)
            {
                lines.Add(Shown('O'));  // the opponent in a tavern
            }

            lines.Add($"F {turn} {30 - turn}");
            var opponent = new List<int>();
            for (var i = random.Next(1, 4); i > 0; i--)
            {
                lines.Add(Shown('O'));
                opponent.Add(id);
            }

            if (random.Next(3) == 0)
            {
                lines.Add(Shown('B'));  // Bob's offer in a combat
                lines.Add($"V {offer[^1]} HEALTH {random.Next(1, 30)}");  // a card of the offer that left view
            }

            lines.Add($"M ATTACK {opponent[0]} -");
            lines.Add($"V {opponent[0]} DAMAGE {random.Next(0, 9)}");
            lines.Add(random.Next(2) == 0 ? "f WIN 3 0" : "f LOSS 0 4");
        }

        return lines;
    }

    private static string Replay(IReadOnlyList<string> events) => ReplayFile.WriteBattlegrounds(
        new ReplayHeader { Summary = new ReplaySummary { Mode = "Battlegrounds", StartedAt = new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc) } },
        new BattlegroundsReplayData
        {
            Rounds = [new BattlegroundsRound { Turn = 1, OpponentHero = "BG20_HERO_102" }],
            Lobby = [new BattlegroundsLobbyHero("TB_BaconShop_HERO_50", 1, true), new BattlegroundsLobbyHero("BG20_HERO_102", 2, false)],
        },
        events);

    /// <summary>The share copy's text, or null when the filter refuses the file.</summary>
    private string? Share(string text)
    {
        if (ReplayShare.Prepare(text, _dir, CardDb.Empty) is not { } path)
        {
            return null;
        }

        using var file = File.OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        return reader.ReadToEnd();
    }

    /// <summary>The copy's event lines (between the header and the G line).</summary>
    private static List<string> Events(string copy)
    {
        var body = ReplayFile.Read(copy)!.Value.Body;
        return [.. body.Take(body.Count - 1)];
    }

    [Fact]
    public void The_copy_shows_bob_and_the_opponent_only_when_they_are_in_view() =>
        Gen.Int.Sample(seed =>
        {
            var copy = Share(Replay(Game(seed)));
            Assert.NotNull(copy);
            var shown = new Dictionary<string, char>(StringComparer.Ordinal);
            bool fight = false, opponentInView = false, leaving = false;
            foreach (var line in Events(copy!))
            {
                Assert.True(BattlegroundsReplayGrammar.IsLine(line), line);
                var t = line.Split(' ');
                if (leaving && t[0] != "X")
                {
                    // Bob's offer left view right after the combat started
                    Assert.DoesNotContain('B', shown.Values);
                    leaving = false;
                }

                switch (t[0])
                {
                    case "F":
                        fight = opponentInView = leaving = true;
                        break;
                    case "f":
                        fight = false;
                        break;
                    case "T":
                        // the combat opponent left view before the tavern
                        Assert.DoesNotContain('O', shown.Values);
                        fight = opponentInView = false;
                        break;
                    case "E":
                        Assert.False(t[2] == "B" && fight, $"Bob's offer in a combat: {line}");
                        Assert.False(t[2] == "O" && !opponentInView, $"the opponent outside a combat: {line}");
                        shown[t[1]] = t[2][0];
                        break;
                    case "X" or "Y":
                        Assert.True(shown.Remove(t[1]), $"{line}: not in view");
                        break;
                    case "V" or "Z" when t[1] != "L":
                        Assert.True(shown.ContainsKey(t[1]), $"{line}: not in view");
                        break;
                }
            }
        }, iter: 300);

    /// <summary>Where a foreign or old writer may put a name: the header, the summary, the G line, a round, a lobby hero.</summary>
    private static readonly Func<string, string, string>[] Carriers =
    [
        (text, name) => SanitizerProperties.WithHeader(text, header => header["playerName"] = name),
        (text, name) => SanitizerProperties.WithHeader(text, header => header["summary"]!["opponentName"] = name),
        (text, name) => WithData(text, data => data["opponentName"] = name),
        (text, name) => WithData(text, data => data["rounds"]![0]!["opponentName"] = name),
        (text, name) => WithData(text, data => data["lobby"]![1]!["name"] = name),
    ];

    private static string WithData(string text, Action<JsonObject> change)
    {
        var lines = text.Split('\n').ToList();
        var at = lines.FindLastIndex(l => l.StartsWith("G ", StringComparison.Ordinal));
        var data = JsonNode.Parse(lines[at][2..])!.AsObject();
        change(data);
        lines[at] = "G " + data.ToJsonString(new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        return string.Join('\n', lines);
    }

    [Fact]
    public void A_foreign_field_with_a_name_does_not_reach_the_copy() =>
        Gen.Select(Gen.Int, Gen.Int[0, Carriers.Length - 1], Gen.Char["abcdefghijklmnopqrstuvwxyzабвгдеёжз0123456789 '#"].Array[3, 10]).Sample((seed, carrier, rest) =>
        {
            var name = "Zx" + new string(rest);
            var text = Carriers[carrier](Replay(Game(seed)), name);
            Assert.True(SanitizerProperties.Holds(text, name), $"«{name}» is not in the input");
            var copy = Share(text);
            Assert.NotNull(copy);
            Assert.False(SanitizerProperties.Leaks(copy!, name), $"«{name}» left in: {copy}");
        }, iter: 200);

    /// <summary>What a damaged or forged line may hold in place of one of its words.</summary>
    private static readonly string[] Junk = ["Zxname", "Zx Name#1234", "-1", "1234567890123", "file:///C:/x", "BG_" + new string('A', 70), "", "ATK=-5", "E", "O"];

    [Fact]
    public void Every_line_of_the_copy_is_a_grammar_line_and_a_damaged_line_never_leaks() =>
        Gen.Select(Gen.Int, Gen.Int[0, 1000], Gen.Int[0, 20], Gen.Int[0, Junk.Length - 1]).Sample((seed, at, word, junk) =>
        {
            var events = Game(seed);
            var line = events[at % events.Count].Split(' ');
            line[word % line.Length] = Junk[junk];
            events[at % events.Count] = string.Join(' ', line);
            var copy = Share(Replay(events));
            if (copy is null)
            {
                return;
            }

            Assert.All(Events(copy), l => Assert.True(BattlegroundsReplayGrammar.IsLine(l), l));
            Assert.DoesNotContain("Zx", copy, StringComparison.Ordinal);
            Assert.DoesNotContain("file:", copy, StringComparison.Ordinal);
        }, iter: 300);
}
