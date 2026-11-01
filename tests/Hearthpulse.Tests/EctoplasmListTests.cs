using Hearthpulse.Core.CardPool;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Power;
using Hearthpulse.Core.Tracking;

namespace Hearthpulse.Tests;

/// <summary>
/// Ectoplasm (CAP_805t), the spell Slime 'em! (CAP_805) gives each player: its "this game" list is your minions the cast
/// destroyed. The lines follow the owner's real logs of October 2026. The cast sets a copy of each minion aside with
/// CREATOR = the cast and COPIED_FROM_ENTITY_ID = the minion. Each player's Ectoplasm has the same CREATOR, and playing
/// it summons that player's copies (four games checked). The opponent's copies are logged without a card id.
/// The texts are read from the string tables, without switching the app's language: a switch off the UI thread trips
/// the view models other tests leave subscribed to it.
/// </summary>
public sealed class EctoplasmListTests
{
    private static readonly CardDb Cards = CardDb.Parse("enUS", new MemoryStream("""
        [
          {"id":"HERO_09","dbfId":813,"name":"Anduin","type":"HERO","cardClass":"PRIEST"},
          {"id":"CAP_805","dbfId":127092,"name":"Slime 'em!","type":"SPELL","cardClass":"PRIEST","cost":4},
          {"id":"CAP_805t","dbfId":127118,"name":"Ectoplasm","type":"SPELL","cardClass":"PRIEST","cost":3},
          {"id":"DR_1","dbfId":130002,"name":"Loot Hoarder","type":"MINION","cardClass":"NEUTRAL","cost":2,"rarity":"COMMON","mechanics":["DEATHRATTLE"]},
          {"id":"PLAIN","dbfId":130004,"name":"Chillwind Yeti","type":"MINION","cardClass":"NEUTRAL","cost":4,"rarity":"COMMON"},
          {"id":"DR_2","dbfId":130003,"name":"Abomination","type":"MINION","cardClass":"NEUTRAL","cost":5,"rarity":"RARE","mechanics":["DEATHRATTLE","TAUNT"]}
        ]
        """u8.ToArray()));

    [Fact]
    public void Ectoplasm_lists_your_minions_that_died_to_slime_em_and_nothing_hidden()
    {
        // An earlier cast (#13) whose Ectoplasm was already played: its copies stay aside but are not this one's.
        var earlier = new[] { Copy(310, "PLAIN", 1, 13, from: 300) };
        var board = new[] { Minion(40, "DR_1", 1), Minion(41, "PLAIN", 1), Minion(42, "DR_1", 1), Minion(50, "DR_2", 2) };
        var cast = new[]
        {
            // The opponent casts Slime 'em! (#35): an Ectoplasm for each player, set aside first.
            Card(218, "CAP_805t", "SETASIDE", 2, "SPELL", "tag=CREATOR value=35"),
            Card(222, "CAP_805t", "SETASIDE", 1, "SPELL", "tag=CREATOR value=35"),
            Copy(220, "DR_1", 1, 35, from: 40),
            Copy(221, "PLAIN", 1, 35, from: 41),
            Copy(223, "DR_1", 1, 35, from: 42),
            // The opponent's copy is logged without a card id.
            Card(224, string.Empty, "SETASIDE", 2, "MINION", "tag=CREATOR value=35"),
            // Something else the cast made aside (no COPIED_FROM_ENTITY_ID): not a minion that died.
            Card(225, "DR_2", "SETASIDE", 1, "MINION", "tag=CREATOR value=35"),
            // The opponent's copy were it logged with its card id, and a copied enchantment of ours: neither counts.
            Copy(226, "DR_2", 2, 35, from: 50),
            Copy(227, "DR_1", 1, 35, from: 40, type: "ENCHANTMENT"),
            Die(40, "DR_1", 1), Die(41, "PLAIN", 1), Die(42, "DR_1", 1), Die(50, "DR_2", 2),
        };
        var rule = MatchLists.RuleOf(MatchLists.Ectoplasm)!;
        Assert.Equal(new MatchRule(MatchEvent.SlimedDeaths, MatchSide.Friendly), rule);
        Assert.Null(MatchLists.RuleOf("CAP_805"));

        // Before the Ectoplasm reaches the hand there is no list yet.
        var (aside, asideTracker) = Play([.. earlier, .. board, .. cast]);
        Assert.Equal(0, MatchLists.Build(MatchLists.Ectoplasm, rule, aside, asideTracker.Counters, Cards).Total);

        var (game, tracker) = Play([.. earlier, .. board, .. cast, [.. ToHand(222, 1), .. ToHand(218, 2)]]);
        var list = MatchLists.Build(MatchLists.Ectoplasm, rule, game, tracker.Counters, Cards);
        Assert.Equal("Ectoplasm", list.SourceName);
        Assert.Equal(MatchLists.Title(rule), list.Title);
        Assert.Equal(3, list.Total);
        Assert.Equal([("DR_1", 2), ("PLAIN", 1)], list.Rows.Select(r => (r.CardId, r.Count)));

        // Played, the Ectoplasm leaves the hand: no list any more.
        var (played, playedTracker) = Play([.. earlier, .. board, .. cast, [.. ToHand(222, 1), $"DebugPrintPower() - TAG_CHANGE Entity=[entityName=X id=222 zone=HAND zonePos=1 cardId=CAP_805t player=1] tag=ZONE value=PLAY "]]);
        Assert.Equal(0, MatchLists.Build(MatchLists.Ectoplasm, rule, played, playedTracker.Counters, Cards).Total);
    }

