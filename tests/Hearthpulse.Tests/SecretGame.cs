using System.Text.Json.Nodes;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Power;
using Hearthpulse.Core.Secrets;

namespace Hearthpulse.Tests;

/// <summary>
/// A Constructed game in synthetic Power.log lines (the shapes of the client's real lines) for the Secrets helper:
/// you are player 1 (entity 2, hero 20), the opponent is player 2 (entity 3, hero 30). Card backs 50-59 sit in the
/// opponent's starting deck; your own cards are made with <see cref="LocalCard"/>.
/// </summary>
internal sealed class SecretGame
{
    public const int OpponentHero = 30;
    private const string Prefix = "D 12:30:00.0000000 GameState.";

    private readonly PowerParser _parser = new(new DateOnly(2026, 10, 25));
    private int _turn;
    private int _nextId = 100;

    public static CardDb Cards { get; } = LoadCards();

    public SecretTracker Tracker { get; } = new();

    public GameState Game => _parser.Game!;

    public SecretGame(string opponentHero = "HERO_08", string format = "FT_STANDARD", string gameType = "GT_RANKED")
    {
        _parser.AddListener(Tracker);
        List<string> setup =
        [
            "DebugPrintPower() - CREATE_GAME",
            "DebugPrintPower() -     GameEntity EntityID=1",
            "DebugPrintPower() -         tag=CARDTYPE value=GAME",
            "DebugPrintPower() -     Player EntityID=2 PlayerID=1 GameAccountId=[hi=144115198130930503 lo=11]",
            "DebugPrintPower() -         tag=CONTROLLER value=1",
            "DebugPrintPower() -         tag=CARDTYPE value=PLAYER",
            "DebugPrintPower() -         tag=HERO_ENTITY value=20",
            "DebugPrintPower() -     Player EntityID=3 PlayerID=2 GameAccountId=[hi=144115198130930503 lo=22]",
            "DebugPrintPower() -         tag=CONTROLLER value=2",
            "DebugPrintPower() -         tag=CARDTYPE value=PLAYER",
            "DebugPrintPower() -         tag=HERO_ENTITY value=30",
            "DebugPrintPower() - FULL_ENTITY - Creating ID=20 CardID=HERO_09",
            "DebugPrintPower() -     tag=ZONE value=PLAY",
            "DebugPrintPower() -     tag=CONTROLLER value=1",
            "DebugPrintPower() -     tag=CARDTYPE value=HERO",
            $"DebugPrintPower() - FULL_ENTITY - Creating ID=30 CardID={opponentHero}",
            "DebugPrintPower() -     tag=ZONE value=PLAY",
            "DebugPrintPower() -     tag=CONTROLLER value=2",
            "DebugPrintPower() -     tag=CARDTYPE value=HERO",
            $"DebugPrintGame() - GameType={gameType}",
            $"DebugPrintGame() - FormatType={format}",
            "DebugPrintGame() - PlayerID=1, PlayerName=velox#21276",
            "DebugPrintGame() - PlayerID=2, PlayerName=UNKNOWN HUMAN PLAYER",
            // Your opening hand card: it tells the parser which player you are.
            "DebugPrintPower() - FULL_ENTITY - Creating ID=99 CardID=CS2_029",
            "DebugPrintPower() -     tag=ZONE value=HAND",
            "DebugPrintPower() -     tag=CONTROLLER value=1",
            "DebugPrintPower() -     tag=CARDTYPE value=SPELL",
        ];
        for (var id = 50; id < 60; id++)
        {
            setup.Add($"DebugPrintPower() - FULL_ENTITY - Creating ID={id} CardID=");
            setup.Add("DebugPrintPower() -     tag=ZONE value=DECK");
            setup.Add("DebugPrintPower() -     tag=CONTROLLER value=2");
        }

        Feed([.. setup]);
    }

    public void Feed(params string[] bodies)
    {
        foreach (var body in bodies)
        {
            _parser.Feed(Prefix + body);
        }
    }

    /// <summary>The view after the last action has resolved (the session does this once the log goes quiet).</summary>
    public SecretsView? View()
    {
        _parser.Flush();
        Tracker.CommitIfQuiet(Game);
        return Tracker.BuildView(Game, Cards);
    }

    /// <summary>Keys of the Secrets the opponent's Secrets can still be.</summary>
    public IReadOnlyList<string> Open() => View()?.Rows.Where(r => r.IsOpen).Select(r => r.Key).ToList() ?? [];

