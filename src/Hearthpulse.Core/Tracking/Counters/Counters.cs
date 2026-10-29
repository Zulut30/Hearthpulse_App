using System.Globalization;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Power;

namespace Hearthpulse.Core.Tracking.Counters;

/// <summary>When a counter is shown: when a card that cares about it is in play (Auto), always, or never.</summary>
public enum CounterMode
{
    Auto,
    Always,
    Off,
}

/// <summary>One counter as the overlay shows it.</summary>
/// <param name="Id">The counter's id (settings key); quests use "quests".</param>
/// <param name="Value">The number as shown: "2/4", "7", "Next: 3".</param>
/// <param name="Progress">0..1 toward a goal, when the counter has one.</param>
/// <param name="IconCardId">A card of this player that cares about the count, for the chip's art.</param>
/// <param name="Details">Lines for the tooltip: what was counted.</param>
/// <param name="Relevant">A card that cares about it was seen, or the count is under way: shown in Auto mode.</param>
public sealed record CounterView(string Id, string Title, string Value, double? Progress, bool Opponent, string? IconCardId, string Description,
    IReadOnlyList<string> Details, bool Relevant)
{
    /// <summary>What was counted, by card, in the order it was first counted (spells cast, minions that died...).</summary>
    public IReadOnlyList<CounterCard> Counted { get; init; } = [];

    /// <summary>The player's cards that use the count (in the deck or seen this game), with the copies known.</summary>
    public IReadOnlyList<CounterCard> RelatedCards { get; init; } = [];

    /// <summary>The parts of the goal, each marked done or not (the four C'Thun abilities, the nine Rafaams).</summary>
    public IReadOnlyList<CounterCard> Parts { get; init; } = [];

    /// <summary>How many steps the goal has ("2/4" has 4), when it is counted in steps.</summary>
    public int? Goal { get; init; }
}

/// <summary>A card in a counter's tooltip.</summary>
/// <param name="Count">Times it was counted, or copies of a related card; 0 for goal parts.</param>
/// <param name="Done">For goal parts: whether the part is done; null for other cards.</param>
public sealed record CounterCard(string CardId, string Name, int Count, bool? Done = null);

/// <summary>A counter as the settings page lists it.</summary>
public sealed record CounterInfo(string Id, string Title, string Description);

/// <summary>
/// The in-game counters (HSReplay/Deck Tracker style): numbers that cards care about, for you and your opponent.
/// Values come from the tags the client keeps on each player (C'Thun abilities, Herald, Corpses, spells cast...) and
/// from <see cref="CounterTracker"/> for what the client does not count. A counter shows in Auto mode when a card
/// that cares about it is in the player's deck or has been seen (<see cref="CounterCards"/>), or when the count
/// itself is under way (a quest, C'Thun abilities, Heralds).
/// </summary>
public static class Counters
{
    private static readonly string[] SchoolNames = ["", "Arcane", "Fire", "Frost", "Nature", "Holy", "Shadow", "Fel"];

