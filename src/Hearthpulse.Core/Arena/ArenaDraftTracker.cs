using System.Globalization;
using System.Text.RegularExpressions;
using Hearthpulse.Core.Cards;

namespace Hearthpulse.Core.Arena;

/// <summary>What Arena.log has told about the current draft.</summary>
/// <param name="HeroCardId">The hero picked for the run, or null before the hero pick.</param>
/// <param name="DeckId">The Arena deck id once the client named it.</param>
/// <param name="Cards">Card ids in the drafted deck, in pick order (a card picked twice is listed twice).</param>
/// <param name="Mode">The client's draft mode (DRAFTING, ACTIVE_DRAFT_DECK, IN_REWARDS, REDRAFTING, NO_ACTIVE_DRAFT...), or null.</param>
/// <param name="HeroPowerCardId">The hero power picked after the hero, or null.</param>
public sealed record ArenaDraftState(string? HeroCardId, long DeckId, IReadOnlyList<string> Cards, string? Mode, string? HeroPowerCardId = null)
{
    public static ArenaDraftState Empty { get; } = new(null, 0, [], null);
}

/// <summary>
/// Follows an Arena draft through Arena.log: the hero and hero power picks, every card the player takes and the deck
/// the client lists when the Arena screen opens. The log does not name the offered cards; they come from the game's
/// memory (<see cref="Memory.HearthstoneMemory.GetArenaDraftChoices"/>). The lines are the client's DraftManager lines
/// of Arena.log (docs/arena-draft.md), not yet checked against a log written on a live draft.
/// </summary>
public sealed partial class ArenaDraftTracker
{
    private string? _hero;
    private string? _heroPower;
    private long _deckId;

    /// <summary>The cards picked in this run as the log showed them (a legendary's package included).</summary>
    private readonly List<string> _picked = [];

    /// <summary>
    /// The client's latest list of the deck (<c>Draft deck contains card</c>). It is not always whole: the real log of
    /// 2026-10-28 lists 27 of 30 cards, missing copies. So it only adds to the picks, card by card (the larger count).
    /// </summary>
    private readonly List<string> _listed = [];

    private string? _lastPick;

    /// <summary>The latest pick came with a package (<see cref="AddPackage"/>).</summary>
    private bool _lastPickPackage;
    private string? _mode;

    public ArenaDraftState State { get; private set; } = ArenaDraftState.Empty;

    /// <summary>
    /// Counts choices made in this session (a hero pick can count twice: the client logs it in two lines), so a stale
    /// offer can be told from a new one.
    /// </summary>
    public int PickCount { get; private set; }

    /// <summary>The card, hero or hero power of the latest choice, or null.</summary>
    public string? LastChosen { get; private set; }

    public void Reset()
    {
        NewRun(null);
        _mode = null;
        PickCount = 0;
        LastChosen = null;
        State = ArenaDraftState.Empty;
    }

    /// <summary>
    /// Reads one Arena.log line; returns true when the state changed. <paramref name="cards"/> tells heroes and hero
    /// powers from drafted cards (without it, or for a card it does not know, the HERO_ id pattern is used: HERO_08,
    /// HERO_01aa are heroes, HERO_08bp, HERO_01aphp hero powers).
    /// </summary>
    public bool Feed(string line, CardDb? cards = null)
    {
        if (!line.Contains("Draft", StringComparison.Ordinal) && !line.Contains("chooses", StringComparison.Ordinal))
        {
            return false;
        }

        if (HeroPowerChosenRegex().Match(line) is { Success: true } power)
        {
            _heroPower = power.Groups["id"].Value;
            Chose(_heroPower);
            return Publish();
        }

        if (BeginRegex().Match(line) is { Success: true } begin)
        {
            // A new draft deck: a new run, whose hero comes next. The same deck again (back on the screen) is the same run.
            _ = long.TryParse(begin.Groups["deck"].Value, CultureInfo.InvariantCulture, out var begun);
            if (begun != 0 && begun == _deckId)
            {
                return false;
            }

            NewRun(null);
            _deckId = begun;
            return Publish();
        }

        if (HeroChosenRegex().Match(line) is { Success: true } hero)
        {
            // A hero pick starts a new run: the deck so far belongs to the previous one (the id OnBegin gave stays).
            NewRun(hero.Groups["id"].Value, KeptDeckId());
            Chose(_hero);
            return Publish();
        }

        if (DeckIdRegex().Match(line) is { Success: true } deck)
        {
            // The client lists the deck after this line: the list starts over. Another deck than the run's is another run.
            _ = long.TryParse(deck.Groups["deck"].Value, CultureInfo.InvariantCulture, out var deckId);
            if (_deckId != 0 && deckId != _deckId)
            {
                NewRun(null);
            }

            _deckId = deckId;
            _hero = deck.Groups["hero"].Value;
            _listed.Clear();
            return Publish();
        }

        if (DeckContainsRegex().Match(line) is { Success: true } contains)
        {
            _listed.Add(contains.Groups["id"].Value);
            return Publish();
        }

        if (ClientChoosesRegex().Match(line) is { Success: true } chosen)
        {
            var id = chosen.Groups["id"].Value;
            switch (KindOf(id, cards))
            {
                case ArenaChoiceKind.Hero:
                    // The same hero right after OnChosen is that pick logged again; another hero, or one after cards
                    // were taken, is a new run even when its OnChosen line was missed.
                    if (_hero != id || _picked.Count + _listed.Count > 0)
                    {
                        NewRun(id, KeptDeckId());
                    }

                    break;
                case ArenaChoiceKind.HeroPower:
                    _heroPower = id;
                    break;
                default:
                    // A legendary taken with its package is logged twice in a row (the real log of 2026-10-28): one pick.
                    // Told by its rarity, by the package that came with the first line, or, without the card data, by
                    // the repeat alone (a common taken twice in a row then counts once: one card short beats a package
                    // counted twice).
                    if (id == _lastPick && (_lastPickPackage || cards?.Get(id) is not { } known || known.Rarity == "LEGENDARY"))
                    {
                        return false;
                    }

                    _picked.Add(id);
                    _lastPick = id;
                    _lastPickPackage = false;
                    break;
            }

            Chose(id);
            return Publish();
        }

        if (DraftModeRegex().Match(line) is { Success: true } mode)
        {
            _mode = mode.Groups["mode"].Value;
            if (_mode == "NO_ACTIVE_DRAFT")
            {
                NewRun(null);
            }

            return Publish();
        }

        return false;
    }