    public static string Ref(int id, string zone, int player, string cardId = "") =>
        $"[entityName=X id={id} zone={zone} zonePos=1 cardId={cardId} player={player}]";

    private static string Tag(string entity, string tag, object value) => $"DebugPrintPower() -     TAG_CHANGE Entity={entity} tag={tag} value={value} ";

    private static string Step(string step) => $"DebugPrintPower() - TAG_CHANGE Entity=GameEntity tag=STEP value={step} ";

    private static string BlockStart(string type, string entity) =>
        $"DebugPrintPower() - BLOCK_START BlockType={type} Entity={entity} EffectCardId=System.Collections.Generic.List`1[System.String] EffectIndex=0 Target=0 SubOption=-1 ";

    private const string BlockEnd = "DebugPrintPower() - BLOCK_END";

    /// <summary>A new turn for player 1 (you) or 2, through to the main action step.</summary>
    public void Turn(int player, int mana = 10)
    {
        _turn++;
        var (me, other) = player == 1 ? ("2", "3") : ("3", "2");
        Feed(
            Step("MAIN_NEXT"),
            Tag(other, "CURRENT_PLAYER", 0),
            Tag(me, "CURRENT_PLAYER", 1),
            Tag("GameEntity", "TURN", _turn),
            Step("MAIN_READY"),
            Tag(me, "RESOURCES", mana),
            Tag(me, "RESOURCES_USED", 0),
            Tag(me, "NUM_CARDS_PLAYED_THIS_TURN", 0),
            Tag(me, "NUM_CARDS_DRAWN_THIS_TURN", 0),
            Step("MAIN_START_TRIGGERS"),
            Step("MAIN_START"),
            Step("MAIN_ACTION"));
    }

    public void EndTurn() => Feed(Step("MAIN_END"), Step("MAIN_CLEANUP"));

    /// <summary>The opponent draws card back <paramref name="id"/> and plays it as a Secret of <paramref name="cardClass"/>.</summary>
    public void OpponentSecret(int id, string cardClass, int paid)
    {
        var spent = Game.OpponentPlayerEntity!.GetInt("RESOURCES_USED") + paid;
        Feed(
            $"DebugPrintPower() - TAG_CHANGE Entity={Ref(id, "DECK", 2)} tag=ZONE value=HAND ",
            BlockStart("PLAY", Ref(id, "HAND", 2)),
            Tag("3", "RESOURCES_USED", spent),
            Tag(Ref(id, "HAND", 2), "CLASS", cardClass),
            Tag(Ref(id, "HAND", 2), "SECRET", 1),
            Tag(Ref(id, "HAND", 2), "ZONE", "SECRET"),
            BlockEnd);
    }

    /// <summary>The opponent's Secret <paramref name="id"/> fires and is revealed as <paramref name="cardId"/>.</summary>
    public string[] Fires(int id, string cardId) =>
    [
        BlockStart("TRIGGER", Ref(id, "SECRET", 2)),
        $"DebugPrintPower() -     SHOW_ENTITY - Updating Entity={Ref(id, "SECRET", 2)} CardID={cardId}",
        "DebugPrintPower() -         tag=CARDTYPE value=SPELL",
        Tag(Ref(id, "SECRET", 2, cardId), "ZONE", "GRAVEYARD"),
        BlockEnd,
    ];

    /// <summary>Makes a card of yours in <paramref name="zone"/>; returns its entity id.</summary>
    public int LocalCard(string cardId, string type, string zone = "HAND", params string[] tags)
    {
        var id = _nextId++;
        Feed(
            [
                $"DebugPrintPower() - FULL_ENTITY - Creating ID={id} CardID={cardId}",
                $"DebugPrintPower() -     tag=ZONE value={zone}",
                "DebugPrintPower() -     tag=CONTROLLER value=1",
                $"DebugPrintPower() -     tag=CARDTYPE value={type}",
                .. tags.Select(t => "DebugPrintPower() -     " + t),
            ]);
        return id;
    }

    /// <summary>Makes an opponent minion on the board; returns its entity id.</summary>
    public int OpponentMinion(string cardId = "CS2_182")
    {
        var id = _nextId++;
        Feed(
            $"DebugPrintPower() - FULL_ENTITY - Creating ID={id} CardID={cardId}",
            "DebugPrintPower() -     tag=ZONE value=PLAY",
            "DebugPrintPower() -     tag=CONTROLLER value=2",
            "DebugPrintPower() -     tag=CARDTYPE value=MINION");
        return id;
    }