    private static readonly CounterDef[] Defs =
    [
        new("cthun", "C'Thun abilities", "C'Thun abilities triggered this game. C'Thun, Eye of the Empire triples its abilities at 4/4.", c =>
        {
            // The abilities are the tokens BE_013t1..t4; real logs set CTHUN_ABILITYn_USED when BE_013tn resolves.
            var used = Enumerable.Range(1, 4).Where(i => c.Tag($"CTHUN_ABILITY{i}_USED") > 0).ToList();
            return new($"{used.Count}/4", used.Count / 4.0, Active: used.Count > 0, Goal: 4,
                Parts: Enumerable.Range(1, 4).Select(i => ($"BE_013t{i}", used.Contains(i))).ToList());
        }),
        new("herald", "Herald", "Heralds this game. Every two Heralds upgrade your class Colossal.", c =>
        {
            var n = c.Tag("HERALD_COLOSSAL_AMOUNT");
            var next = 2 - n % 2;
            return new(n.ToString(CultureInfo.InvariantCulture), n % 2 / 2.0, [Loc.P("Core.Counter.HeraldNext", next)], n > 0, Goal: 2);
        }),
        new("corpses", "Corpses", "Corpses you have to spend.", c => Num(c.Tag("CORPSES"))),
        new("jade", "Jade Golem", "Size of your next Jade Golem.", c =>
        {
            var next = c.Tag("JADE_GOLEM") + 1;
            return new($"{next}/{next}", Counted: c.PlayedIds(p => CounterCards.Related["jade"].Contains(p.CardId)));
        }),
        new("spells-cast", "Spells cast", "Spells you've cast this game.",
            c => Num(Math.Max(c.Tag("NUM_SPELLS_PLAYED_THIS_GAME"), c.Played(p => p.CardType == "SPELL")), counted: c.PlayedIds(p => p.CardType == "SPELL"))),
        new("spell-schools", "Spell schools", "Different spell schools you've cast this game.", c =>
        {
            var spells = c.Log.Played.Where(p => p.CardType == "SPELL" && p.SpellSchool is > 0 and < 8).ToList();
            var schools = spells.Select(p => p.SpellSchool).Distinct().Order().ToList();
            return new(schools.Count.ToString(CultureInfo.InvariantCulture), Details: schools.Select(s => Loc.Or($"Core.SpellSchool.{SchoolNames[s]}", SchoolNames[s])).ToList(),
                Counted: spells.OrderBy(p => p.SpellSchool).Select(p => p.CardId).ToList());
        }),
        new("spells-arcane", "Arcane spells", "Arcane spells you've cast this game.", c => School(c, 1)),
        new("spells-fire", "Fire spells", "Fire spells you've cast this game.", c => School(c, 2)),
        new("spells-frost", "Frost spells", "Frost spells you've cast this game.", c => School(c, 3)),
        new("spells-nature", "Nature spells", "Nature spells you've cast this game.", c => School(c, 4)),
        new("spells-holy", "Holy spells", "Holy spells you've cast this game.", c => School(c, 5)),
        new("spells-shadow", "Shadow spells", "Shadow spells you've cast this game.", c => School(c, 6)),
        new("spells-fel", "Fel spells", "Fel spells you've cast this game.", c => School(c, 7)),
        new("minion-types", "Minion types", "Different minion types you've played this game.", c =>
        {
            var typed = c.Log.Played.Where(p => p.CardType == "MINION").Select(p => (p.CardId, Races: RacesOf(c, p.CardId))).Where(p => p.Races.Count > 0).ToList();
            var types = typed.SelectMany(p => p.Races).Distinct().Order().ToList();
            return new(types.Count.ToString(CultureInfo.InvariantCulture), Details: types.Select(t => Loc.Or($"Core.MinionType.{t}", Title(t))).ToList(), Counted: typed.Select(p => p.CardId).ToList());
        }),
        new("elemental-streak", "Elemental streak", "Turns in a row you've played an Elemental (this turn or last turn keeps it going).", c =>
        {
            var elementals = c.Log.Played.Where(p => c.Cards.Get(p.CardId)?.HasRace("ELEMENTAL") == true).ToList();
            var turns = elementals.Select(p => p.Turn).ToHashSet();
            var turn = CounterTracker.PlayerTurn(c.Game);
            var start = turns.Contains(turn) ? turn : turn - 1;
            var streak = 0;
            while (turns.Contains(start - streak))
            {
                streak++;
            }

            return new(streak.ToString(CultureInfo.InvariantCulture), Details: [Loc.T(turns.Contains(turn) ? "Core.Counter.ElementalThisTurn" : "Core.Counter.NoElementalThisTurn")],
                Counted: elementals.Where(p => p.Turn > start - streak && p.Turn <= start).Select(p => p.CardId).ToList());
        }),
        new("dragons", "Dragons played", "Dragons you've played this game.", c => PlayedNum(c, p => c.Cards.Get(p.CardId)?.HasRace("DRAGON") == true)),
        new("beasts-summoned", "Beasts summoned", "Beasts you've summoned this game.", c => Ids(c.Log.Summoned.Where(id => c.Cards.Get(id)?.HasRace("BEAST") == true))),
        new("totems", "Totems summoned", "Totems you've summoned this game.", c => Ids(c.Log.Summoned.Where(id => c.Cards.Get(id)?.HasRace("TOTEM") == true))),
        new("deathrattle-summoned", "Deathrattle minions", "Deathrattle minions you've summoned this game (N'Zoth, Dream's End).",
            c => Ids(c.Log.Summoned.Where(id => c.Cards.Get(id)?.Mechanics.Contains("DEATHRATTLE") == true))),
        new("minion-mana", "Mana on minions", "Mana you've spent on minions this game (Y'Shaarj needs 30).", c =>
        {
            var spent = c.Log.Played.Where(p => p.CardType == "MINION").Sum(p => p.Cost);
            return new($"{Math.Min(spent, 30)}/30", Math.Min(1, spent / 30.0), spent > 30 ? [Loc.F("Core.Counter.ManaInTotal", spent)] : null,
                Counted: c.PlayedIds(p => p.CardType == "MINION"));
        }),
        new("friendly-deaths", "Friendly minions died", "Your minions that died this game.",
            c => Num(Math.Max(c.Tag("NUM_FRIENDLY_MINIONS_THAT_DIED_THIS_GAME"), c.Log.Died.Count), Last(c, c.Log.Died), c.Log.Died)),
        new("minions-died", "Minions died", "Minions that died this game, both sides.",
            c => Num(c.AllDied, counted: c.Game.Players.Keys.SelectMany(id => c.Tracker.For(id).Died).ToList())),
        new("not-from-deck", "Cards not from deck", "Cards you've played that didn't start in your deck.", c => PlayedNum(c, p => !p.FromDeck)),
        new("other-class", "Other-class cards", "Cards from another class you've played this game.", c => PlayedNum(c, p =>
            c.Cards.Get(p.CardId)?.CardClass is { Length: > 0 } cls && cls != "NEUTRAL" && !string.Equals(cls, c.PlayerClass, StringComparison.OrdinalIgnoreCase))),
        new("secrets-played", "Secrets played", "Secrets you've played this game.", c => PlayedNum(c, p => c.Cards.Get(p.CardId)?.Mechanics.Contains("SECRET") == true)),
        new("cards-played", "Cards played", "Cards you've played this game.", c => PlayedNum(c, _ => true)),
        new("cards-drawn", "Cards drawn", "Cards you've drawn this game.", c => Num(c.Log.Drawn)),
        new("cards-discarded", "Cards discarded", "Cards you've discarded this game.", c => Num(c.Log.Discarded.Count, Last(c, c.Log.Discarded), c.Log.Discarded)),
        new("overload", "Mana Overloaded", "Mana Crystals you've Overloaded this game.",
            c => Num(c.Log.Played.Sum(p => p.Overload), counted: c.PlayedIds(p => p.Overload > 0))),
        new("imbue", "Imbues", "Times you've Imbued your Hero Power this game.", c =>
            Num(c.Log.Played.Sum(p => CounterCards.ImbueSources.GetValueOrDefault(p.CardId)), counted: c.PlayedIds(p => CounterCards.ImbueSources.ContainsKey(p.CardId)))),
        new("hero-power", "Hero Power uses", "Times you've used your Hero Power this game.", c => Num(c.Tag("NUM_TIMES_HERO_POWER_USED_THIS_GAME"))),
        new("healed", "Healing done", "Health you've restored this game.", c => Num(c.Tag("AMOUNT_HEALED_THIS_GAME"))),
        new("armor", "Armor gained", "Armor you've gained this game.", c => Num(c.Tag("ARMOR_GAINED_THIS_GAME"))),
        new("friendly-attacks", "Attacks", "Times your characters attacked this game.", c => Num(c.Log.Attacks)),
        new("fatigue", "Fatigue", "Damage of your next fatigue draw. Shown when your deck is almost empty or a card deals Fatigue.", c =>
        {
            var next = c.Tag("FATIGUE") + 1;
            var deck = c.Game.Controlled(c.PlayerId, "DECK").Count();
            return new(Loc.F("Core.Counter.Next", next), Details: [Loc.P("Core.Counter.CardsLeftInDeck", deck)], Active: deck <= 3);
        }),
        new("cards-drawn-turn", "Cards drawn this turn", "Cards you've drawn this turn.", c => Num(c.Tag("NUM_CARDS_DRAWN_THIS_TURN"))),
        // The tag of the player whose turn just ended is reset a turn late, so it only counts on the player's own turn.
        new("cards-played-turn", "Cards played this turn", "Cards you've played this turn.", c =>
        {
            var current = CounterTracker.IsCurrentPlayer(c.Game, c.PlayerId);
            var turn = CounterTracker.PlayerTurn(c.Game);
            return Num(current ? c.Tag("NUM_CARDS_PLAYED_THIS_TURN") : 0, counted: current ? c.PlayedIds(p => p.Turn == turn) : null);
        }),
        new("minions-died-turn", "Minions died this turn", "Minions that died this turn, both sides.", c =>
        {
            var all = c.Game.Players.Values.Sum(p => c.Game.Entities.GetValueOrDefault(p.EntityId)?.GetInt("NUM_FRIENDLY_MINIONS_THAT_DIED_THIS_TURN") ?? 0);
            return Num(all, [Loc.F("Core.Counter.Yours", c.Tag("NUM_FRIENDLY_MINIONS_THAT_DIED_THIS_TURN"))]);
        }),
        new("cards-2-mana", "Cards played for 2 Mana", "Cards you've played that cost 2 Mana when played.", c => PlayedNum(c, p => p.Cost == 2)),
        new("expensive-spells", "Spells (5+) cast", "Spells you've cast this game that cost 5 or more (Dragoncaller Alanna).",
            c => PlayedNum(c, p => p.CardType == "SPELL" && p.Cost >= 5)),
        new("paladin-cards", "Paladin cards played", "Paladin cards you've played this game.", c => PlayedNum(c, p => c.Cards.Get(p.CardId)?.CardClass == "PALADIN")),
        new("spells-on-friendly", "Spells on friendly characters", "Spells you've cast on your own characters this game.",
            c => PlayedNum(c, p => p.CardType == "SPELL" && TargetOf(c, p) is { } target && target.Controller == c.PlayerId)),
        new("spells-on-characters", "Spells on characters", "Spells you've cast on any character this game.",
            c => PlayedNum(c, p => p.CardType == "SPELL" && TargetOf(c, p) is not null)),
        new("dragons-summoned", "Dragons summoned", "Dragons you've summoned this game.", c => Ids(c.Log.Summoned.Where(id => c.Cards.Get(id)?.HasRace("DRAGON") == true))),
        new("hero-attacks", "Hero attacks", "Times your hero attacked this game (Infest the Scullery).", c => Num(c.Log.HeroAttacks)),
        new("self-damage", "Damage taken on your turns", "Damage your hero has taken on your own turns this game (Imprisoned Horror).",
            c => Num(c.Log.DamageTakenOnOwnTurn)),
        new("discovers", "Discovers", "Cards you've Discovered this game.", c =>
        {
            // Only the local player's picks are in the log.
            if (c.PlayerId != c.Game.LocalPlayerId)
            {
                return null;
            }

            var turn = CounterTracker.PlayerTurn(c.Game);
            var picks = c.Log.Choices.Where(p => p.Source.Has("DISCOVER") || p.Source.Has("USE_DISCOVER_VISUALS")
                || c.Cards.Get(p.Source.CardId)?.Mechanics.Contains("DISCOVER") == true).ToList();
            return Num(picks.Count, [Loc.F("Core.Counter.ThisTurn", picks.Count(p => p.Turn == turn))], picks.Select(p => p.Source.CardId));
        }),
        new("abyssal-curse", "Abyssal Curse", "Damage of the next Abyssal Curse you give: each Curse is worse than the last.",
            c => new(Loc.F("Core.Counter.Next", 1 + c.Source("abyssal-curse")), Counted: c.SourceCards("abyssal-curse"))),
        new("animal-companion", "Animal Companion", "How much more your future Animal Companions cost (they become random Beasts).",
            c => new(Loc.F("Core.Counter.CostMore", c.Source("animal-companion")), Counted: c.SourceCards("animal-companion"))),
        new("extra-companion", "Extra Animal Companion", "Extra Animal Companions your cards summon this game.",
            c => new($"+{c.Source("extra-companion")}", Counted: c.SourceCards("extra-companion"))),
        new("asteroid", "Asteroid damage", "Damage your Asteroids deal.", c => new(Loc.F("Core.Counter.Damage", 2 + c.Source("asteroid")), Counted: c.SourceCards("asteroid"))),
        new("astral-automaton", "Astral Automaton", "Bonus of your next Astral Automaton: +1/+1 for each one you've summoned.", c =>
        {
            var summoned = c.Log.Summoned.Where(id => id == "TTN_401").ToList();
            return new($"+{summoned.Count}/+{summoned.Count}", Counted: summoned);
        }),
        new("chaotic-tendril", "Chaotic Tendril", "Cost of the spell your next Chaotic Tendril casts.", c => Next(c, "YOG_514", "Core.Counter.NextSpell")),
        new("void-soul", "Void Soul", "Cost of the Demon your next Void Soul summons.", c => Next(c, "JAIL_732", "Core.Counter.NextDemon")),
        new("pogo-hopper", "Pogo-Hopper", "Bonus of your next Pogo-Hopper: +2/+2 for each other one you've played.", c =>
        {
            var n = 2 * c.Plays("BOT_283");
            return new($"+{n}/+{n}", Counted: c.PlayedIds(p => p.CardId == "BOT_283"));
        }),
        new("captured-archmage", "Captured Archmage", "Your Captured Archmages that died this game. After 4, the next one casts Fireball.",
            c => Goal(c.Deaths("JAIL_974"), 4) with { Counted = c.Log.Died.Where(id => id == "JAIL_974").ToList() }),
        new("elwynn-boar", "Elwynn Boar", "Your Elwynn Boars that died this game. At 10, equip the Sword of a Thousand Truths.",
            c => Goal(c.Deaths("SW_075"), 10) with { Counted = c.Log.Died.Where(id => id == "SW_075").ToList() }),
        new("ysondre", "Ysondre", "Times your Ysondre has died this game: Dragons summoned by her next Deathrattle.",
            c => Ids(c.Log.Died.Where(id => id == "EDR_465"))),
        new("blood-runes", "Blood Runes", "Blood Runes on the cards you've played this game (G'huun).",
            c => Num(c.Log.Played.Sum(p => CounterCards.BloodRunes.GetValueOrDefault(p.CardId)), counted: c.PlayedIds(p => CounterCards.BloodRunes.ContainsKey(p.CardId)))),
        new("colossus", "Protoss spells", "Protoss spells you've cast this game (Colossus).",
            c => PlayedNum(c, p => p.CardType == "SPELL" && (CounterCards.Protoss.Contains(p.CardId) || p.Entity.Has("PROTOSS")))),
        new("protoss-discount", "Protoss minion discount", "How much less your Protoss minions cost this game.", c =>
        {
            // Photon Cannon only discounts when it kills a minion.
            var table = CounterCards.Sources["protoss-discount"];
            var plays = c.Log.Played.Where(p => (p.CardId != "SC_753" || c.Log.KillsByPlay.GetValueOrDefault(p.Entity.Id) > 0) && table.GetValueOrDefault(p.CardId).Play != 0)
                .ToList();
            var deaths = c.Log.Died.Where(id => table.GetValueOrDefault(id).Death != 0).ToList();
            return new($"-{plays.Sum(p => table[p.CardId].Play) + deaths.Sum(id => table[id].Death)}", Counted: [.. plays.Select(p => p.CardId), .. deaths]);
        }),
        new("libram", "Libram cost reduction", "How much less your Librams cost this game.", c => new($"-{c.Source("libram")}", Counted: c.SourceCards("libram"))),
        new("leyline-cost", "Leyline cost reduction", "How much less your Leylines cost this game.",
            c => new($"-{c.Source("leyline-cost")}", Counted: c.SourceCards("leyline-cost"))),
        new("leyline-effect", "Leyline effect increase", "How much the effects of your Leylines are increased this game.",
            c => new($"+{c.Source("leyline-effect")}", Counted: c.SourceCards("leyline-effect"))),
        new("leyline-trigger", "Leyline extra triggers", "Additional times your Leylines trigger this game.",
            c => new($"+{c.Source("leyline-trigger")}", Counted: c.SourceCards("leyline-trigger"))),
        new("excavate", "Excavates", "Times you've Excavated this game.", c => Num(c.Source("excavate"), counted: c.SourceCards("excavate"))),
        new("galakrond", "Galakrond invokes", "Times you've Invoked Galakrond this game.",
            c => Num(Math.Max(c.Tag("INVOKE_COUNTER"), c.Source("galakrond")), counted: c.SourceCards("galakrond"))),
        new("cthun-old", "C'Thun", "Attack and Health of your C'Thun (6/6 plus its buffs, wherever it is).", c =>
        {
            int attack = c.Tag("CTHUN_ATTACK_BUFF"), health = c.Tag("CTHUN_HEALTH_BUFF");
            return new($"{6 + attack}/{6 + health}", Details: c.Tag("CTHUN_TAUNT_BUFF") > 0 ? [Loc.T("Core.Counter.HasTaunt")] : null, Active: attack + health > 0);
        }),
        new("silver-hand", "Silver Hand Recruit buff", "Attack and Health your Silver Hand Recruits get for the rest of the game.", c =>
        {
            // Named tags as in the log; the numeric ids are what an older client would print instead.
            int attack = Math.Max(c.Tag("SILVER_HAND_RECRUIT_ATK_BUFF"), c.Tag("4805")), health = Math.Max(c.Tag("SILVER_HAND_RECRUIT_HP_BUFF"), c.Tag("4806"));
            return new($"+{attack}/+{health}", Active: attack + health > 0);
        }),
        new("golakka", "Dive the Golakka Depths", "Times you've completed Dive the Golakka Depths: the Murlocs you summon gain +1/+1 for each.", c =>
        {
            var n = c.Log.QuestsCompleted.GetValueOrDefault("TLC_426");
            return new($"+{n}/+{n}", Active: n > 0);
        }),
        new("incindius", "Eruption damage", "Damage of your Eruptions: Incindius upgrades them at the end of each of your turns.",
            c => new(Loc.F("Core.Counter.Damage", 1 + c.Log.Triggers.GetValueOrDefault("VAC_321")))),
        new("timethief-rafaam", "Timethief Rafaam", "Different Rafaams you've played. Timethief Rafaam destroys the enemy hero once you've played the other 9.", c =>
        {
            // The nine other Rafaams are TIME_005t1..t9.
            var rafaams = c.Log.Played.Where(p => p.CardId.StartsWith("TIME_005t", StringComparison.Ordinal) && p.CardId.Length == 10).ToList();
            var distinct = rafaams.Select(p => p.CardId).Distinct().Count();
            return new($"{distinct}/9", distinct / 9.0, [Loc.P("Core.Counter.RafaamsPlayed", rafaams.Count)], distinct > 0, Goal: 9,
                Parts: Enumerable.Range(1, 9).Select(i => ($"TIME_005t{i}", rafaams.Exists(p => p.CardId == $"TIME_005t{i}"))).ToList());
        }),
        new("tyrande", "Tyrande", "Spells you play next that cast twice (Tyrande).", c =>
        {
            var last = c.Log.Played.FindLastIndex(p => p.CardId == "EDR_464");
            if (last < 0)
            {
                return new(Loc.T("Core.Counter.NotPlayed"));
            }

            var spells = c.Log.Played.Skip(last + 1).Where(p => p.CardType == "SPELL").Take(3).Select(p => p.CardId).ToList();
            var left = 3 - spells.Count;
            return new(Loc.F("Core.Counter.Left", left), Active: left > 0, Counted: spells);
        }),
        new("wheel-of-death", "Wheel of Death", "Turns until Wheel of DEATH!!! destroys the enemy hero.", c => Countdown(c, "TOY_529", 5, "Core.Counter.WheelDone")),
        new("aviana", "Lunar cycle", "Turns until the Full Moon rises (Aviana, Elune's Chosen): then your cards cost (1).", c => Countdown(c, "EDR_895", 3, "Core.Counter.FullMoon")),
        new("ceaseless-expanse", "The Ceaseless Expanse", "Cost of The Ceaseless Expanse: (1) less for each card drawn, played or destroyed, both sides.", c =>
        {
            var logs = c.Game.Players.Keys.Select(c.Tracker.For).ToList();
            var times = logs.Sum(l => l.Drawn + l.Played.Count + l.Died.Count + l.Destroyed.Count);
            return new(Loc.F("Core.Counter.Cost", Math.Max(0, 100 - times)), Details: [Loc.F("Core.Counter.CeaselessTimes", times)]);
        }),
    ];

