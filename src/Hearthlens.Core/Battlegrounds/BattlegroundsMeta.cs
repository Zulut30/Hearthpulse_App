using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace Hearthlens.Core.Battlegrounds;

public sealed record MetaTribeImpact(int Tribe, double Impact);

public sealed record MetaHero(
    string HeroCardId,
    int DataPoints,
    double AveragePosition,
    double PickRate,
    IReadOnlyDictionary<int, double> TribeImpact,
    double Top4Rate = 0,
    double FirstRate = 0);

public sealed record MetaTrinket(
    string CardId,
    int DataPoints,
    double AveragePlacement,
    double PickRate,
    IReadOnlyDictionary<int, (double Placement, int DataPoints)> PlacementAtMmr);

public sealed record MetaCompCard(string CardId, string Status);

/// <summary>
/// Community results for one card (minion or tavern spell): the average final placement of players who
/// played it, overall and per turn it was played.
/// </summary>
public sealed record MetaCard(
    string CardId,
    int DataPoints,
    double AveragePlacement,
    IReadOnlyDictionary<int, (int Played, double Placement)> Turns);

public sealed record MetaComp(
    string Id,
    string Name,
    string PowerLevel,
    string Difficulty,
    IReadOnlyList<string> Tribes,
    IReadOnlyList<MetaCompCard> Cards,
    string? WhenToCommit,
    string? Tip,
    double AveragePlacement,
    IReadOnlyDictionary<int, double> PlacementAtMmr,
    IReadOnlyDictionary<string, (int DataPoints, double Placement)> HeroPlacement);

/// <summary>
/// Community Battlegrounds statistics (hero placement per MMR bracket and tribe, comp archetypes) from the
/// public data files published by Firestone (zerotoheroes.com). Cached on disk; a section that fails to parse
/// is skipped rather than discarding the rest of the data.
/// </summary>
public sealed class BattlegroundsMeta
{
    public const string Attribution = "Community stats: Firestone (zerotoheroes.com)";

    private static readonly int[] Brackets = [100, 50, 25, 10, 1];
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(6);
    private const string Root = "https://static.zerotoheroes.com/";

    private static readonly Dictionary<string, string> PrefixTribes = new(StringComparer.Ordinal)
    {
        ["beast"] = "BEAST", ["dragon"] = "DRAGON", ["aberration"] = "ABERRATION", ["abberation"] = "ABERRATION",
        ["murloc"] = "MURLOC", ["demon"] = "DEMON", ["quilboar"] = "QUILBOAR", ["mech"] = "MECHANICAL",
        ["pirate"] = "PIRATE", ["elemental"] = "ELEMENTAL", ["undead"] = "UNDEAD", ["naga"] = "NAGA",
    };

    /// <summary>Built by <see cref="LoadAsync"/>; internal so tests can build small offline data sets.</summary>
    internal BattlegroundsMeta(
        IReadOnlyDictionary<int, IReadOnlyDictionary<string, MetaHero>> heroes,
        IReadOnlyList<(int Percentile, int Mmr)> percentiles,
        IReadOnlyList<MetaComp> comps,
        IReadOnlyDictionary<string, MetaTrinket> trinkets,
        IReadOnlyDictionary<int, IReadOnlyDictionary<string, MetaCard>> cards,
        DateTime updated)
    {
        HeroesByBracket = heroes;
        Percentiles = percentiles;
        Comps = comps;
        Trinkets = trinkets;
        CardsByBracket = cards;
        Updated = updated;
    }

    /// <summary>Minimum plays before a card's placement is trusted.</summary>
    public const int MinCardGames = 200;

    /// <summary>Card results per MMR bracket (100 = all ranks).</summary>
    public IReadOnlyDictionary<int, IReadOnlyDictionary<string, MetaCard>> CardsByBracket { get; }

    /// <summary>All-ranks results for a card, or null.</summary>
    public MetaCard? Card(string cardId) =>
        CardsByBracket.TryGetValue(100, out var all) ? all.GetValueOrDefault(cardId) : null;

