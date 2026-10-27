using System.Text;
using System.Text.RegularExpressions;
using Hearthpulse.Core.Power;

namespace Hearthpulse.Tests;

/// <summary>
/// <see cref="PowerLine"/> replaced three regexes in <see cref="PowerParser"/>; it must accept exactly the same lines and
/// return the same groups. The patterns below are the ones the parser used before.
/// </summary>
public sealed class PowerLineTests
{
    private static readonly Regex Line = new(@"^[DWEI] (?<time>\d{2}:\d{2}:\d{2}\.\d+) GameState\.(?<method>DebugPrint\w+)\(\) - (?<body>.*)$");
    private static readonly Regex TagValue = new(@"^tag=(?<tag>\S+) value=(?<value>\S*)");
    private static readonly Regex TagChange = new(@"^TAG_CHANGE Entity=(?<entity>.+?) tag=(?<tag>\S+) value=(?<value>\S*)");

    private static void Same(string text)
    {
        var line = Line.Match(text);
        Assert.Equal(line.Success, PowerLine.TrySplit(text, out var time, out var method, out var body));
        if (line.Success)
        {
            Assert.Equal(line.Groups["time"].Value, text[time]);
            Assert.Equal(line.Groups["method"].Value, text[method]);
            Assert.Equal(line.Groups["body"].Value, text[body]);
        }

        var tag = TagValue.Match(text);
        Assert.Equal(tag.Success, PowerLine.TryTagValue(text, out var tagName, out var tagValue));
        if (tag.Success)
        {
            Assert.Equal(tag.Groups["tag"].Value, text[tagName]);
            Assert.Equal(tag.Groups["value"].Value, text[tagValue]);
        }

        var change = TagChange.Match(text);
        Assert.Equal(change.Success, PowerLine.TryTagChange(text, out var entity, out var changeTag, out var changeValue));
        if (change.Success)
        {
            Assert.Equal(change.Groups["entity"].Value, text[entity]);
            Assert.Equal(change.Groups["tag"].Value, text[changeTag]);
            Assert.Equal(change.Groups["value"].Value, text[changeValue]);
        }
    }

    /// <summary>Every line of a real log, and each body as the parser passes it on (trimmed).</summary>
    private static void SameForLog(IEnumerable<string> lines)
    {
        foreach (var text in lines)
        {
            Same(text);
            if (Line.Match(text) is { Success: true } m)
            {
                Same(m.Groups["body"].Value.Trim());
            }
        }
    }

    [Fact]
    public void A_real_log_splits_exactly_as_the_regexes_did() => SameForLog(BattlegroundsFixtureTests.Lines());