    /// <summary>You play card <paramref name="id"/> from your hand (a minion lands on the board).</summary>
    public void Play(int id, string cardId, int cost = 1, int? target = null, params string[] during)
    {
        var minion = Game.Entities[id].IsMinion;
        var spent = Game.LocalPlayerEntity!.GetInt("RESOURCES_USED") + cost;
        var played = Game.LocalPlayerEntity!.GetInt("NUM_CARDS_PLAYED_THIS_TURN") + 1;
        Feed(
            [
                .. target is { } t ? [Tag(Ref(id, "HAND", 1, cardId), "CARD_TARGET", t)] : Array.Empty<string>(),
                BlockStart("PLAY", Ref(id, "HAND", 1, cardId)),
                Tag("2", "RESOURCES_USED", spent),
                Tag("2", "NUM_CARDS_PLAYED_THIS_TURN", played),
                Tag(Ref(id, "HAND", 1, cardId), "ZONE", minion ? "PLAY" : "GRAVEYARD"),
                .. during,
                BlockEnd,
            ]);
    }

    /// <summary><paramref name="attacker"/> attacks <paramref name="defender"/>, which takes <paramref name="damage"/>.</summary>
    public void Attack(int attacker, int defender, int damage = 0, params string[] during)
    {
        var dealt = Game.Entities[defender].GetInt("DAMAGE") + damage;
        Feed(
            [
                BlockStart("ATTACK", Ref(attacker, "PLAY", 1)),
                Tag("GameEntity", "PROPOSED_ATTACKER", attacker),
                Tag("GameEntity", "PROPOSED_DEFENDER", defender),
                .. during,
                .. damage > 0 ? [Tag(Ref(defender, "PLAY", 2), "DAMAGE", dealt)] : Array.Empty<string>(),
                Tag("GameEntity", "PROPOSED_ATTACKER", 0),
                Tag("GameEntity", "PROPOSED_DEFENDER", 0),
                BlockEnd,
            ]);
    }

    /// <summary>Opponent minion <paramref name="id"/> dies (a DEATHS block, as the client writes it).</summary>
    public void Dies(int id, params string[] during) =>
        Feed(
        [
            BlockStart("DEATHS", "GameEntity"),
            Tag(Ref(id, "PLAY", 2), "ZONE", "GRAVEYARD"),
            .. during,
            BlockEnd,
        ]);

    private static CardDb LoadCards()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Secrets", "secrets.enUS.json");
        var cards = JsonNode.Parse(File.ReadAllText(path))!.AsArray();
        foreach (var extra in Extra)
        {
            cards.Add(JsonNode.Parse(extra));
        }

        return CardDb.Parse("enUS", new MemoryStream(System.Text.Encoding.UTF8.GetBytes(cards.ToJsonString())));
    }

    private static string[] Extra =>
    [
        """{"id":"HERO_08","dbfId":637,"name":"Jaina Proudmoore","type":"HERO","cardClass":"MAGE"}""",
        """{"id":"HERO_05","dbfId":31,"name":"Rexxar","type":"HERO","cardClass":"HUNTER"}""",
        """{"id":"HERO_04","dbfId":671,"name":"Uther Lightbringer","type":"HERO","cardClass":"PALADIN"}""",
        """{"id":"HERO_03","dbfId":930,"name":"Valeera Sanguinar","type":"HERO","cardClass":"ROGUE"}""",
        """{"id":"HERO_09","dbfId":813,"name":"Anduin Wrynn","type":"HERO","cardClass":"PRIEST"}""",
        """{"id":"CS2_182","dbfId":1182,"name":"Chillwind Yeti","type":"MINION","cardClass":"NEUTRAL","cost":4,"collectible":true}""",
        """{"id":"CS2_029","dbfId":315,"name":"Fireball","type":"SPELL","cardClass":"MAGE","cost":4,"collectible":true}""",
        """{"id":"CS2_106","dbfId":401,"name":"Fiery War Axe","type":"WEAPON","cardClass":"WARRIOR","cost":3,"collectible":true}""",
        """{"id":"GAME_005","dbfId":1746,"name":"The Coin","type":"SPELL","cardClass":"NEUTRAL","cost":0}""",
    ];
}