    /// <summary>Every counter, in display order, for the settings page; built on each read, in the interface language.</summary>
    public static IReadOnlyList<CounterInfo> All =>
        [.. Defs.Select(d => new CounterInfo(d.Id, d.LocalTitle, d.LocalDescription)),
            new CounterInfo(QuestsId, Loc.Or("Core.Counter.quests.Title", "Quests"), Loc.Or("Core.Counter.quests.Description", "Progress of quests and questlines, yours and your opponent's."))];

    public const string QuestsId = "quests";

    public static CounterMode ModeOf(IReadOnlyDictionary<string, string>? modes, string id) =>
        modes is not null && modes.TryGetValue(id, out var mode) && Enum.TryParse<CounterMode>(mode, true, out var parsed) ? parsed : CounterMode.Auto;

    /// <summary>Every counter for both players, yours first; the overlay applies each counter's <see cref="CounterMode"/>.</summary>
    public static IReadOnlyList<CounterView> Build(GameState game, CounterTracker tracker, CardDb cards, DeckDefinition? deck)
    {
        var result = new List<CounterView>((Defs.Length * 2) + 4);
        if (game.LocalPlayerId == 0)
        {
            return result;
        }

        // Copies of each card the player is known to have: the deck list, or the entities seen this game if more.
        var mine = Known(game, game.LocalPlayerId);
        foreach (var (dbf, count) in deck?.Cards ?? new Dictionary<int, int>())
        {
            if (cards.Get(dbf)?.Id is { } id)
            {
                mine[id] = Math.Max(mine.GetValueOrDefault(id), count);
            }
        }

        Add(result, game, tracker, cards, game.LocalPlayerId, false, mine);
        if (game.OpponentPlayerId != 0)
        {
            Add(result, game, tracker, cards, game.OpponentPlayerId, true, Known(game, game.OpponentPlayerId));
        }

        return result;
    }

