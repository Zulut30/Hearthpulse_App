using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Constructed;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Power;
using Hearthpulse.Core.Replay;
using Hearthpulse.Core.Tracking;
using Xunit;
using static Hearthpulse.Tests.ReplayRecorderTests;

namespace Hearthpulse.Tests;

/// <summary>
/// After the game the match page shows what the log revealed by its end (format 3, post-game part): a card back that was
/// played later is face up on the turns it was hidden, the opponent's mulligan shows once its cards came to light, and
/// what the game never revealed stays a card back. During the game nothing changes: the events and the live view hold
/// only what was visible at that moment.
/// </summary>
public sealed class ReplayRevealTests : IDisposable
{
    private const string PlayBlock =
        "DebugPrintPower() - BLOCK_START BlockType=PLAY Entity=[entityName=UNKNOWN ENTITY [cardType=INVALID] id={0} zone=HAND zonePos=1 cardId= player=2] EffectCardId=System.Collections.Generic.List`1[System.String] EffectIndex=0 Target=0 SubOption=-1 ";

    private readonly ReplayStore _store = new(Path.Combine(Path.GetTempPath(), $"hp-replay-reveal-{Guid.NewGuid():N}"));

    public void Dispose()
    {
        if (Directory.Exists(_store.Directory))
        {
            Directory.Delete(_store.Directory, recursive: true);
        }
    }

    private static string Turn(int turn) => $"DebugPrintPower() - TAG_CHANGE Entity=GameEntity tag=TURN value={turn} ";

    private static string Step(string step) => $"DebugPrintPower() - TAG_CHANGE Entity=GameEntity tag=STEP value={step} ";

    /// <summary>Their card <paramref name="id"/> is played from hand and revealed as <paramref name="cardId"/>.</summary>
    private static string[] TheyPlay(int id, string cardId) =>
    [
        string.Format(PlayBlock, id),
        .. Show(id, cardId, "tag=CARDTYPE value=MINION", "tag=ATK value=3", "tag=HEALTH value=3"),
        Zone(id, "PLAY", cardId),
        "DebugPrintPower() - BLOCK_END",
    ];

    /// <summary>
    /// The usual game with 62 and 64 in their starting deck; turn 5: they draw both (card backs); turn 7 (<see cref="Reveal"/>):
    /// they play 62. 64 is never revealed.
    /// </summary>
    private static readonly string[] BeforeReveal =
    [
        .. Setup[..^1],
        .. Card(62, "", "DECK", 2, ""),
        .. Card(64, "", "DECK", 2, ""),
        Setup[^1],
        Zone(62, "HAND"),
        Zone(64, "HAND"),
    ];

    private static IReadOnlyList<string> Play(out ReplayRecorder recorder, params string[] lines)
    {
        var parser = new PowerParser(new DateOnly(2026, 10, 23));
        recorder = new ReplayRecorder();
        parser.AddListener(recorder);
        Feed(parser, lines);
        parser.Flush();
        return recorder.Events;
    }

    private static readonly string[] Reveal = [Turn(6), Turn(7), .. TheyPlay(62, "CS2_182")];

    private static GameRecord Game(string? replay) => new()
    {
        Key = "20261023123000|velox#21276|GT_RANKED",
        StartedAt = new DateTime(2026, 10, 23, 12, 30, 0),
        EndedAt = new DateTime(2026, 10, 23, 12, 41, 5),
        Mode = "Ranked",
        PlayerName = "velox#21276",
        OpponentName = "Rival#1234",
        PlayerClass = "PRIEST",
        OpponentClass = "WARRIOR",
        Result = "Win",
        Turns = 9,
        Replay = replay,
    };

