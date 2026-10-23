using System.Globalization;
using System.Text.Json;

namespace Hearthpulse.Core.Constructed;

/// <summary>How one card does in an archetype's opening hand. Coin and play games are added together.</summary>
public sealed record MetaMulliganCard(string CardId, int Drawn, double KeepRate, int InHand, double InHandWinRate);

public sealed record MetaArchetype(
    int Id,
    string Key,
    string Name,
    string Class,
    int Games,
    int Wins,
    double WinRate,
    double Share,
    int FirstGames,
    double FirstWinRate,
    int CoinGames,
    double CoinWinRate,
    IReadOnlyList<string> CoreCards,
    IReadOnlyList<MetaMulliganCard> Mulligan)
{
    /// <summary>
    /// The win rate the mulligan numbers compare against: the deck's win rate over the same games (they come from
    /// HSReplay while <see cref="WinRate"/> is HSGuru's). Null means <see cref="WinRate"/> (one source for both).
    /// </summary>
    public double? MulliganBaseline { get; init; }

    /// <summary>The HSReplay archetype the mulligan numbers belong to; several HSGuru archetypes can share one.</summary>
    public int? MulliganArchetypeId { get; init; }
}

public sealed record MetaDeck(string DeckString, int ArchetypeId, string Archetype, string Class, int Games, int Wins, double WinRate, DateTime LastSeen);

public sealed record MetaOption(string Key, string Label);

/// <summary>
/// Ranked Constructed stats: archetypes with win rate, popularity and mulligan data, plus the most played decklists.
/// One instance holds one format, rank and time period. The app loads them from the Hearthpulse API
/// (<see cref="ApiConstructedMeta"/>: HSGuru meta and decks, HSReplay mulligan).
/// </summary>
public sealed class ConstructedMeta
{
    public const string Attribution = Api.HearthpulseData.Attribution;

    /// <summary>Archetypes and decks with fewer games are too noisy to show.</summary>
    public const int MinArchetypeGames = 100;

    public const int MinDeckGames = 50;

    public static IReadOnlyList<MetaOption> Formats { get; } = [new("standard", "Standard"), new("wild", "Wild")];

    /// <summary>
    /// HSGuru's rank brackets (the keys are the API's). The API also has diamond, diamond_4to1 and top_legend; six
    /// fit the page's filter row.
    /// </summary>
    public static IReadOnlyList<MetaOption> Ranks { get; } =
    [
        new("all", "All ranks"),
        new("diamond_to_legend", "Diamond+"),
        new("legend", "Legend"),
        new("top_5k", "Top 5K"),
        new("top_500", "Top 500"),
        new("top_100", "Top 100"),
    ];

    /// <summary>
    /// HSGuru's periods; "expansion" and "patch" stand for whatever the API calls the current ones. The API also has
    /// 3 and 6 hours and 2 weeks.
    /// </summary>
    public static IReadOnlyList<MetaOption> Periods { get; } =
    [
        new("past_day", "1 day"),
        new("past_3_days", "3 days"),
        new("past_week", "7 days"),
        new("past_30_days", "30 days"),
        new("patch", "Patch"),
        new("expansion", "Expansion"),
    ];

    /// <summary>Filter keys saved by earlier versions (Firestone's) and the option they become.</summary>
    private static readonly Dictionary<string, string> OldKeys = new(StringComparer.Ordinal)
    {
        ["bronze-gold"] = "all",
        ["platinum"] = "all",
        ["diamond"] = "diamond_to_legend",
        ["top-2000-legend"] = "top_5k",
        ["past-3"] = "past_3_days",
        ["past-7"] = "past_week",
        ["past-20"] = "past_30_days",
        ["last-patch"] = "patch",
    };

    private static readonly HashSet<string> Acronyms = new(StringComparer.Ordinal) { "dk", "dh", "xl", "otk", "bbu", "bub", "ubb", "fff", "uuu", "bbb" };

    internal ConstructedMeta(string format, string rank, string period, DateTime updated, int games, IReadOnlyList<MetaArchetype> archetypes, IReadOnlyList<MetaDeck> decks)
    {
        Format = format;
        Rank = rank;
        Period = period;
        Updated = updated;
        Games = games;
        Archetypes = archetypes;
        Decks = decks;
    }

    public string Format { get; }

    public string Rank { get; }

    public string Period { get; }

    public DateTime Updated { get; }

    /// <summary>Games behind the archetype numbers.</summary>
    public int Games { get; }

    /// <summary>Where these stats came from, for the attribution line (<see cref="Attribution"/> unless a fallback was used).</summary>
    public string Source { get; internal init; } = Attribution;

    /// <summary>Most played first.</summary>
    public IReadOnlyList<MetaArchetype> Archetypes { get; }

    /// <summary>Most played first.</summary>
    public IReadOnlyList<MetaDeck> Decks { get; }

    public static string Key(IReadOnlyList<MetaOption> options, string? key) => Option(options, key).Key;