    /// <summary>
    /// Average final placement of players who played the card around <paramref name="turn"/>. Nearby turns are
    /// pooled until the sample reaches <see cref="MinCardGames"/>; a thin bracket falls back to all ranks.
    /// Comparing cards at the same turn removes most of the "late cards look better" survivor effect.
    /// </summary>
    public (double Placement, int Games)? CardAtTurn(string cardId, int turn, int bracket)
    {
        foreach (var b in bracket == 100 ? [100] : new[] { bracket, 100 })
        {
            if (!CardsByBracket.TryGetValue(b, out var cards) || !cards.TryGetValue(cardId, out var card))
            {
                continue;
            }

            for (var spread = 0; spread <= 3; spread++)
            {
                var games = 0;
                var sum = 0.0;
                for (var t = turn - spread; t <= turn + spread; t++)
                {
                    if (card.Turns.TryGetValue(t, out var stats))
                    {
                        games += stats.Played;
                        sum += stats.Placement * stats.Played;
                    }
                }

                if (games >= MinCardGames)
                {
                    return (sum / games, games);
                }
            }
        }

        return null;
    }

    public IReadOnlyDictionary<string, MetaTrinket> Trinkets { get; }

    /// <summary>Trinket placement at the bracket, falling back to all ranks when the bracket sample is thin.</summary>
    public (double Placement, double PickRate, int DataPoints)? Trinket(string cardId, int bracket)
    {
        if (!Trinkets.TryGetValue(cardId, out var trinket) || trinket.DataPoints < 50)
        {
            return null;
        }

        return trinket.PlacementAtMmr.TryGetValue(bracket, out var at) && at.DataPoints >= 50
            ? (at.Placement, trinket.PickRate, at.DataPoints)
            : (trinket.AveragePlacement, trinket.PickRate, trinket.DataPoints);
    }

    public IReadOnlyDictionary<int, IReadOnlyDictionary<string, MetaHero>> HeroesByBracket { get; }

    public IReadOnlyList<(int Percentile, int Mmr)> Percentiles { get; }

    public IReadOnlyList<MetaComp> Comps { get; }

    public DateTime Updated { get; }

    /// <summary>Tightest MMR bracket (top X%) whose floor the rating reaches.</summary>
    public int BracketFor(int? rating)
    {
        if (rating is not > 0)
        {
            return 100;
        }

        var best = 100;
        foreach (var (percentile, mmr) in Percentiles)
        {
            if (rating >= mmr && percentile < best && HeroesByBracket.ContainsKey(percentile))
            {
                best = percentile;
            }
        }

        return best;
    }

    public MetaHero? Hero(string baseHeroId, int bracket)
    {
        if (HeroesByBracket.TryGetValue(bracket, out var heroes) && heroes.TryGetValue(baseHeroId, out var hero) && hero.DataPoints >= 100)
        {
            return hero;
        }

        return HeroesByBracket.TryGetValue(100, out var all) ? all.GetValueOrDefault(baseHeroId) : null;
    }

    public static async Task<BattlegroundsMeta?> LoadAsync(HttpClient http, CancellationToken cancellationToken = default)
    {
        try
        {
            var heroes = new Dictionary<int, IReadOnlyDictionary<string, MetaHero>>();
            var percentiles = new List<(int, int)>();
            DateTime updated = default;
            foreach (var bracket in Brackets)
            {
                var doc = await FetchAsync(http, $"api/bgs/hero-stats/mmr-{bracket}/last-patch/overview-from-hourly.gz.json", $"heroes-{bracket}.json", cancellationToken).ConfigureAwait(false);
                if (doc is null)
                {
                    continue;
                }

                using (doc)
                {
                    Guard($"heroes {bracket}", () =>
                    {
                        heroes[bracket] = ParseHeroes(doc.RootElement);
                        if (bracket != 100)
                        {
                            return;
                        }

                        if (doc.RootElement.TryGetProperty("mmrPercentiles", out var pct) && pct.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var p in pct.EnumerateArray())
                            {
                                if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty("percentile", out var percent) && percent.ValueKind == JsonValueKind.Number && percent.TryGetInt32(out var percentileValue)
                                    && p.TryGetProperty("mmr", out var mmr) && mmr.ValueKind == JsonValueKind.Number && mmr.TryGetInt32(out var mmrValue))
                                {
                                    percentiles.Add((percentileValue, mmrValue));
                                }
                            }
                        }

                        if (doc.RootElement.TryGetProperty("lastUpdateDate", out var date) && date.ValueKind == JsonValueKind.String && date.TryGetDateTime(out var parsed))
                        {
                            updated = parsed;
                        }
                    });
                }
            }

            if (!heroes.ContainsKey(100))
            {
                return null;
            }

