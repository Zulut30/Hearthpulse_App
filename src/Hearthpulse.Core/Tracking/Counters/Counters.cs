using System.Globalization;
using Hearthpulse.Core.Cards;
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
    IReadOnlyList<string> Details, bool Relevant);

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
            var used = Enumerable.Range(1, 4).Where(i => c.Tag($"CTHUN_ABILITY{i}_USED") > 0).ToList();
            return new($"{used.Count}/4", used.Count / 4.0,
                Enumerable.Range(1, 4).Select(i => $"Ability {i}: {(used.Contains(i) ? "triggered" : "not yet")}").ToList(), used.Count > 0);
        }),
        new("herald", "Herald", "Heralds this game. Every two Heralds upgrade your class Colossal.", c =>
        {
            var n = c.Tag("HERALD_COLOSSAL_AMOUNT");
            var next = 2 - n % 2;
            return new(n.ToString(CultureInfo.InvariantCulture), n % 2 / 2.0, [$"Next Colossal upgrade after {next} more Herald{(next == 1 ? "" : "s")}."], n > 0);
        }),
        new("corpses", "Corpses", "Corpses you have to spend.", c => Num(c.Tag("CORPSES"))),
        new("jade", "Jade Golem", "Size of your next Jade Golem.", c =>
        {
            var next = c.Tag("JADE_GOLEM") + 1;
            return new($"{next}/{next}");
        }),
        new("spells-cast", "Spells cast", "Spells you've cast this game.", c => Num(Math.Max(c.Tag("NUM_SPELLS_PLAYED_THIS_GAME"), c.Played(p => p.CardType == "SPELL")))),
        new("spell-schools", "Spell schools", "Different spell schools you've cast this game.", c =>
        {
            var schools = c.Log.Played.Where(p => p.CardType == "SPELL" && p.SpellSchool is > 0 and < 8).Select(p => p.SpellSchool).Distinct().Order().ToList();
            return new(schools.Count.ToString(CultureInfo.InvariantCulture), Details: schools.Select(s => SchoolNames[s]).ToList());
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
            var types = c.Log.Played.Where(p => p.CardType == "MINION")
                .SelectMany(p => c.Cards.Get(p.CardId)?.Races ?? []).Where(r => r is not ("ALL" or "INVALID")).Distinct().Order().ToList();
            return new(types.Count.ToString(CultureInfo.InvariantCulture), Details: types.Select(Title).ToList());
        }),
        new("elemental-streak", "Elemental streak", "Turns in a row you've played an Elemental (this turn or last turn keeps it going).", c =>
        {
            var turns = c.Log.Played.Where(p => c.Cards.Get(p.CardId)?.HasRace("ELEMENTAL") == true).Select(p => p.Turn).ToHashSet();
            var turn = CounterTracker.PlayerTurn(c.Game);
            var start = turns.Contains(turn) ? turn : turn - 1;
            var streak = 0;
            while (turns.Contains(start - streak))
            {
                streak++;
            }

            return new(streak.ToString(CultureInfo.InvariantCulture), Details: [turns.Contains(turn) ? "Elemental played this turn." : "No Elemental yet this turn."]);
        }),
        new("dragons", "Dragons played", "Dragons you've played this game.", c => Num(c.Played(p => c.Cards.Get(p.CardId)?.HasRace("DRAGON") == true))),
        new("beasts-summoned", "Beasts summoned", "Beasts you've summoned this game.", c => Num(c.Log.Summoned.Count(id => c.Cards.Get(id)?.HasRace("BEAST") == true))),
        new("totems", "Totems summoned", "Totems you've summoned this game.", c => Num(c.Log.Summoned.Count(id => c.Cards.Get(id)?.HasRace("TOTEM") == true))),
        new("deathrattle-summoned", "Deathrattle minions", "Deathrattle minions you've summoned this game (N'Zoth, Dream's End).",
            c => Num(c.Log.Summoned.Count(id => c.Cards.Get(id)?.Mechanics.Contains("DEATHRATTLE") == true))),
        new("minion-mana", "Mana on minions", "Mana you've spent on minions this game (Y'Shaarj needs 30).", c =>
        {
            var spent = c.Log.Played.Where(p => p.CardType == "MINION").Sum(p => p.Cost);
            return new($"{Math.Min(spent, 30)}/30", Math.Min(1, spent / 30.0), spent > 30 ? [$"{spent} Mana in total."] : null);
        }),
        new("friendly-deaths", "Friendly minions died", "Your minions that died this game.",
            c => Num(Math.Max(c.Tag("NUM_FRIENDLY_MINIONS_THAT_DIED_THIS_GAME"), c.Log.Died.Count), Last(c, c.Log.Died))),
        new("minions-died", "Minions died", "Minions that died this game, both sides.", c => Num(c.AllDied)),
        new("not-from-deck", "Cards not from deck", "Cards you've played that didn't start in your deck.", c => Num(c.Played(p => !p.FromDeck))),
        new("other-class", "Other-class cards", "Cards from another class you've played this game.", c => Num(c.Played(p =>
            c.Cards.Get(p.CardId)?.CardClass is { Length: > 0 } cls && cls != "NEUTRAL" && !string.Equals(cls, c.PlayerClass, StringComparison.OrdinalIgnoreCase)))),
        new("secrets-played", "Secrets played", "Secrets you've played this game.", c => Num(c.Played(p => c.Cards.Get(p.CardId)?.Mechanics.Contains("SECRET") == true))),
        new("cards-played", "Cards played", "Cards you've played this game.", c => Num(c.Log.Played.Count)),
        new("cards-drawn", "Cards drawn", "Cards you've drawn this game.", c => Num(c.Log.Drawn)),
        new("cards-discarded", "Cards discarded", "Cards you've discarded this game.", c => Num(c.Log.Discarded.Count, Last(c, c.Log.Discarded))),
        new("overload", "Mana Overloaded", "Mana Crystals you've Overloaded this game.", c => Num(c.Log.Played.Sum(p => p.Overload))),
        new("imbue", "Imbues", "Times you've Imbued your Hero Power this game.", c =>
            Num(c.Log.Played.Sum(p => CounterCards.ImbueSources.GetValueOrDefault(p.CardId)))),
        new("hero-power", "Hero Power uses", "Times you've used your Hero Power this game.", c => Num(c.Tag("NUM_TIMES_HERO_POWER_USED_THIS_GAME"))),
        new("healed", "Healing done", "Health you've restored this game.", c => Num(c.Tag("AMOUNT_HEALED_THIS_GAME"))),
        new("armor", "Armor gained", "Armor you've gained this game.", c => Num(c.Tag("ARMOR_GAINED_THIS_GAME"))),
        new("friendly-attacks", "Attacks", "Times your characters attacked this game.", c => Num(c.Log.Attacks)),
        new("fatigue", "Fatigue", "Damage of your next fatigue draw. Shown when your deck is almost empty or a card deals Fatigue.", c =>
        {
            var next = c.Tag("FATIGUE") + 1;
            var deck = c.Game.Controlled(c.PlayerId, "DECK").Count();
            return new($"Next: {next}", Details: [$"{deck} card{(deck == 1 ? "" : "s")} left in deck."], Active: deck <= 3);
        }),
    ];

    /// <summary>Every counter, in display order, for the settings page.</summary>
    public static IReadOnlyList<CounterInfo> All { get; } =
        [.. Defs.Select(d => new CounterInfo(d.Id, d.Title, d.Description)), new CounterInfo(QuestsId, "Quests", "Progress of quests and questlines, yours and your opponent's.")];

    public const string QuestsId = "quests";

    public static CounterMode ModeOf(IReadOnlyDictionary<string, string>? modes, string id) =>
        modes is not null && modes.TryGetValue(id, out var mode) && Enum.TryParse<CounterMode>(mode, true, out var parsed) ? parsed : CounterMode.Auto;

    /// <summary>Every counter for both players, yours first; the overlay applies each counter's <see cref="CounterMode"/>.</summary>
    public static IReadOnlyList<CounterView> Build(GameState game, CounterTracker tracker, CardDb cards, DeckDefinition? deck)
    {
        var result = new List<CounterView>();
        if (game.LocalPlayerId == 0)
        {
            return result;
        }

        var deckIds = deck?.Cards.Keys.Select(dbf => cards.Get(dbf)?.Id).OfType<string>() ?? [];
        Add(result, game, tracker, cards, game.LocalPlayerId, false, [.. deckIds, .. Known(game, game.LocalPlayerId)]);
        if (game.OpponentPlayerId != 0)
        {
            Add(result, game, tracker, cards, game.OpponentPlayerId, true, [.. Known(game, game.OpponentPlayerId)]);
        }

        return result;
    }

    private static void Add(List<CounterView> result, GameState game, CounterTracker tracker, CardDb cards, int playerId, bool opponent,
        HashSet<string> seen)
    {
        var player = game.Players.TryGetValue(playerId, out var slot) ? game.Entities.GetValueOrDefault(slot.EntityId) : null;
        var hero = opponent ? game.OpponentHero : game.LocalHero;
        var allDied = game.Players.Keys.Sum(id => tracker.For(id).Died.Count);
        var context = new CounterContext(game, playerId, player, tracker.For(playerId), cards, cards.Get(hero?.CardId)?.CardClass ?? string.Empty, allDied);
        foreach (var def in Defs)
        {
            var related = CounterCards.Related.GetValueOrDefault(def.Id) ?? [];
            var icon = related.FirstOrDefault(seen.Contains);
            var value = def.Compute(context);
            if (value is not null)
            {
                result.Add(new CounterView(def.Id, def.Title, value.Value, value.Progress, opponent, icon, def.Description, value.Details ?? [], icon is not null || value.Active));
            }
        }

        foreach (var quest in game.Entities.Values.Where(e => e.Controller == playerId && e.GetInt("QUEST_PROGRESS_TOTAL") > 0 && e.Zone is "SECRET" or "PLAY"))
        {
            var done = quest.GetInt("QUEST_PROGRESS");
            var total = quest.GetInt("QUEST_PROGRESS_TOTAL");
            var name = quest.CardId.Length > 0 ? cards.NameOf(quest.CardId) : "Quest";
            result.Add(new CounterView(QuestsId, name, $"{Math.Min(done, total)}/{total}", Math.Min(1, (double)done / total), opponent,
                quest.CardId.Length > 0 ? quest.CardId : null, "Quest progress.", [], true));
        }
    }

    /// <summary>Cards of a player the tracker can see: everything outside the hidden deck and hand, plus revealed ones.</summary>
    private static IEnumerable<string> Known(GameState game, int playerId) =>
        game.Entities.Values.Where(e => (e.Controller == playerId || e.InitialController == playerId) && e.CardId.Length > 0).Select(e => e.CardId);

    private static CounterValue Num(int value, IReadOnlyList<string>? details = null) =>
        new(value.ToString(CultureInfo.InvariantCulture), Details: details, Active: false);

    private static CounterValue School(CounterContext c, int school) => Num(c.Played(p => p.CardType == "SPELL" && p.SpellSchool == school));

    private static IReadOnlyList<string> Last(CounterContext c, IReadOnlyList<string> cardIds) =>
        cardIds.TakeLast(6).Select(id => c.Cards.NameOf(id)).ToList();

    private static string Title(string upper) => upper.Length == 0 ? upper : upper[0] + upper[1..].ToLowerInvariant();

    private sealed record CounterDef(string Id, string Title, string Description, Func<CounterContext, CounterValue?> Compute);

    private sealed record CounterValue(string Value, double? Progress = null, IReadOnlyList<string>? Details = null, bool Active = false);

    private sealed record CounterContext(GameState Game, int PlayerId, Entity? Player, CounterTracker.PlayerLog Log, CardDb Cards, string PlayerClass, int AllDied)
    {
        public int Tag(string tag) => Player?.GetInt(tag) ?? 0;

        public int Played(Func<PlayedCardInfo, bool> match) => Log.Played.Count(match);
    }
}