    public static string Label(IReadOnlyList<MetaOption> options, string? key) => Option(options, key).Label;

    private static MetaOption Option(IReadOnlyList<MetaOption> options, string? key) =>
        options.FirstOrDefault(o => string.Equals(o.Key, key, StringComparison.Ordinal))
        ?? options.FirstOrDefault(o => key is not null && OldKeys.TryGetValue(key, out var renamed) && o.Key == renamed)
        ?? options[0];

    /// <summary>
    /// Loads the stats from the Hearthpulse API (<see cref="ApiConstructedMeta"/>): empty when the slice has no
    /// archetypes, null when it could not be loaded.
    /// </summary>
    /// <param name="locale">The card language; "ruRU" shows Russian archetype names.</param>
    /// <param name="tierList">Gets the tier list early, before the mulligan numbers are in.</param>
    public static Task<ConstructedMeta?> LoadAsync(Api.HearthpulseData data, Cards.CardDb cards, string? format, string? rank, string? period, string? locale,
        IProgress<ConstructedMeta>? tierList = null, CancellationToken cancellationToken = default) =>
        ApiConstructedMeta.LoadAsync(data, cards, Key(Formats, format), Key(Ranks, rank ?? "all"), Key(Periods, period ?? "past_week"), locale, tierList, cancellationToken);

    /// <summary>The <see cref="Format"/> key ("standard", "wild") of a game's FormatType; null for formats without stats.</summary>
    public static string? FormatKeyOf(string? formatType) => formatType switch
    {
        "FT_STANDARD" => "standard",
        "FT_WILD" => "wild",
        _ => null,
    };