    [Fact]
    public void A_card_back_played_later_is_face_up_on_earlier_turns_after_the_game()
    {
        Play(out var recorder, [.. BeforeReveal, .. Reveal]);
        var text = recorder.Text(new ReplayHeader());

        Assert.StartsWith("HPR3\n", text);
        Assert.Contains("K 62 CS2_182 7 played 0", text);
        Assert.DoesNotContain("K 64 ", text);
        var replay = MatchReplay.Parse(text)!;
        Assert.True(replay.HasPostGame);

        // At turn 5 both are card backs in their hand: 62 knows what it was, 64 does not.
        var early = replay.Frames.First(f => f.Opponent.Hand.Count == 2);
        Assert.All(early.Opponent.Hand, c => Assert.True(c.IsCardBack));
        var revealed = Assert.Single(early.Opponent.Hand, c => c.Reveal is not null);
        Assert.Equal("CS2_182", revealed.Reveal!.CardId);
        Assert.Equal(4, revealed.Reveal.Turn);
        Assert.Equal("played", revealed.Reveal.How);
        Assert.Single(early.Opponent.Hand, c => c.Reveal is null);

        var page = MatchPage.Load(Game(_store.Save(Game(null).Key, text)), _store, CardDb.Empty, null);
        var hand = page.Step(early.Index, ReplayPlayer.Local)!.Top.Hand;
        var face = Assert.Single(hand, c => !c.CardBack);
        Assert.True(face.Revealed);
        Assert.Equal("CS2_182", face.CardId);
        Assert.Equal("Revealed on turn 4", face.Note);
        var back = Assert.Single(hand, c => c.CardBack);
        Assert.Null(back.CardId);

        var row = Assert.Single(page.OpponentCards);
        Assert.Equal("CS2_182", row.CardId);
        Assert.Contains("played", row.Note);
    }

    [Fact]
    public void The_live_game_gets_nothing_from_the_future()
    {
        // One game fed line by line, as the live session reads it. At turn 5 the opponent holds 62, which it plays on
        // turn 7: what the overlay and the recorder hold at turn 5 must not change because of that.
        var parser = new PowerParser(new DateOnly(2026, 10, 23));
        var recorder = new ReplayRecorder();
        var tracker = new ConstructedTracker();
        parser.AddListener(recorder);
        parser.AddListener(tracker);
        Feed(parser, BeforeReveal);
        parser.Flush();
        var liveHand = tracker.BuildView(parser.Game!, CardDb.Empty).OpponentHand;
        var liveEvents = recorder.Events.ToList();

        Assert.Equal(2, liveHand.Count);
        Assert.All(liveHand, h => Assert.Null(h.CardId));
        Assert.DoesNotContain(liveEvents, e => e.Contains("CS2_182", StringComparison.Ordinal));
        Assert.DoesNotContain(liveEvents, e => e[0] is 'N' or 'K');

        // The same moment from a parser that never sees the rest is identical: the live view has no later data in it.
        var prefixOnly = new PowerParser(new DateOnly(2026, 10, 23));
        var prefixTracker = new ConstructedTracker();
        prefixOnly.AddListener(prefixTracker);
        Feed(prefixOnly, BeforeReveal);
        prefixOnly.Flush();
        Assert.Equal(prefixTracker.BuildView(prefixOnly.Game!, CardDb.Empty).OpponentHand, liveHand);

        Feed(parser, Reveal);
        parser.Flush();

        // What was recorded at turn 5 is not rewritten; the events never carry the post-game part, even after the game.
        Assert.Equal(liveEvents, recorder.Events.Take(liveEvents.Count));
        ReplayChecks.AssertNothingHidden(recorder.Events);
        Assert.DoesNotContain(recorder.Events, e => e[0] is 'N' or 'K');
        Assert.All(liveEvents.Where(e => e.Contains(" O HAND ", StringComparison.Ordinal)), e => Assert.Contains(" - - ", e));
        Assert.Contains(recorder.PostGame(), l => l == "K 62 CS2_182 7 played 0");
    }

    [Fact]
    public void Their_mulligan_shows_what_was_kept_and_replaced_once_revealed()
    {
        string[] game =
        [
            .. Setup.TakeWhile(l => !l.Contains("MULLIGAN_STATE", StringComparison.Ordinal)),
            .. Card(63, "", "HAND", 2, ""),
            .. Card(64, "", "HAND", 2, ""),
            .. Card(65, "", "HAND", 2, ""),
            .. Card(66, "", "DECK", 2, ""),
            Turn(1),
            Step("BEGIN_MULLIGAN"),
            Zone(64, "DECK"),
            Zone(66, "HAND"),
            .. Card(67, "GAME_005", "HAND", 2, "SPELL", "tag=COIN_CARD value=1"),
            "DebugPrintPower() - TAG_CHANGE Entity=velox#21276 tag=MULLIGAN_STATE value=DONE ",
            Step("MAIN_READY"),
            Turn(2),
            .. TheyPlay(63, "TIME_700"),
            Turn(3),
            Turn(4),
            Zone(64, "HAND"),
            .. TheyPlay(64, "BE_014"),
        ];
        Play(out var recorder, game);
        var replay = MatchReplay.Parse(recorder.Text(new ReplayHeader()))!;
        var mulligan = replay.OpponentMulligan!;
        Assert.Equal(["TIME_700", null], mulligan.Kept);
        Assert.Equal(["BE_014"], mulligan.Replaced);
        Assert.Equal([null], mulligan.Received);

        // The mulligan step shows it with the card backs it could not name.
        var page = MatchPage.Load(Game(_store.Save(Game(null).Key, recorder.Text(new ReplayHeader()))), _store, CardDb.Empty, null);
        var step = page.Step(replay.Frames.First(f => f.Action.Kind == ReplayActionKind.Mulligan).Index, ReplayPlayer.Local)!;
        var theirs = Assert.Single(step.Choices, c => c.Title == "Opponent's mulligan");
        Assert.Equal(["TIME_700", null, "BE_014"], theirs.Cards.Select(c => c.CardId));
        Assert.True(theirs.Cards[2].Dimmed);
        Assert.True(theirs.Cards[1].CardBack);
    }