    private static void Add(List<CounterView> result, GameState game, CounterTracker tracker, CardDb cards, int playerId, bool opponent,
        Dictionary<string, int> seen)
    {
        var player = game.Players.TryGetValue(playerId, out var slot) ? game.Entities.GetValueOrDefault(slot.EntityId) : null;
        var hero = opponent ? game.OpponentHero : game.LocalHero;
        var allDied = game.Players.Keys.Sum(id => tracker.For(id).Died.Count);
        var context = new CounterContext(game, playerId, player, tracker, tracker.For(playerId), cards, cards.Get(hero?.CardId)?.CardClass ?? string.Empty, allDied);
        var views = tracker.Views;
        views.Use(cards);
        // Built for both players on every overlay update (up to 10 a second): plain loops, and shared empty lists for
        // the many counters with nothing to list. LINQ here allocated about 200 KB per update.
        foreach (var def in Defs)
        {
            var related = CounterCards.Related.GetValueOrDefault(def.Id) ?? [];
            var value = def.Compute(context);
            if (value is null)
            {
                continue;
            }

            // Most counters read the same as on the last update: that view again, nothing built (these views and their
            // card lists were about half of what an update allocated).
            if (views.Same(opponent, def, value, related, seen) is { } same)
            {
                result.Add(same);
                continue;
            }

            var relatedCards = RelatedCards(cards, related, seen);
            var icon = relatedCards.Count > 0 ? relatedCards[0].CardId : null;
            var counted = Tally(cards, value.Counted);
            var view = new CounterView(def.Id, def.LocalTitle, value.Value, value.Progress, opponent,
                icon ?? (counted.Count > 0 ? counted[0].CardId : related.Length > 0 ? related[0] : null), def.LocalDescription, value.Details ?? [],
                icon is not null || value.Active)
            {
                Counted = counted,
                RelatedCards = relatedCards,
                Parts = value.Parts is { Count: > 0 } parts ? parts.Select(p => new CounterCard(p.CardId, cards.NameOf(p.CardId), 0, p.Done)).ToList() : [],
                Goal = value.Goal,
            };
            views.Keep(opponent, def.Id, value, view);
            result.Add(view);
        }

        foreach (var quest in game.Entities.Values.Where(e => e.Controller == playerId && e.GetInt("QUEST_PROGRESS_TOTAL") > 0 && e.Zone is "SECRET" or "PLAY"))
        {
            var done = quest.GetInt("QUEST_PROGRESS");
            var total = quest.GetInt("QUEST_PROGRESS_TOTAL");
            var name = quest.CardId.Length > 0 ? cards.NameOf(quest.CardId) : Loc.T("Core.Counter.Quest");
            result.Add(new CounterView(QuestsId, name, $"{Math.Min(done, total)}/{total}", Math.Min(1, (double)done / total), opponent,
                quest.CardId.Length > 0 ? quest.CardId : null, Loc.T("Core.Counter.QuestProgress"), [], true) { Goal = total });
        }
    }

