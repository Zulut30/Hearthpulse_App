using System.Text;
using System.Text.RegularExpressions;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Replay;

namespace Hearthpulse.Tests;

[CollectionDefinition(nameof(ReplayImportRealLogs), DisableParallelization = true)]
public sealed class ReplayImportRealLogs;

/// <summary>
/// The import filter over every game of the kept logs: heavy (it records them all), so it runs alone, not alongside
/// the timing tests of the parser.
/// </summary>
[Collection(nameof(ReplayImportRealLogs))]
public sealed partial class ReplayImportRealLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"hp-import-real-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    /// <summary>
    /// Every replay the app records from the kept logs passes the filter with its event lines unchanged. The filter
    /// keeps an opponent's quest face up only when the card data says it is one (docs/replay-library.md), so it is
    /// given card data here: the quests each log itself marks with QUEST=1, read from the log's text and not from the
    /// recorder. A real secret the recorder named would have no such mark and would fail the comparison.
    /// </summary>
    [RealLogsFact]
    public void Kept_logs_replays_pass_the_filter_unchanged()
    {
        var store = new ReplayStore(Path.Combine(_dir, "real")) { AutoPrune = false };
        var games = 0;
        foreach (var dir in Directory.EnumerateDirectories(RealLogTests.LogRoot, "Hearthstone_*"))
        {
            if (Core.Hearthstone.HearthstoneInstall.SessionStart(dir) is not { } start)
            {
                continue;
            }

            foreach (var file in new[] { "Power_old.log", "Power.log" }.Select(n => Path.Combine(dir, n)).Where(File.Exists))
            {
                var cards = Quests(RealLogTests.SharedLines(file));
                foreach (var record in Core.Data.LogBackfill.Parse(file, start, CardDb.Empty, replays: store))
                {
                    var text = store.Load(record.Replay)!;
                    var clean = ReplayImport.Sanitize(text, cards, out var error);
                    Assert.True(clean is not null, $"{record.StartedAt:g} {record.Mode}: {error}");
                    var (header, body) = ReplayFile.Read(text)!.Value;
                    var (cleanHeader, cleanBody) = ReplayFile.Read(clean!)!.Value;
                    Assert.Equal(body.Take(header.Events), cleanBody.Take(cleanHeader.Events));
                    games++;
                }
            }
        }

        Assert.True(games > 0);
    }

    /// <summary>Card data holding only the quests of a Power.log: the cards whose entity it shows with QUEST=1.</summary>
    internal static CardDb Quests(IEnumerable<string> lines)
    {
        var quests = new SortedSet<string>(StringComparer.Ordinal);
        string? shown = null;
        foreach (var line in lines)
        {
            if (!line.Contains("GameState.DebugPrintPower()", StringComparison.Ordinal))
            {
                continue;
            }

            var named = EntityCard().Match(line);
            if (named.Success)
            {
                // FULL_ENTITY, SHOW_ENTITY, CHANGE_ENTITY: the tags of this card follow, one a line.
                shown = named.Groups[1].Value;
                continue;
            }

            var tagOfShown = TagLine().IsMatch(line);
            if (!tagOfShown)
            {
                shown = null;
            }

            if (!line.Contains("tag=QUEST value=1", StringComparison.Ordinal))
            {
                continue;
            }

            var changed = TagChangeCard().Match(line);
            if (changed.Success)
            {
                quests.Add(changed.Groups[1].Value);
            }
            else if (tagOfShown && shown is not null)
            {
                quests.Add(shown);
            }
        }

        var json = "[" + string.Join(",", quests.Select((id, i) => $$"""{"id": "{{id}}", "dbfId": {{900_000 + i}}, "type": "SPELL", "mechanics": ["QUEST"]}""")) + "]";
        return CardDb.Parse("enUS", new MemoryStream(Encoding.UTF8.GetBytes(json)));
    }

    [GeneratedRegex(@"_ENTITY - (?:Creating|Updating) .*CardID=(\w+)\s*$")]
    private static partial Regex EntityCard();

    [GeneratedRegex(@"DebugPrintPower\(\) - \s+tag=\w+ value=")]
    private static partial Regex TagLine();

    [GeneratedRegex(@"TAG_CHANGE Entity=\[.* cardId=(\w+) player=\d+\] tag=QUEST value=1")]
    private static partial Regex TagChangeCard();

    [Fact]
    public void The_quests_of_a_log_are_the_cards_it_marks_with_the_quest_tag()
    {
        var cards = Quests(
        [
            "D 17:28:09.1212445 GameState.DebugPrintPower() -     SHOW_ENTITY - Updating Entity=[entityName=UNKNOWN ENTITY [cardType=INVALID] id=13 zone=HAND zonePos=1 cardId= player=1] CardID=TLC_602",
            "D 17:28:09.1212445 GameState.DebugPrintPower() -         tag=CARDTYPE value=SPELL",
            "D 17:28:09.1212445 GameState.DebugPrintPower() -         tag=QUEST value=1",
            "D 17:28:10.0000000 GameState.DebugPrintPower() -     SHOW_ENTITY - Updating Entity=[entityName=UNKNOWN ENTITY [cardType=INVALID] id=14 zone=HAND zonePos=1 cardId= player=1] CardID=EX1_610",
            "D 17:28:10.0000000 GameState.DebugPrintPower() -         tag=SECRET value=1",
            "D 17:28:10.0000000 GameState.DebugPrintPower() -     TAG_CHANGE Entity=[entityName=X id=15 zone=SECRET zonePos=0 cardId=UNG_940 player=1] tag=QUEST value=1 ",
            // PowerTaskList repeats the game's lines later: not read.
            "D 17:28:11.0000000 PowerTaskList.DebugPrintPower() -     TAG_CHANGE Entity=[entityName=X id=16 zone=SECRET zonePos=0 cardId=EX1_611 player=1] tag=QUEST value=1 ",
        ]);

        Assert.Contains("QUEST", cards.Get("TLC_602")!.Mechanics);
        Assert.Contains("QUEST", cards.Get("UNG_940")!.Mechanics);
        Assert.Null(cards.Get("EX1_610"));
        Assert.Null(cards.Get("EX1_611"));
    }
}