    /// <summary>
    /// Adds the cards that came with the latest pick (a legendary's package): the client logs only the card the player
    /// clicked; the rest shows up in the deck list the next time the Arena screen opens.
    /// </summary>
    public bool AddPackage(IReadOnlyList<string> cardIds)
    {
        if (cardIds.Count == 0)
        {
            return false;
        }

        _picked.AddRange(cardIds);
        _lastPickPackage = true;
        return Publish();
    }

    /// <summary>Hero portraits and their skins are HERO_xx cards; a drafted card never is.</summary>
    public static bool IsHeroId(string? cardId) =>
        cardId is { Length: > 0 } && (cardId.StartsWith("HERO_", StringComparison.Ordinal) || cardId.Contains("_HERO_", StringComparison.Ordinal));

    /// <summary>
    /// Whether a choice is a hero, a hero power or a card: by the card data when it knows the id, else by the id
    /// (HERO_08 is a hero, HERO_08bp its hero power).
    /// </summary>
    public static ArenaChoiceKind KindOf(string? cardId, CardDb? cards)
    {
        var card = cardId is null ? null : cards?.Get(cardId);
        switch (card?.Type)
        {
            case "HERO" when card.Cost == 0:
                // A portrait costs nothing (the card data lists the base heroes as collectible too); a hero card with
                // a cost (a Death Knight hero...) is drafted like any other card.
                return ArenaChoiceKind.Hero;
            case "HERO_POWER":
                return ArenaChoiceKind.HeroPower;
            case { Length: > 0 }:
                return ArenaChoiceKind.Card;
        }

        if (!IsHeroId(cardId))
        {
            return ArenaChoiceKind.Card;
        }

        return HeroPowerIdRegex().IsMatch(cardId!) ? ArenaChoiceKind.HeroPower : ArenaChoiceKind.Hero;
    }

    /// <summary>The run's deck id while no card was taken yet: OnBegin names the deck before the hero is picked.</summary>
    private long KeptDeckId() => _picked.Count + _listed.Count == 0 ? _deckId : 0;

    private void NewRun(string? hero, long deckId = 0)
    {
        _hero = hero;
        _heroPower = null;
        _deckId = deckId;
        _picked.Clear();
        _listed.Clear();
        _lastPick = null;
        _lastPickPackage = false;
    }

    private void Chose(string? id)
    {
        PickCount++;
        LastChosen = id;
    }

    private bool Publish()
    {
        State = new ArenaDraftState(_hero, _deckId, Deck(), _mode, _heroPower);
        return true;
    }

    /// <summary>The client's list in its order, then the picks it misses: each card as many times as either has it.</summary>
    private List<string> Deck()
    {
        var deck = _listed.ToList();
        var listed = _listed.GroupBy(id => id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var id in _picked)
        {
            seen[id] = seen.GetValueOrDefault(id) + 1;
            if (seen[id] > listed.GetValueOrDefault(id))
            {
                deck.Add(id);
            }
        }

        return deck;
    }

    [GeneratedRegex(@"DraftManager\.OnBegin - Got new draft deck with ID: (?<deck>\d+)")]
    private static partial Regex BeginRegex();

    [GeneratedRegex(@"DraftManager\.OnChosen\(\): hero=(?<id>\w+)")]
    private static partial Regex HeroChosenRegex();

    [GeneratedRegex(@"DraftManager\.OnChosen\(\): heroPower=(?<id>\w+)")]
    private static partial Regex HeroPowerChosenRegex();

    [GeneratedRegex(@"Draft Deck ID: (?<deck>\d+), Hero Card = (?<hero>\w+)")]
    private static partial Regex DeckIdRegex();

    [GeneratedRegex(@"Draft deck contains card (?<id>\w+)")]
    private static partial Regex DeckContainsRegex();

    [GeneratedRegex(@"Client chooses: .* \((?<id>\w+)\)\s*$")]
    private static partial Regex ClientChoosesRegex();

    [GeneratedRegex(@"SetDraftMode - (?<mode>\w+)")]
    private static partial Regex DraftModeRegex();

    [GeneratedRegex(@"(bp|hp)\d*[a-z]?$")]
    private static partial Regex HeroPowerIdRegex();
}

/// <summary>What an Arena draft choice is.</summary>
public enum ArenaChoiceKind
{
    Card,
    Hero,
    HeroPower,
}
