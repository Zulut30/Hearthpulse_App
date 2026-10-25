using System.Text.RegularExpressions;

namespace Hearthpulse.Core.Arena;

/// <summary>What Arena.log has told about the current draft.</summary>
/// <param name="HeroCardId">The hero picked for the run, or null before the hero pick.</param>
/// <param name="DeckId">The Arena deck id once the client named it.</param>
/// <param name="Cards">Card ids in the drafted deck, in pick order (a card picked twice is listed twice).</param>
/// <param name="Mode">The client's draft mode (DRAFTING, ACTIVE_DRAFT_DECK, IN_REWARDS, NO_ACTIVE_DRAFT...), or null.</param>
public sealed record ArenaDraftState(string? HeroCardId, long DeckId, IReadOnlyList<string> Cards, string? Mode)
{
    public static ArenaDraftState Empty { get; } = new(null, 0, [], null);
}

/// <summary>
/// Follows an Arena draft through Arena.log: the hero pick, every card the player takes and the deck the client lists
/// when the Arena screen opens. The log does not name the offered cards; they come from the game's memory
/// (<see cref="Memory.HearthstoneMemory.GetArenaDraftChoices"/>). The line formats are the client's long-standing
/// DraftManager lines, not yet checked against a log of the current client (see docs/arena-draft.md).
/// </summary>
public sealed partial class ArenaDraftTracker
{
    private string? _hero;
    private long _deckId;
    private readonly List<string> _cards = [];
    private string? _mode;

    public ArenaDraftState State { get; private set; } = ArenaDraftState.Empty;

    /// <summary>Counts picks made in this session, so a stale offer can be told from a new one.</summary>
    public int PickCount { get; private set; }

    public void Reset()
    {
        _hero = null;
        _deckId = 0;
        _cards.Clear();
        _mode = null;
        PickCount = 0;
        State = ArenaDraftState.Empty;
    }

    /// <summary>Reads one Arena.log line; returns true when the state changed.</summary>
    public bool Feed(string line)
    {
        if (!line.Contains("Draft", StringComparison.Ordinal) && !line.Contains("chooses", StringComparison.Ordinal))
        {
            return false;
        }

        if (HeroChosenRegex().Match(line) is { Success: true } hero)
        {
            // A hero pick starts a new run: the deck so far belongs to the previous one.
            _hero = hero.Groups["id"].Value;
            _deckId = 0;
            _cards.Clear();
            PickCount++;
            return Publish();
        }

        if (DeckIdRegex().Match(line) is { Success: true } deck)
        {
            // The client lists the whole deck after this line, so the list starts over.
            long.TryParse(deck.Groups["deck"].Value, out _deckId);
            _hero = deck.Groups["hero"].Value;
            _cards.Clear();
            return Publish();
        }

        if (DeckContainsRegex().Match(line) is { Success: true } contains)
        {
            _cards.Add(contains.Groups["id"].Value);
            return Publish();
        }

        if (ClientChoosesRegex().Match(line) is { Success: true } chosen)
        {
            var id = chosen.Groups["id"].Value;
            PickCount++;
            if (IsHeroId(id))
            {
                _hero ??= id;
            }
            else
            {
                _cards.Add(id);
            }

            return Publish();
        }

        if (DraftModeRegex().Match(line) is { Success: true } mode)
        {
            _mode = mode.Groups["mode"].Value;
            if (_mode == "NO_ACTIVE_DRAFT")
            {
                _hero = null;
                _deckId = 0;
                _cards.Clear();
            }

            return Publish();
        }

        return false;
    }

    /// <summary>Hero portraits and their skins are HERO_xx cards; a drafted card never is.</summary>
    public static bool IsHeroId(string? cardId) =>
        cardId is { Length: > 0 } && (cardId.StartsWith("HERO_", StringComparison.Ordinal) || cardId.Contains("_HERO_", StringComparison.Ordinal));

    private bool Publish()
    {
        State = new ArenaDraftState(_hero, _deckId, _cards.ToList(), _mode);
        return true;
    }

    [GeneratedRegex(@"DraftManager\.OnChosen\(\): hero=(?<id>\w+)")]
    private static partial Regex HeroChosenRegex();

    [GeneratedRegex(@"Draft Deck ID: (?<deck>\d+), Hero Card = (?<hero>\w+)")]
    private static partial Regex DeckIdRegex();

    [GeneratedRegex(@"Draft deck contains card (?<id>\w+)")]
    private static partial Regex DeckContainsRegex();

    [GeneratedRegex(@"Client chooses: .* \((?<id>\w+)\)\s*$")]
    private static partial Regex ClientChoosesRegex();

    [GeneratedRegex(@"SetDraftMode - (?<mode>\w+)")]
    private static partial Regex DraftModeRegex();
}