    [Fact]
    public void The_installed_games_logs_split_exactly_as_the_regexes_did()
    {
        // Optional: HP_POWER_LOGS=<Hearthstone Logs folder> checks every kept Power.log on this machine as well.
        if (Environment.GetEnvironmentVariable("HP_POWER_LOGS") is not { Length: > 0 } root || !Directory.Exists(root))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(root, "Power.log", SearchOption.AllDirectories))
        {
            SameForLog(File.ReadLines(path));
        }
    }

    [Theory]
    [InlineData("D 12:34:56.1234567 GameState.DebugPrintPower() -     tag=ZONE value=HAND")]
    [InlineData("D 12:34:56.1234567 GameState.DebugPrintPower() - ")]
    [InlineData("D 12:34:56.1234567 GameState.DebugPrintPower() -")]
    [InlineData("D 12:34:56.1234567 GameState.DebugPrintPower() - body\n")]
    [InlineData("D 12:34:56.1234567 GameState.DebugPrintPower() - body\n\n")]
    [InlineData("D 12:34:56.1234567 GameState.DebugPrintPower() - a\nb")]
    [InlineData("D 12:34:56.1234567 GameState.DebugPrintPower() - body\r")]
    [InlineData("D 12:34:56. GameState.DebugPrintPower() - x")]
    [InlineData("D 12:34:5.1 GameState.DebugPrintPower() - x")]
    [InlineData("D 1:34:56.1 GameState.DebugPrintPower() - x")]
    [InlineData("X 12:34:56.1 GameState.DebugPrintPower() - x")]
    [InlineData("D 12:34:56.1  GameState.DebugPrintPower() - x")]
    [InlineData("D 12:34:56.1 GameState.DebugPrint() - x")]
    [InlineData("D 12:34:56.1 GameState.DebugPrint_9() - x")]
    [InlineData("D 12:34:56.1 GameState.DebugPrintPower () - x")]
    [InlineData("D 12:34:56.1 PowerTaskList.DebugPrintPower() - x")]
    [InlineData("D 12:34:56.1 GameState.DebugPrintPower() -x")]
    [InlineData("D ١٢:٣٤:٥٦.٧ GameState.DebugPrintPower() - Arabic-Indic digits")]
    [InlineData("tag=ZONE value=HAND")]
    [InlineData("tag=ZONE value=")]
    [InlineData("tag=ZONE value= trailing")]
    [InlineData("tag=ZONE  value=HAND")]
    [InlineData("tag= value=HAND")]
    [InlineData("tag=ZONE\tvalue=HAND")]
    [InlineData("tag=ZONE value=HAND\u00A0x")]
    [InlineData("tag=A\u2028B value=1")]
    [InlineData("tag=ZONE value=HAND extra=1")]
    [InlineData("TAG_CHANGE Entity=GameEntity tag=TURN value=3 ")]
    [InlineData("TAG_CHANGE Entity=[entityName=Fireball id=45 zone=HAND zonePos=3 cardId=CS2_029 player=1] tag=ZONE value=PLAY ")]
    [InlineData("TAG_CHANGE Entity=Some tag=Name tag=ZONE value=PLAY")]
    [InlineData("TAG_CHANGE Entity=Some tag= tag=ZONE value=PLAY")]
    [InlineData("TAG_CHANGE Entity= tag=ZONE value=PLAY")]
    [InlineData("TAG_CHANGE Entity=  tag=ZONE value=PLAY")]
    [InlineData("TAG_CHANGE Entity=a\nb tag=ZONE value=PLAY")]
    [InlineData("TAG_CHANGE Entity=a\rb tag=ZONE value=PLAY")]
    [InlineData("TAG_CHANGE Entity=x tag=ZONE")]
    [InlineData("TAG_CHANGE Entity=x tag=ZONE value")]
    [InlineData("TAG_CHANGE Entity=")]
    [InlineData("TAG_CHANGE Entity=x")]
    [InlineData("")]
    public void Edge_cases_split_exactly_as_the_regexes_did(string text) => Same(text);

    [Fact]
    public void Every_character_classifies_as_in_the_regexes()
    {
        // \d in the time, \w in the method name, \S in a tag name and value, '.' in an entity: every UTF-16 unit.
        for (var c = 0; c <= char.MaxValue; c++)
        {
            var ch = ((char)c).ToString();
            Same($"D 12:34:56.1{ch} GameState.DebugPrintPower() - x");
            Same($"D 1{ch}:34:56.1 GameState.DebugPrintPower() - x");
            Same($"D 12:34:56.1 GameState.DebugPrint{ch}() - x");
            Same($"tag=Z{ch}NE value=H{ch}ND");
            Same($"TAG_CHANGE Entity=a{ch}b tag=ZONE value=1{ch}2");
        }
    }

    [Fact]
    public void Mutated_real_lines_split_exactly_as_the_regexes_did()
    {
        var random = new Random(6);
        var lines = BattlegroundsFixtureTests.Lines().Where(l => l.Contains("GameState.", StringComparison.Ordinal)).Take(3000).ToList();
        const string Inserts = " \t\n\r=-.:()[]0123456789aZ_\u00A0\u0661\u0301";
        foreach (var line in lines)
        {
            for (var n = 0; n < 4; n++)
            {
                var text = new StringBuilder(line);
                var at = random.Next(text.Length + 1);
                switch (random.Next(3))
                {
                    case 0:
                        text.Insert(at, Inserts[random.Next(Inserts.Length)]);
                        break;
                    case 1 when at < text.Length:
                        text.Remove(at, 1);
                        break;
                    default:
                        text.Length = Math.Min(text.Length, at);
                        break;
                }

                var mutated = text.ToString();
                Same(mutated);
                if (Line.Match(mutated) is { Success: true } m)
                {
                    Same(m.Groups["body"].Value.Trim());
                }
            }
        }
    }

    [Fact]
    public void Tag_strings_are_shared_and_bounded()
    {
        var pool = new TagStrings();
        var first = pool.Get("ZONE".AsSpan());
        Assert.Same(first, pool.Get("xZONEx".AsSpan(1, 4)));
        Assert.Same(string.Empty, pool.Get(ReadOnlySpan<char>.Empty));

        var longText = new string('a', TagStrings.MaxLength + 1);
        Assert.Equal(longText, pool.Get(longText));
        Assert.NotSame(pool.Get(longText), pool.Get(longText));

        for (var i = 0; pool.Count < TagStrings.MaxCount; i++)
        {
            pool.Get(i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        Assert.Equal("past the limit", pool.Get("past the limit"));
        Assert.Equal(TagStrings.MaxCount, pool.Count);
    }

    [Fact]
    public void Entities_share_the_tag_values_of_the_log()
    {
        var parser = new PowerParser(new DateOnly(2026, 10, 20));
        foreach (var line in BattlegroundsFixtureTests.Lines())
        {
            parser.Feed(line);
        }

        var game = parser.Game!;
        var zones = game.Entities.Values.Select(e => e.Tags.GetValueOrDefault(GameTag.Zone)).OfType<string>().Where(z => z == "PLAY").ToList();
        Assert.True(zones.Count > 1);
        Assert.All(zones, z => Assert.Same(zones[0], z));
    }
}