    [Fact]
    public void The_likely_deck_is_the_closest_archetype_and_says_it_is_a_guess()
    {
        // Three revealed cards fit two archetypes equally: no clear call, so the closest (the more played) is the guess.
        Play(out var recorder,
        [
            .. Setup[..^1],
            .. Card(62, "", "DECK", 2, ""),
            .. Card(64, "", "DECK", 2, ""),
            .. Card(66, "", "DECK", 2, ""),
            Setup[^1],
            Zone(62, "HAND"),
            Zone(64, "HAND"),
            Zone(66, "HAND"),
            Turn(6),
            .. TheyPlay(62, "AAA_001"),
            .. TheyPlay(64, "AAA_002"),
            .. TheyPlay(66, "AAA_003"),
        ]);
        static MetaArchetype Archetype(int id, string name, int games, params string[] core) =>
            new(id, name, name, "WARRIOR", games, games / 2, 0.5, 0.1, 0, 0, 0, 0, core, []);
        var meta = new ConstructedMeta("standard", "all", "past_week", DateTime.UtcNow, 1000,
            [Archetype(1, "Big Warrior", 100, "AAA_001", "AAA_002", "AAA_003", "AAA_004"), Archetype(2, "Tempo Warrior", 50, "AAA_001", "AAA_002", "AAA_003", "AAA_005")], []);
        var game = Game(null) with { Format = "FT_STANDARD" };

        var page = MatchPage.Load(game with { Replay = _store.Save(game.Key, recorder.Text(new ReplayHeader())) }, _store, CardDb.Empty, meta);

        Assert.Null(page.OpponentArchetype);
        Assert.Contains("Big Warrior", page.OpponentPredictedNote);
        Assert.Contains("A guess", page.OpponentPredictedNote);
        Assert.Contains("only the closest", page.OpponentPredictedNote);
        Assert.Equal(["AAA_001", "AAA_002", "AAA_003", "AAA_004"], page.OpponentPredicted.Select(r => r.CardId).Order());
        Assert.Equal(3, page.OpponentPredicted.Count(r => r.Used));
        Assert.Equal(3, page.OpponentCards.Count);
    }

    [Fact]
    public void A_format_2_file_still_reads_without_reveals_and_is_replaced_on_the_next_save()

    {
        Play(out var recorder, [.. BeforeReveal, .. Reveal]);
        var text = recorder.Text(new ReplayHeader());
        var old = ReplayFile.PreviousMagic + string.Concat(text.Split('\n').Skip(1).Where(l => !(l.StartsWith("N ", StringComparison.Ordinal) || l.StartsWith("K ", StringComparison.Ordinal))).Select(l => "\n" + l));

        var replay = MatchReplay.Parse(old)!;
        Assert.False(replay.HasPostGame);
        Assert.Empty(replay.Reveals);
        Assert.All(replay.Frames.SelectMany(f => f.Opponent.Hand), c => Assert.Null(c.Reveal));

        var key = Game(null).Key;
        var name = _store.Save(key, old);
        Assert.StartsWith(ReplayFile.PreviousMagic, _store.Load(name));
        Assert.Equal(name, _store.Save(key, text));
        Assert.StartsWith(ReplayFile.Magic, _store.Load(name));
        // A current file is kept as it is.
        _store.Save(key, old);
        Assert.StartsWith(ReplayFile.Magic, _store.Load(name));
    }
}