    /// <summary>
    /// Cards of a player the tracker can see, with the number of entities of each: everything outside the hidden deck
    /// and hand, plus revealed ones.
    /// </summary>
    private static Dictionary<string, int> Known(GameState game, int playerId)
    {
        var known = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var e in game.Entities.Values)
        {
            if ((e.Controller == playerId || e.InitialController == playerId) && e.CardId.Length > 0)
            {
                known[e.CardId] = known.GetValueOrDefault(e.CardId) + 1;
            }
        }

        return known;
    }

    /// <summary>The counter's related cards the player is known to have, each once, in the table's order.</summary>
    private static IReadOnlyList<CounterCard> RelatedCards(CardDb cards, string[] related, Dictionary<string, int> seen)
    {
        List<CounterCard>? list = null;
        foreach (var id in related)
        {
            if (seen.TryGetValue(id, out var copies) && IndexOf(list, id) < 0)
            {
                (list ??= []).Add(new CounterCard(id, cards.NameOf(id), copies));
            }
        }

        return list ?? (IReadOnlyList<CounterCard>)[];
    }

    /// <summary>Counted card ids grouped by card, in the order each was first counted; hidden cards (no id yet) are left out.</summary>
    private static IReadOnlyList<CounterCard> Tally(CardDb cards, IReadOnlyList<string>? cardIds)
    {
        if (cardIds is null || cardIds.Count == 0)
        {
            return [];
        }

        // A handful of cards at most: a linear search is cheaper than a dictionary.
        List<CounterCard>? tally = null;
        foreach (var id in cardIds)
        {
            if (id.Length == 0)
            {
                continue;
            }

            var at = IndexOf(tally, id);
            if (at >= 0)
            {
                tally![at] = tally[at] with { Count = tally[at].Count + 1 };
            }
            else
            {
                (tally ??= []).Add(new CounterCard(id, cards.NameOf(id), 1));
            }
        }

        return tally ?? (IReadOnlyList<CounterCard>)[];
    }

    private static int IndexOf(List<CounterCard>? list, string cardId)
    {
        for (var i = 0; i < (list?.Count ?? 0); i++)
        {
            if (list![i].CardId == cardId)
            {
                return i;
            }
        }

        return -1;
    }

    /// <param name="counted">Copied: a tracker's list goes on growing after this update.</param>
    private static CounterValue Num(int value, IReadOnlyList<string>? details = null, IEnumerable<string>? counted = null) =>
        new(Number(value), Details: details, Active: false, Counted: counted?.ToList());

    /// <summary>A count of cards that are also what the tooltip lists.</summary>
    private static CounterValue Ids(IEnumerable<string> cardIds) => Ids(cardIds.ToList());

    /// <summary>A count of cards that are also what the tooltip lists, from a list made for this update (kept, not copied).</summary>
    private static CounterValue Ids(List<string> cardIds) => new(Number(cardIds.Count), Counted: cardIds);

    private static readonly string[] SmallNumbers = Enumerable.Range(0, 100).Select(n => n.ToString(CultureInfo.InvariantCulture)).ToArray();

    /// <summary>A count as text; the small ones every update shows are made once.</summary>
    private static string Number(int value) => (uint)value < SmallNumbers.Length ? SmallNumbers[value] : value.ToString(CultureInfo.InvariantCulture);

    /// <summary>The cards the player played that match, counted and listed.</summary>
    private static CounterValue PlayedNum(CounterContext c, Func<PlayedCardInfo, bool> match) => Ids(c.PlayedIds(match));

    private static CounterValue School(CounterContext c, int school) => PlayedNum(c, p => p.CardType == "SPELL" && p.SpellSchool == school);

    private static IReadOnlyList<string> RacesOf(CounterContext c, string cardId) =>
        c.Cards.Get(cardId)?.Races.Where(r => r is not ("ALL" or "INVALID")).ToList() ?? [];

    /// <summary>"Improve your future X": the next one makes a card that costs one more for each played.</summary>
    /// <param name="key">Key of the chip text ("{0}-Cost Spell").</param>
    private static CounterValue Next(CounterContext c, string cardId, string key)
    {
        var played = c.PlayedIds(p => p.CardId == cardId);
        return new(Loc.F(key, played.Count + 1), Details: [Loc.F("Core.Counter.Played", played.Count)], Counted: played);
    }

    private static CounterValue Goal(int count, int goal) =>
        new($"{Math.Min(count, goal)}/{goal}", Math.Min(1, (double)count / goal), count > goal ? [Loc.F("Core.Counter.InTotal", count)] : null, Goal: goal);

    /// <summary>"In N turns": counted in the player's own turns from the last time the card was played.</summary>
    /// <param name="doneKey">Key of the chip text once the countdown is over.</param>
    private static CounterValue Countdown(CounterContext c, string cardId, int turns, string doneKey)
    {
        if (c.Log.Played.LastOrDefault(p => p.CardId == cardId) is not { } played)
        {
            return new(Loc.T("Core.Counter.NotPlayed"));
        }

        var left = turns - (c.OwnTurn - played.Turn);
        return left > 0
            ? new(Loc.P("Core.Counter.Turns", left), (double)(turns - left) / turns, Active: true, Goal: turns, Counted: [cardId])
            : new(Loc.T(doneKey), 1, Goal: turns, Counted: [cardId]);
    }

    /// <summary>The character a card was played on, if any.</summary>
    private static Entity? TargetOf(CounterContext c, PlayedCardInfo played) =>
        played.Target > 0 && c.Game.Entities.GetValueOrDefault(played.Target) is { CardType: "MINION" or "HERO" } target ? target : null;

    private static IReadOnlyList<string> Last(CounterContext c, IReadOnlyList<string> cardIds) =>
        cardIds.TakeLast(6).Select(id => c.Cards.NameOf(id)).ToList();

    private static string Title(string upper) => upper.Length == 0 ? upper : upper[0] + upper[1..].ToLowerInvariant();

    /// <param name="Title">English title; <see cref="LocalTitle"/> is what the player reads.</param>
    /// <param name="Description">English description; <see cref="LocalDescription"/> is what the player reads.</param>
    internal sealed record CounterDef(string Id, string Title, string Description, Func<CounterContext, CounterValue?> Compute)
    {
        // Keys made once: Build reads them for every counter on every overlay update.
        private readonly string _titleKey = $"Core.Counter.{Id}.Title";
        private readonly string _descriptionKey = $"Core.Counter.{Id}.Description";

        /// <summary>The title in the interface language, read each time so a language switch shows at once.</summary>
        public string LocalTitle => Loc.Or(_titleKey, Title);

        /// <summary>The description in the interface language.</summary>
        public string LocalDescription => Loc.Or(_descriptionKey, Description);
    }

    /// <param name="Counted">Card ids of what was counted, one per count (the tooltip groups them).</param>
    /// <param name="Parts">The parts of the goal and whether each is done.</param>
    internal sealed record CounterValue(string Value, double? Progress = null, IReadOnlyList<string>? Details = null, bool Active = false,
        IReadOnlyList<string>? Counted = null, IReadOnlyList<(string CardId, bool Done)>? Parts = null, int? Goal = null);

    internal sealed record CounterContext(GameState Game, int PlayerId, Entity? Player, CounterTracker Tracker, CounterTracker.PlayerLog Log, CardDb Cards,
        string PlayerClass, int AllDied)
    {
        public int Tag(string tag) => Player?.GetInt(tag) ?? 0;

        public int Played(Func<PlayedCardInfo, bool> match) => Log.Played.Count(match);

        public int Plays(string cardId) => Log.Played.Count(p => p.CardId == cardId);

        public List<string> PlayedIds(Func<PlayedCardInfo, bool> match)
        {
            var ids = new List<string>();
            foreach (var p in Log.Played)
            {
                if (match(p))
                {
                    ids.Add(p.CardId);
                }
            }

            return ids;
        }

        public int Deaths(string cardId) => Log.Died.Count(id => id == cardId);

        /// <summary>Sum of what the player's cards add to a counter when played and when they die (<see cref="CounterCards.Sources"/>).</summary>
        public int Source(string key)
        {
            var table = CounterCards.Sources[key];
            var sum = 0;
            foreach (var p in Log.Played) sum += table.GetValueOrDefault(p.CardId).Play;
            foreach (var id in Log.Died) sum += table.GetValueOrDefault(id).Death;
            foreach (var id in Log.Destroyed) sum += table.GetValueOrDefault(id).Death;
            return sum;
        }

        /// <summary>The played, died and destroyed cards that added to <see cref="Source"/>.</summary>
        public List<string> SourceCards(string key)
        {
            var table = CounterCards.Sources[key];
            return [.. Log.Played.Where(p => table.GetValueOrDefault(p.CardId).Play != 0).Select(p => p.CardId),
                .. Log.Died.Concat(Log.Destroyed).Where(id => table.GetValueOrDefault(id).Death != 0)];
        }

        /// <summary>Turns this player has started, the current one included.</summary>
        public int OwnTurn => Game.Turn <= 0 ? 0 : Tag("FIRST_PLAYER") == 1 ? (Game.Turn + 1) / 2 : Game.Turn / 2;
    }
    /// <summary>
    /// The counters of the last update, by side and id, with the value each was built from. A counter whose value,
    /// title, description and related cards read the same is handed out again: the view is immutable, and every list in
    /// a <see cref="CounterValue"/> is made for its update (a tracker's list is copied), so equal content means an equal view.
    /// </summary>
    internal sealed class CounterViews
    {
        private readonly Dictionary<(bool Opponent, string Id), (CounterValue Value, CounterView View)> _last = [];
        private CardDb? _cards;

        /// <summary>Off: every counter is built (tests compare both ways).</summary>
        public bool Reuse { get; set; } = true;

        /// <summary>Card names come from <paramref name="cards"/>: another card set (a language switch) builds everything again.</summary>
        public void Use(CardDb cards)
        {
            if (!ReferenceEquals(cards, _cards))
            {
                _last.Clear();
                _cards = cards;
            }
        }

        public CounterView? Same(bool opponent, CounterDef def, CounterValue value, string[] related, Dictionary<string, int> seen) =>
            Reuse && _last.TryGetValue((opponent, def.Id), out var last)
                  && last.View.Title == def.LocalTitle
                  && last.View.Description == def.LocalDescription
                  && SameValue(last.Value, value)
                  && SameRelated(last.View.RelatedCards, related, seen)
                ? last.View
                : null;

        public void Keep(bool opponent, string id, CounterValue value, CounterView view)
        {
            if (Reuse)
            {
                _last[(opponent, id)] = (value, view);
            }
        }

        private static bool SameValue(CounterValue a, CounterValue b) =>
            a.Value == b.Value && a.Progress == b.Progress && a.Active == b.Active && a.Goal == b.Goal
            && SameList(a.Details, b.Details) && SameList(a.Counted, b.Counted) && SameParts(a.Parts, b.Parts);

        // Null and empty build the same view.
        private static bool SameList(IReadOnlyList<string>? a, IReadOnlyList<string>? b)
        {
            if (ReferenceEquals(a, b))
            {
                return true;
            }

            if ((a?.Count ?? 0) != (b?.Count ?? 0))
            {
                return false;
            }

            for (var i = 0; i < (a?.Count ?? 0); i++)
            {
                if (!string.Equals(a![i], b![i], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool SameParts(IReadOnlyList<(string CardId, bool Done)>? a, IReadOnlyList<(string CardId, bool Done)>? b)
        {
            if ((a?.Count ?? 0) != (b?.Count ?? 0))
            {
                return false;
            }

            for (var i = 0; i < (a?.Count ?? 0); i++)
            {
                if (a![i].Done != b![i].Done || !string.Equals(a[i].CardId, b[i].CardId, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Whether <see cref="RelatedCards"/> would list what <paramref name="built"/> lists, without building it.</summary>
        private static bool SameRelated(IReadOnlyList<CounterCard> built, string[] related, Dictionary<string, int> seen)
        {
            var at = 0;
            for (var i = 0; i < related.Length; i++)
            {
                var id = related[i];
                if (!seen.TryGetValue(id, out var copies) || Array.IndexOf(related, id, 0, i) >= 0)
                {
                    continue;
                }

                if (at >= built.Count || built[at].CardId != id || built[at].Count != copies)
                {
                    return false;
                }

                at++;
            }

            return at == built.Count;
        }
    }
}