    [Fact]
    public void Two_ectoplasms_from_two_casts_list_the_leftmost_ones_minions()
    {
        // Each Ectoplasm brings back its own cast's minions. The hover tells only the card id, so the list goes with
        // the leftmost Ectoplasm in hand, the one the panel stands beside.
        string[][] casts =
        [
            Minion(40, "DR_1", 1), Minion(41, "PLAIN", 1),
            Card(222, "CAP_805t", "SETASIDE", 1, "SPELL", "tag=CREATOR value=35"),
            Copy(220, "DR_1", 1, 35, from: 40),
            Die(40, "DR_1", 1),
            Card(232, "CAP_805t", "SETASIDE", 1, "SPELL", "tag=CREATOR value=36"),
            Copy(230, "PLAIN", 1, 36, from: 41),
            Die(41, "PLAIN", 1),
        ];
        var rule = MatchLists.RuleOf(MatchLists.Ectoplasm)!;

        var (later, laterTracker) = Play([.. casts, [.. ToHand(232, 1, position: 1), .. ToHand(222, 1, position: 2)]]);
        Assert.Equal(["PLAIN"], MatchLists.Build(MatchLists.Ectoplasm, rule, later, laterTracker.Counters, Cards).Rows.Select(r => r.CardId));

        var (first, firstTracker) = Play([.. casts, [.. ToHand(222, 1, position: 1), .. ToHand(232, 1, position: 2)]]);
        Assert.Equal(["DR_1"], MatchLists.Build(MatchLists.Ectoplasm, rule, first, firstTracker.Counters, Cards).Rows.Select(r => r.CardId));
    }

    [Fact]
    public void The_list_title_names_slime_em_in_both_languages()
    {
        Assert.Equal("Your minions that died to Slime 'em!", Loc.Table(Loc.English)["Pool.List.SlimedDeaths"]);
        Assert.Equal("Ваши погибшие скользкой смертью", Loc.Table(Loc.Russian)["Pool.List.SlimedDeaths"]);
    }

    private static (GameState Game, ConstructedTracker Tracker) Play(string[][] bodies)
    {
        var parser = new PowerParser(new DateOnly(2026, 10, 30));
        var tracker = new ConstructedTracker();
        parser.AddListener(tracker);
        parser.AddListener(tracker.Counters);
        string[] setup =
        [
            "DebugPrintPower() - CREATE_GAME",
            "DebugPrintPower() -     GameEntity EntityID=1",
            "DebugPrintPower() -         tag=CARDTYPE value=GAME",
            "DebugPrintPower() -         tag=TURN value=9",
            "DebugPrintPower() -     Player EntityID=2 PlayerID=1 GameAccountId=[hi=144115198130930503 lo=11]",
            "DebugPrintPower() -         tag=CONTROLLER value=1",
            "DebugPrintPower() -         tag=CARDTYPE value=PLAYER",
            "DebugPrintPower() -         tag=HERO_ENTITY value=20",
            "DebugPrintPower() -     Player EntityID=3 PlayerID=2 GameAccountId=[hi=144115198130930503 lo=22]",
            "DebugPrintPower() -         tag=CONTROLLER value=2",
            "DebugPrintPower() -         tag=CARDTYPE value=PLAYER",
            .. Card(20, "HERO_09", "PLAY", 1, "HERO"),
            "DebugPrintGame() - GameType=GT_RANKED",
            "DebugPrintGame() - FormatType=FT_STANDARD",
            "DebugPrintGame() - PlayerID=1, PlayerName=velox#21276",
            "DebugPrintGame() - PlayerID=2, PlayerName=UNKNOWN HUMAN PLAYER",
            // A known card in hand tells the parser which player is local, as in every real game.
            .. Card(99, "DR_2", "HAND", 1, "MINION"),
        ];
        foreach (var line in setup.Concat(bodies.SelectMany(b => b)))
        {
            parser.Feed("D 12:30:00.0000000 GameState." + line);
        }

        parser.Flush();
        return (parser.Game!, tracker);
    }

    private static string[] Card(int id, string cardId, string zone, int controller, string type, params string[] tags) =>
    [
        $"DebugPrintPower() - FULL_ENTITY - Creating ID={id} CardID={cardId}",
        $"DebugPrintPower() -     tag=ZONE value={zone}",
        $"DebugPrintPower() -     tag=CONTROLLER value={controller}",
        .. cardId.Length > 0 ? new[] { $"DebugPrintPower() -     tag=CARDTYPE value={type}" } : [],
        .. tags.Select(t => "DebugPrintPower() -     " + t),
    ];

    private static string[] Minion(int id, string cardId, int controller) => Card(id, cardId, "PLAY", controller, "MINION");

    /// <summary>The copy the cast sets aside, then marks with the minion it copies, as the real log does.</summary>
    private static string[] Copy(int id, string cardId, int controller, int cast, int from, string type = "MINION") =>
    [
        .. Card(id, cardId, "SETASIDE", controller, type, $"tag=CREATOR value={cast}"),
        $"DebugPrintPower() - TAG_CHANGE Entity={id} tag=COPIED_FROM_ENTITY_ID value={from} ",
    ];

    private static string[] Die(int id, string cardId, int controller) =>
        [$"DebugPrintPower() - TAG_CHANGE Entity=[entityName=X id={id} zone=PLAY zonePos=1 cardId={cardId} player={controller}] tag=ZONE value=GRAVEYARD "];

    /// <summary>The Ectoplasm comes into the hand at <paramref name="position"/> (1 = leftmost).</summary>
    private static string[] ToHand(int id, int controller, int position = 1) =>
    [
        $"DebugPrintPower() - TAG_CHANGE Entity=[entityName=X id={id} zone=SETASIDE zonePos=0 cardId=CAP_805t player={controller}] tag=ZONE value=HAND ",
        $"DebugPrintPower() - TAG_CHANGE Entity={id} tag=ZONE_POSITION value={position} ",
    ];
}