            var comps = new List<MetaComp>();
            using (var strategies = await FetchAsync(http, "hearthstone/data/battlegrounds-strategies/bgs-comps-strategies.gz.json", "comp-strategies.json", cancellationToken).ConfigureAwait(false))
            {
                var compStatsFile = await CacheFileAsync(http, "api/bgs/comp-stats/last-patch/overview-from-hourly.gz.json", "comp-stats.json", cancellationToken).ConfigureAwait(false);
                if (compStatsFile is not null)
                {
                    // The comp file is tens of megabytes. It is streamed instead of parsed into a document, which
                    // would need several hundred megabytes of memory and stalls the app on a small PC.
                    Guard("comps", () => comps.AddRange(ParseCompsFile(compStatsFile, strategies?.RootElement)));
                }
            }

            var trinkets = new Dictionary<string, MetaTrinket>(StringComparer.Ordinal);
            using (var trinketStats = await FetchAsync(http, "api/bgs/trinket-stats/last-patch/overview-from-hourly.gz.json", "trinket-stats.json", cancellationToken).ConfigureAwait(false))
            {
                if (trinketStats is not null)
                {
                    Guard("trinkets", () => trinkets = ParseTrinkets(trinketStats.RootElement));
                }
            }

            var cardStats = new Dictionary<int, IReadOnlyDictionary<string, MetaCard>>();
            foreach (var bracket in Brackets)
            {
                using var doc = await FetchAsync(http, $"api/bgs/card-stats/mmr-{bracket}/last-patch/overview-from-hourly.gz.json", $"cards-{bracket}.json", cancellationToken).ConfigureAwait(false);
                if (doc is null)
                {
                    continue;
                }

                Guard($"cards {bracket}", () =>
                {
                    if (ParseCards(doc.RootElement) is { Count: > 0 } parsed)
                    {
                        cardStats[bracket] = parsed;
                    }
                });
            }

