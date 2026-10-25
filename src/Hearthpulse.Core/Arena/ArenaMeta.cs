using Hearthpulse.Core.Api;

namespace Hearthpulse.Core.Arena;

/// <summary>ArenaSmith score and results of one card across all Arena classes.</summary>
/// <param name="Score">ArenaSmith score (about 0-60, higher is a better pick); null when the server has too few games for it.</param>
/// <param name="WinRate">Deck win rate with the card, 0..1.</param>
/// <param name="PickRate">How often the card is taken when offered, 0..1.</param>
public sealed record ArenaCardScore(string CardId, int DbfId, double? Score, double? WinRate, double? PickRate, int Games)
{
    /// <summary>Tier letter of <see cref="Score"/>, or null without a score.</summary>
    public string? Tier => ArenaMeta.TierOf(Score);
}

/// <summary>Community results of one Arena class.</summary>
/// <param name="PlayerClass">Upper-case class key as the card data writes it (WARLOCK, DEATHKNIGHT, DEMONHUNTER...).</param>
/// <param name="WinRate">Win rate, 0..1.</param>
/// <param name="PickRate">How often the class is taken when offered, 0..1.</param>
/// <param name="SevenPlus">Share of runs that reach 7 wins or more, 0..1.</param>
public sealed record ArenaClassStat(string PlayerClass, double WinRate, double? PickRate, double? SevenPlus, int Drafts);

/// <summary>
/// Arena card scores (ArenaSmith) and class results from the Hearthpulse API. The server publishes one all-classes
/// slice of the cards, so a neutral card has one score whatever class drafts it.
/// </summary>
public sealed class ArenaMeta
{
    /// <summary>What the numbers go by in the UI.</summary>
    public const string Attribution = HearthpulseData.Attribution;

    private readonly Dictionary<string, ArenaCardScore> _byId;
    private readonly Dictionary<int, ArenaCardScore> _byDbf;
    private readonly Dictionary<string, ArenaClassStat> _byClass;

    public ArenaMeta(IEnumerable<ArenaCardScore> cards, IEnumerable<ArenaClassStat> classes, DateTime? fetchedAt = null)
    {
        _byId = new Dictionary<string, ArenaCardScore>(StringComparer.Ordinal);
        _byDbf = new Dictionary<int, ArenaCardScore>();
        foreach (var card in cards)
        {
            _byId.TryAdd(card.CardId, card);
            if (card.DbfId > 0)
            {
                _byDbf.TryAdd(card.DbfId, card);
            }
        }

        Classes = classes.OrderByDescending(c => c.WinRate).ToList();
        _byClass = Classes.GroupBy(c => c.PlayerClass, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        FetchedAt = fetchedAt;
    }

    public static ArenaMeta Empty { get; } = new([], []);

    /// <summary>Classes, best win rate first.</summary>
    public IReadOnlyList<ArenaClassStat> Classes { get; }

    /// <summary>When the server fetched the card numbers.</summary>
    public DateTime? FetchedAt { get; }

    public int CardCount => _byId.Count;

    public bool HasCards => _byId.Count > 0;

    public ArenaCardScore? Card(string? cardId) => cardId is { Length: > 0 } && _byId.TryGetValue(cardId, out var card) ? card : null;

    public ArenaCardScore? Card(int dbfId) => _byDbf.GetValueOrDefault(dbfId);

    public double? Score(string? cardId) => Card(cardId)?.Score;

    public ArenaClassStat? Class(string? playerClass) =>
        playerClass is { Length: > 0 } && _byClass.TryGetValue(playerClass.ToUpperInvariant(), out var stat) ? stat : null;

    /// <summary>
    /// Tier letter of an ArenaSmith score, with the hearthpulse.net tier list's thresholds (S 53+, A 45+, B 35+, C 24+,
    /// D 10+, else E), so the overlay and the site agree.
    /// </summary>
    public static string? TierOf(double? score) => score switch
    {
        null => null,
        >= 53 => "S",
        >= 45 => "A",
        >= 35 => "B",
        >= 24 => "C",
        >= 10 => "D",
        _ => "E",
    };

    /// <summary>
    /// Class key of the card data from the API's class names ("Deathknight", "Demon Hunter", "death-knight"), or
    /// null for something else.
    /// </summary>
    public static string? ClassKey(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var key = new string(name.Where(char.IsLetter).ToArray()).ToUpperInvariant();
        return key is "DEATHKNIGHT" or "DEMONHUNTER" or "DRUID" or "HUNTER" or "MAGE" or "PALADIN" or "PRIEST" or "ROGUE"
            or "SHAMAN" or "WARLOCK" or "WARRIOR"
            ? key
            : null;
    }
}