    /// <summary>Archetypes of one class (a class key such as "priest" or "PRIEST"), most played first.</summary>
    public IEnumerable<MetaArchetype> ForClass(string? heroClass) =>
        Archetypes.Where(a => string.Equals(a.Class, heroClass, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The archetype a deck belongs to: the one of its class whose core cards it holds the largest share of, when it
    /// holds at least 60% of them.
    /// </summary>
    public MetaArchetype? Match(string? heroClass, IReadOnlyCollection<string> cardIds)
    {
        if (cardIds.Count == 0)
        {
            return null;
        }

        var owned = new HashSet<string>(cardIds, StringComparer.Ordinal);
        MetaArchetype? best = null;
        var bestScore = 0.0;
        foreach (var archetype in ForClass(heroClass))
        {
            var core = archetype.CoreCards.Distinct(StringComparer.Ordinal).ToList();
            if (core.Count == 0)
            {
                continue;
            }

            var score = (double)core.Count(owned.Contains) / core.Count;
            if (score >= 0.6 && (best is null || score > bestScore || (score == bestScore && archetype.Games > best.Games)))
            {
                best = archetype;
                bestScore = score;
            }
        }

        return best;
    }

    internal static ConstructedMeta Parse(string format, string rank, string period, JsonElement? archetypes, JsonElement? decks)
    {
        var updated = DateTime.MinValue;
        var games = 0;
        var archetypeList = new List<MetaArchetype>();
        if (archetypes is { ValueKind: JsonValueKind.Object } a)
        {
            updated = Date(a, "lastUpdated");
            if (a.TryGetProperty("archetypeStats", out var stats) && stats.ValueKind == JsonValueKind.Array)
            {
                var parsed = stats.EnumerateArray().Select(ParseArchetype).OfType<MetaArchetype>().ToList();
                games = parsed.Sum(x => x.Games);
                // Tiny archetypes swing wildly: keep the ones with 0.1% of the games or more.
                var minGames = Math.Max(MinArchetypeGames, games / 1000);
                archetypeList = parsed
                    .Where(x => x.Games >= minGames)
                    .Select(x => x with { Share = games > 0 ? (double)x.Games / games : 0 })
                    .OrderByDescending(x => x.Games)
                    .ToList();
            }
        }

        var names = archetypeList.ToDictionary(x => x.Id, x => x.Name);
        var deckList = new List<MetaDeck>();
        if (decks is { ValueKind: JsonValueKind.Object } d)
        {
            if (updated == DateTime.MinValue)
            {
                updated = Date(d, "lastUpdated");
            }

            if (d.TryGetProperty("deckStats", out var stats) && stats.ValueKind == JsonValueKind.Array)
            {
                foreach (var deck in stats.EnumerateArray())
                {
                    if (deck.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var code = GetString(deck, "decklist");
                    var played = GetInt(deck, "totalGames");
                    if (code.Length == 0 || played < MinDeckGames)
                    {
                        continue;
                    }

                    var heroClass = GetString(deck, "playerClass");
                    var archetypeId = GetInt(deck, "archetypeId");
                    var wins = GetInt(deck, "totalWins");
                    deckList.Add(new MetaDeck(
                        code,
                        archetypeId,
                        names.GetValueOrDefault(archetypeId) ?? Prettify(GetString(deck, "archetypeName"), heroClass),
                        heroClass,
                        played,
                        wins,
                        (double)wins / played,
                        Date(deck, "lastUpdate")));
                }
            }
        }

        deckList.Sort((x, y) => y.Games.CompareTo(x.Games));
        return new ConstructedMeta(format, rank, period, updated, games, archetypeList, deckList);
    }

    private static MetaArchetype? ParseArchetype(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var played = GetInt(e, "totalGames");
        if (played <= 0)
        {
            return null;
        }

        var heroClass = GetString(e, "heroCardClass");
        var key = GetString(e, "name");
        var wins = GetInt(e, "totalWins");
        int firstGames = 0, firstWins = 0, coinGames = 0, coinWins = 0;
        var cards = new Dictionary<string, (int Drawn, int Kept, int InHand, int InHandWins)>(StringComparer.Ordinal);
        if (e.TryGetProperty("coinPlayInfo", out var split) && split.ValueKind == JsonValueKind.Array)
        {
            foreach (var side in split.EnumerateArray())
            {
                if (side.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var sideGames = GetInt(side, "totalGames");
                var sideWins = GetInt(side, "wins");
                if (GetString(side, "coinPlay") == "coin")
                {
                    coinGames += sideGames;
                    coinWins += sideWins;
                }
                else
                {
                    firstGames += sideGames;
                    firstWins += sideWins;
                }

                if (!side.TryGetProperty("cardsData", out var cardData) || cardData.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var card in cardData.EnumerateArray())
                {
                    var id = card.ValueKind == JsonValueKind.Object ? GetString(card, "cardId") : string.Empty;
                    if (id.Length == 0)
                    {
                        continue;
                    }

                    var sum = cards.GetValueOrDefault(id);
                    cards[id] = (
                        sum.Drawn + GetInt(card, "drawnBeforeMulligan"),
                        sum.Kept + GetInt(card, "keptInMulligan"),
                        sum.InHand + GetInt(card, "inHandAfterMulligan"),
                        sum.InHandWins + GetInt(card, "inHandAfterMulliganThenWin"));
                }
            }
        }

        // Cards seen in the opening hand too rarely say nothing about the mulligan.
        var minInHand = Math.Max(20, played / 50);
        var mulligan = cards
            .Where(c => c.Value.InHand >= minInHand && c.Value.Drawn > 0)
            .Select(c => new MetaMulliganCard(
                c.Key,
                c.Value.Drawn,
                Math.Min(1, (double)c.Value.Kept / c.Value.Drawn),
                c.Value.InHand,
                (double)c.Value.InHandWins / c.Value.InHand))
            .OrderByDescending(c => c.InHandWinRate)
            .ToList();

        var core = e.TryGetProperty("coreCards", out var coreElement) && coreElement.ValueKind == JsonValueKind.Array
            ? coreElement.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.String).Select(c => c.GetString()!).ToList()
            : [];

        return new MetaArchetype(
            GetInt(e, "id"),
            key,
            Prettify(key, heroClass),
            heroClass,
            played,
            wins,
            (double)wins / played,
            0,
            firstGames,
            firstGames > 0 ? (double)firstWins / firstGames : 0,
            coinGames,
            coinGames > 0 ? (double)coinWins / coinGames : 0,
            core,
            mulligan);
    }

    /// <summary>"Death Knight" for "deathknight" or "DEATHKNIGHT".</summary>
    public static string ClassName(string? heroClass)
    {
        if (string.IsNullOrEmpty(heroClass))
        {
            return string.Empty;
        }

        return heroClass.ToUpperInvariant() switch
        {
            "DEATHKNIGHT" => "Death Knight",
            "DEMONHUNTER" => "Demon Hunter",
            var upper => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(upper.ToLowerInvariant()),
        };
    }

    /// <summary>"xl-rafaamlock" becomes "XL Rafaamlock"; a bucket named after its class becomes "Other Warlock".</summary>
    internal static string Prettify(string key, string heroClass)
    {
        if (key.Length == 0)
        {
            return ClassName(heroClass);
        }

        if (string.Equals(key.Replace("-", string.Empty, StringComparison.Ordinal), heroClass, StringComparison.OrdinalIgnoreCase))
        {
            return "Other " + ClassName(heroClass);
        }

        var words = key.Split('-', StringSplitOptions.RemoveEmptyEntries).Select(w => w switch
        {
            "cthun" => "C'Thun",
            "deathknight" => "Death Knight",
            "demonhunter" => "Demon Hunter",
            _ when Acronyms.Contains(w) => w.ToUpperInvariant(),
            _ => char.ToUpperInvariant(w[0]) + w[1..],
        });
        return string.Join(' ', words);
    }

    private static string GetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

    private static int GetInt(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;

    private static DateTime Date(JsonElement e, string name) =>
        DateTime.TryParse(GetString(e, name), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date)
            ? date
            : DateTime.MinValue;
}