            return new BattlegroundsMeta(heroes, percentiles, comps, trinkets, cardStats, updated);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IOException)
        {
            Diag.Error("Battlegrounds meta unreadable", ex);
            return null;
        }
    }

    /// <summary>Parses one section; a malformed section is logged and skipped instead of losing the whole meta.</summary>
    private static void Guard(string section, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or InvalidCastException or FormatException or ArgumentException)
        {
            Diag.Error($"Battlegrounds meta section unreadable ({section})", ex);
        }
    }

    internal static Dictionary<string, MetaHero> ParseHeroes(JsonElement root)
    {
        var result = new Dictionary<string, MetaHero>(StringComparer.Ordinal);
        if (!root.TryGetProperty("heroStats", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var hero in list.EnumerateArray())
        {
            if (hero.ValueKind != JsonValueKind.Object
                || !hero.TryGetProperty("heroCardId", out var idElement) || idElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var id = idElement.GetString() ?? string.Empty;
            if (id.Length == 0)
            {
                continue;
            }

            var impact = new Dictionary<int, double>();
            if (hero.TryGetProperty("tribeStats", out var tribes) && tribes.ValueKind == JsonValueKind.Array)
            {
                foreach (var tribe in tribes.EnumerateArray())
                {
                    if (tribe.ValueKind == JsonValueKind.Object
                        && tribe.TryGetProperty("tribe", out var tribeId) && tribeId.ValueKind == JsonValueKind.Number && tribeId.TryGetInt32(out var tribeValue)
                        && tribe.TryGetProperty("impactAveragePosition", out var impactValue) && impactValue.ValueKind == JsonValueKind.Number)
                    {
                        impact[tribeValue] = impactValue.GetDouble();
                    }
                }
            }

            double top4 = 0, first = 0;
            if (hero.TryGetProperty("placementDistribution", out var places) && places.ValueKind == JsonValueKind.Array)
            {
                foreach (var place in places.EnumerateArray())
                {
                    if (place.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var rank = GetInt(place, "rank");
                    var share = GetDouble(place, "percentage") / 100;
                    if (rank is >= 1 and <= 4)
                    {
                        top4 += share;
                    }

                    if (rank == 1)
                    {
                        first = share;
                    }
                }
            }

            var offered = GetDouble(hero, "totalOffered");
            var picked = GetDouble(hero, "totalPicked");
            result[id] = new MetaHero(
                id,
                GetInt(hero, "dataPoints"),
                GetDouble(hero, "averagePosition"),
                offered > 0 ? picked / offered : 0,
                impact,
                Math.Min(1, top4),
                first);
        }

        return result;
    }

    internal static IEnumerable<MetaComp> ParseComps(JsonElement stats, JsonElement? strategies)
    {
        var strategyById = StrategyIndex(strategies);
        if (!stats.TryGetProperty("compStats", out var compList) || compList.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var comp in compList.EnumerateArray())
        {
            if (comp.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = GetString(comp, "archetype");
            var dataPoints = GetInt(comp, "dataPoints");
            if (id.Length == 0 || dataPoints < 500)
            {
                continue;
            }

            var atMmr = new Dictionary<int, double>();
            if (comp.TryGetProperty("averagePlacementAtMmr", out var mmrList) && mmrList.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in mmrList.EnumerateArray())
                {
                    if (m.ValueKind == JsonValueKind.Object && GetInt(m, "dataPoints") >= 200
                        && m.TryGetProperty("mmr", out var mmr) && mmr.ValueKind == JsonValueKind.Number && mmr.TryGetInt32(out var mmrValue))
                    {
                        atMmr[mmrValue] = GetDouble(m, "placement");
                    }
                }
            }

            var heroPlacement = new Dictionary<string, (int, double)>(StringComparer.Ordinal);
            if (comp.TryGetProperty("heroStats", out var heroList) && heroList.ValueKind == JsonValueKind.Array)
            {
                foreach (var h in heroList.EnumerateArray())
                {
                    if (h.ValueKind == JsonValueKind.Object && GetString(h, "heroCardId") is { Length: > 0 } heroId)
                    {
                        heroPlacement[heroId] = (GetInt(h, "dataPoints"), GetDouble(h, "averagePlacement"));
                    }
                }
            }

            yield return BuildComp(id, GetDouble(comp, "averagePlacement"), atMmr, heroPlacement, strategyById);
        }
    }

    /// <summary>
    /// Reads the comp statistics straight from the file. The file is tens of megabytes; a document would cost
    /// several hundred megabytes of memory and stall the app on a small PC, so only the fields the advisor uses
    /// are read and everything else is skipped.
    /// </summary>
    internal static List<MetaComp> ParseCompsFile(string path, JsonElement? strategies)
    {
        var strategyById = StrategyIndex(strategies);
        var result = new List<MetaComp>();
        var bytes = File.ReadAllBytes(path);
        var reader = new Utf8JsonReader(bytes);
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName || reader.GetString() != "compStats")
            {
                continue;
            }

            if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
            {
                break;
            }

            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                var comp = ReadCompStats(ref reader, strategyById);
                if (comp is not null)
                {
                    result.Add(comp);
                }
            }

            break;
        }

        return result;
    }

    private static MetaComp? ReadCompStats(ref Utf8JsonReader reader, Dictionary<string, JsonElement> strategyById)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip();
            return null;
        }

        var id = string.Empty;
        var dataPoints = 0;
        var average = 0d;
        var atMmr = new Dictionary<int, double>();
        var heroPlacement = new Dictionary<string, (int, double)>(StringComparer.Ordinal);

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }

            var property = reader.GetString();
            if (!reader.Read())
            {
                break;
            }

            switch (property)
            {
                case "archetype":
                    id = reader.TokenType == JsonTokenType.String ? reader.GetString() ?? string.Empty : string.Empty;
                    break;
                case "dataPoints":
                    dataPoints = reader.TryGetInt32(out var points) ? points : 0;
                    break;
                case "averagePlacement":
                    average = reader.TokenType == JsonTokenType.Number ? reader.GetDouble() : 0;
                    break;
                case "averagePlacementAtMmr":
                    ReadPlacementAtMmr(ref reader, atMmr);
                    break;
                case "heroStats":
                    ReadHeroStats(ref reader, heroPlacement);
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        return id.Length == 0 || dataPoints < 500
            ? null
            : BuildComp(id, average, atMmr, heroPlacement, strategyById);
    }

    private static void ReadPlacementAtMmr(ref Utf8JsonReader reader, Dictionary<int, double> atMmr)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            reader.Skip();
            return;
        }

        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                reader.Skip();
                continue;
            }

            var mmr = -1;
            var points = 0;
            var placement = 0d;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    continue;
                }

                var name = reader.GetString();
                if (!reader.Read())
                {
                    break;
                }

                switch (name)
                {
                    case "mmr": mmr = reader.TryGetInt32(out var m) ? m : -1; break;
                    case "dataPoints": points = reader.TryGetInt32(out var p) ? p : 0; break;
                    case "placement": placement = reader.TokenType == JsonTokenType.Number ? reader.GetDouble() : 0; break;
                    default: reader.Skip(); break;
                }
            }

            if (mmr >= 0 && points >= 200)
            {
                atMmr[mmr] = placement;
            }
        }
    }

    private static void ReadHeroStats(ref Utf8JsonReader reader, Dictionary<string, (int, double)> heroPlacement)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            reader.Skip();
            return;
        }

        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                reader.Skip();
                continue;
            }

            var heroId = string.Empty;
            var points = 0;
            var average = 0d;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    continue;
                }

                var name = reader.GetString();
                if (!reader.Read())
                {
                    break;
                }

                switch (name)
                {
                    case "heroCardId": heroId = reader.TokenType == JsonTokenType.String ? reader.GetString() ?? string.Empty : string.Empty; break;
                    case "dataPoints": points = reader.TryGetInt32(out var p) ? p : 0; break;
                    case "averagePlacement": average = reader.TokenType == JsonTokenType.Number ? reader.GetDouble() : 0; break;
                    default: reader.Skip(); break;
                }
            }

            if (heroId.Length > 0)
            {
                heroPlacement[heroId] = (points, average);
            }
        }
    }

    private static Dictionary<string, JsonElement> StrategyIndex(JsonElement? strategies)
    {
        var index = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (strategies is not { ValueKind: JsonValueKind.Array } list)
        {
            return index;
        }

        foreach (var s in list.EnumerateArray())
        {
            if (s.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = (GetString(s, "compId")).Trim();
            if (id.Length > 0)
            {
                index[id] = s;
            }
        }

        return index;
    }

    private static MetaComp BuildComp(
        string id,
        double averagePlacement,
        Dictionary<int, double> atMmr,
        Dictionary<string, (int, double)> heroPlacement,
        Dictionary<string, JsonElement> strategyById)
    {
        var cards = new List<MetaCompCard>();
        string name = Prettify(id), power = string.Empty, difficulty = string.Empty;
        string? when = null, tip = null;
        var tribes = new List<string>();
        if (strategyById.TryGetValue(id, out var strategy))
        {
            name = GetString(strategy, "name") is { Length: > 0 } n ? n : name;
            power = GetString(strategy, "powerLevel");
            difficulty = GetString(strategy, "difficulty");
            if (strategy.TryGetProperty("cards", out var cardList) && cardList.ValueKind == JsonValueKind.Array)
            {
                foreach (var card in cardList.EnumerateArray())
                {
                    if (card.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    cards.Add(new MetaCompCard(GetString(card, "cardId"), GetString(card, "status") is { Length: > 0 } st ? st : "ADDON"));
                }
            }

            if (strategy.TryGetProperty("tips", out var tips) && tips.ValueKind == JsonValueKind.Array)
            {
                var english = tips.EnumerateArray().FirstOrDefault(t => t.ValueKind == JsonValueKind.Object && GetString(t, "language") == "enUS");
                if (english.ValueKind == JsonValueKind.Object)
                {
                    tip = GetString(english, "tip") is { Length: > 0 } tv ? tv : null;
                    when = GetString(english, "whenToCommit") is { Length: > 0 } wv ? wv : null;
                }
            }

            if (strategy.TryGetProperty("forcedTribes", out var forced) && forced.ValueKind == JsonValueKind.Array)
            {
                foreach (var f in forced.EnumerateArray())
                {
                    if (f.ValueKind == JsonValueKind.Number && Cards.Races.ById.TryGetValue(f.GetInt32(), out var race))
                    {
                        tribes.Add(race);
                    }
                    else if (f.ValueKind == JsonValueKind.String && f.GetString() is { Length: > 0 } raceName)
                    {
                        tribes.Add(raceName);
                    }
                }
            }
        }

        var prefix = id.Split('_')[0];
        if (tribes.Count == 0 && PrefixTribes.TryGetValue(prefix, out var prefixTribe))
        {
            tribes.Add(prefixTribe);
        }

        return new MetaComp(id, name, power, difficulty, tribes, cards, when, tip, averagePlacement, atMmr, heroPlacement);
    }

    internal static Dictionary<string, MetaTrinket> ParseTrinkets(JsonElement root)
    {
        var result = new Dictionary<string, MetaTrinket>(StringComparer.Ordinal);
        if (!root.TryGetProperty("trinketStats", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var t in list.EnumerateArray())
        {
            if (t.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = GetString(t, "trinketCardId");
            if (id.Length == 0)
            {
                continue;
            }

            var byMmr = new Dictionary<int, (double, int)>();
            if (t.TryGetProperty("averagePlacementAtMmr", out var at) && at.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in at.EnumerateArray())
                {
                    if (m.ValueKind == JsonValueKind.Object && m.TryGetProperty("mmr", out var mmr) && mmr.ValueKind == JsonValueKind.Number && mmr.TryGetInt32(out var mmrValue))
                    {
                        byMmr[mmrValue] = (GetDouble(m, "placement"), GetInt(m, "dataPoints"));
                    }
                }
            }

            result[id] = new MetaTrinket(
                id,
                GetInt(t, "dataPoints"),
                GetDouble(t, "averagePlacement"),
                GetDouble(t, "pickRate"),
                byMmr);
        }

        return result;
    }

    internal static Dictionary<string, MetaCard> ParseCards(JsonElement root)
    {
        var result = new Dictionary<string, MetaCard>(StringComparer.Ordinal);
        if (!root.TryGetProperty("cardStats", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var c in list.EnumerateArray())
        {
            if (c.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = GetString(c, "cardId");
            if (id.Length == 0 || !c.TryGetProperty("averagePlacement", out var avg) || avg.ValueKind != JsonValueKind.Number)
            {
                continue;
            }

            var turns = new Dictionary<int, (int, double)>();
            if (c.TryGetProperty("turnStats", out var ts) && ts.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in ts.EnumerateArray())
                {
                    if (t.ValueKind == JsonValueKind.Object
                        && t.TryGetProperty("turn", out var turn) && turn.ValueKind == JsonValueKind.Number && turn.TryGetInt32(out var turnValue)
                        && t.TryGetProperty("totalPlayed", out var played) && played.ValueKind == JsonValueKind.Number && played.TryGetInt32(out var playedValue)
                        && t.TryGetProperty("averagePlacement", out var placement) && placement.ValueKind == JsonValueKind.Number)
                    {
                        turns[turnValue] = (playedValue, placement.GetDouble());
                    }
                }
            }

            result[id] = new MetaCard(id, GetInt(c, "totalPlayed"), avg.GetDouble(), turns);
        }

        return result;
    }

    private static string GetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

    private static int GetInt(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;

    private static double GetDouble(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    private static string Prettify(string archetype) =>
        string.Join(' ', archetype.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w == "apm" ? "APM" : char.ToUpperInvariant(w[0]) + w[1..]));

    /// <summary>
    /// Makes sure the file is in the cache and returns its path. The download is streamed straight to disk, so a
    /// large file is never held in memory twice, and a response that is not JSON never replaces a good cache file.
    /// </summary>
    internal static async Task<string?> CacheFileAsync(HttpClient http, string path, string cacheName, CancellationToken cancellationToken)
    {
        var dir = Directory.CreateDirectory(Path.Combine(AppPaths.Cache, "meta")).FullName;
        var file = Path.Combine(dir, cacheName);
        var info = new FileInfo(file);
        if (!info.Exists || DateTime.UtcNow - info.LastWriteTimeUtc > MaxAge)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, Root + path);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    await using (var target = File.Create(temp))
                    {
                        await response.Content.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
                    }

                    if (LooksLikeJson(temp))
                    {
                        File.Move(temp, file, overwrite: true);
                    }
                    else
                    {
                        File.Delete(temp);
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
            {
                Diag.Error($"Meta download failed ({path})", ex);
            }
        }

        return File.Exists(file) ? file : null;
    }

    /// <summary>Guards against an error page replacing a good cache file.</summary>
    private static bool LooksLikeJson(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length < 64)
            {
                return false;
            }

            using var stream = File.OpenRead(path);
            var head = new byte[64];
            var read = stream.Read(head, 0, head.Length);
            for (var i = 0; i < read; i++)
            {
                var c = (char)head[i];
                if (char.IsWhiteSpace(c))
                {
                    continue;
                }

                return c == '{' || c == '[';
            }

            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static async Task<JsonDocument?> FetchAsync(HttpClient http, string path, string cacheName, CancellationToken cancellationToken)
    {
        var file = await CacheFileAsync(http, path, cacheName, cancellationToken).ConfigureAwait(false);
        if (file is null)
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(file);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            Diag.Error($"Meta cache unreadable ({path})", ex);
            return null;
        }
    }
}
