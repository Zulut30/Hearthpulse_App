using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Pool;

namespace Hearthpulse.Core.Battlegrounds;

/// <summary>Community results for a hero at one MMR bracket.</summary>
/// <param name="PlacementShares">Share of games ending in place 1..8 (index 0 = 1st), when the source publishes them.</param>
/// <param name="CombatWinRate">Share of combats won on each turn, when the source publishes it.</param>
public sealed record MetaHero(
    string HeroCardId,
    int DataPoints,
    double AveragePosition,
    double PickRate,
    IReadOnlyDictionary<int, double> TribeImpact,
    double Top4Rate = 0,
    double FirstRate = 0,
    IReadOnlyList<double>? PlacementShares = null,
    IReadOnlyDictionary<int, double>? CombatWinRate = null);

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
/// Community Battlegrounds statistics: heroes and minions from the Hearthpulse API (HSReplay data), with comps,
/// trinkets, tribes and turn results from the Hearthpulse stats pool when it is set up (see <see cref="PoolClient"/>).
/// Sources per number: docs/bg-meta-sources.md.
/// </summary>
public sealed class BattlegroundsMeta
{
    public const string Attribution = Api.HearthpulseData.Attribution;

    /// <summary>Attribution of stats read from the stats pool (<see cref="Pool.PoolClient"/>).</summary>
    public const string PoolAttribution = "Community stats: Hearthpulse players who share their games";

    /// <summary>Added to <see cref="Source"/> when the stats pool is laid over another source.</summary>
    public const string PoolName = "Hearthpulse stats pool";

    /// <summary>
    /// Hero slices of the API (<c>/api/bg/heroes?mmr=</c>, current patch) and the bracket each becomes. The API has no
    /// all-ranks slice for heroes: top 50% is the widest.
    /// </summary>
    internal static readonly (int Bracket, string Mmr)[] HeroSlices =
        [(50, "TOP_50_PERCENT"), (20, "TOP_20_PERCENT"), (5, "TOP_5_PERCENT"), (1, "TOP_1_PERCENT")];

    /// <summary>Minion slices of the API (<c>/v1/battlegrounds/minions/stats?mmr=</c>, current patch); 100 = all ranks.</summary>
    internal static readonly (int Bracket, string Mmr)[] CardSlices =
        [(100, "ALL"), (50, "TOP_50_PERCENT"), (20, "TOP_20_PERCENT")];

    private static readonly Dictionary<string, string> PrefixTribes = new(StringComparer.Ordinal)
    {
        ["beast"] = "BEAST", ["dragon"] = "DRAGON", ["aberration"] = "ABERRATION", ["abberation"] = "ABERRATION",
        ["murloc"] = "MURLOC", ["demon"] = "DEMON", ["quilboar"] = "QUILBOAR", ["mech"] = "MECHANICAL",
        ["pirate"] = "PIRATE", ["elemental"] = "ELEMENTAL", ["undead"] = "UNDEAD", ["naga"] = "NAGA",
    };

    /// <summary>
    /// The tribe a comp id starts with ("beast_beetle" -> BEAST). Comp guides normally carry an explicit tribe
    /// list, but some patches leave it empty; the id is then the only stable signal.
    /// </summary>
    internal static string? TribeForComp(string id) =>
        PrefixTribes.GetValueOrDefault(id.Split('_')[0]);

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

    /// <summary>
    /// The widest hero bracket: 100 (all ranks) when the source has it, else its widest top-X% slice (50 for the API).
    /// Advice and global comparisons use it when the player's own bracket is unknown.
    /// </summary>
    public int WidestBracket => HeroesByBracket.Count > 0 ? HeroesByBracket.Keys.Max() : 100;

    /// <summary>The hero brackets this meta has, widest first: what a bracket picker can offer.</summary>
    public IReadOnlyList<int> HeroBrackets => HeroesByBracket.Keys.OrderDescending().ToList();

    /// <summary>True when the stats pool is part of these numbers (alone or laid over the API).</summary>
    public bool FromPool => _source != Attribution;

    /// <summary>
    /// What an empty list of <paramref name="what"/> (comps, trinkets, tavern spells: numbers only the stats pool gives)
    /// says, so an empty list never looks like a failure or like data.
    /// </summary>
    public string PoolPending(string what) => FromPool
        ? Loc.F("Core.BgMeta.PoolPending", what)
        : Loc.F("Core.BgMeta.PoolNotRunning", what);

    /// <summary>"all ranks" or "top X%", for captions.</summary>
    public static string BracketLabel(int bracket) => bracket >= 100 ? Loc.T("Core.BgMeta.AllRanks") : Loc.F("Core.BgMeta.Top", bracket / 100.0);

    /// <summary>MMR floor of each percentile; empty when the source does not publish them (the API does not).</summary>
    public IReadOnlyList<(int Percentile, int Mmr)> Percentiles { get; }

    public IReadOnlyList<MetaComp> Comps { get; }

    public DateTime Updated { get; }

    /// <summary>
    /// Where these stats came from, for the attribution line (<see cref="Attribution"/>, the pool's, or both), in the
    /// interface language. Kept in English (<see cref="StoredSource"/>) and translated when read, so comparisons hold
    /// and a language switch shows at once.
    /// </summary>
    public string Source
    {
        get => _source
            .Replace(PoolAttribution, Loc.Or("Core.BgMeta.PoolAttribution", PoolAttribution), StringComparison.Ordinal)
            .Replace(PoolName, Loc.Or("Core.BgMeta.PoolName", PoolName), StringComparison.Ordinal);
        internal init => _source = value;
    }

    /// <summary><see cref="Source"/> as set, in English: what sources are compared and combined by.</summary>
    internal string StoredSource => _source;

    private readonly string _source = Attribution;

    /// <summary>
    /// Games a hero needs before the tier list shows it. Only the stats pool publishes sample sizes (the API's heroes have
    /// 0 = unknown and are always shown), so this is the pool's own minimum, the same one it adds missing heroes at.
    /// </summary>
    public int MinHeroGames => Pool.PoolClient.MinGames;

    /// <summary>Main tribe of final boards, from the stats pool.</summary>
    public IReadOnlyList<PoolTribe> Tribes { get; init; } = [];

    /// <summary>Shared games behind this snapshot; 0 for the imported base alone.</summary>
    public int SharedGames { get; init; }

    /// <summary>Every shared hero row, including samples below <see cref="PoolClient.MinGames"/>, for merging.</summary>
    internal IReadOnlyDictionary<string, MetaHero> SharedHeroRows { get; init; } =
        new Dictionary<string, MetaHero>(StringComparer.Ordinal);

    /// <summary>
    /// Tightest MMR bracket (top X%) whose floor the rating reaches; <see cref="WidestBracket"/> without a rating or
    /// without published floors.
    /// </summary>
    public int BracketFor(int? rating)
    {
        var best = WidestBracket;
        if (rating is not > 0)
        {
            return best;
        }

        foreach (var (percentile, mmr) in Percentiles)
        {
            if (rating >= mmr && percentile < best && HeroesByBracket.ContainsKey(percentile))
            {
                best = percentile;
            }
        }

        return best;
    }

    /// <summary>
    /// The hero's results at <paramref name="bracket"/>; a thin sample there (under 100 games) falls back to the widest
    /// bracket that has the hero. A sample size of 0 means the source does not publish it (the API) and only lists heroes
    /// with enough games, so the row is trusted.
    /// </summary>
    public MetaHero? Hero(string baseHeroId, int bracket)
    {
        if (HeroesByBracket.TryGetValue(bracket, out var heroes) && heroes.TryGetValue(baseHeroId, out var hero) && hero.DataPoints is 0 or >= 100)
        {
            return hero;
        }

        foreach (var wider in HeroesByBracket.Keys.OrderDescending())
        {
            if (HeroesByBracket[wider].TryGetValue(baseHeroId, out var found))
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// Community stats as both apps load them: heroes and minions from the Hearthpulse API (<see cref="LoadAsync"/>),
    /// and, when a stats pool is set up, the pool's meta laid over them for what the API lacks (or the pool's own meta
    /// when the API did not load). Each failure is logged; null when neither loaded. docs/bg-meta-sources.md.
    /// </summary>
    public static async Task<BattlegroundsMeta?> LoadCommunityAsync(Api.HearthpulseApi api, HttpClient http, AppSettings settings)
    {
        BattlegroundsMeta? meta = null;
        try
        {
            meta = await LoadAsync(new Api.HearthpulseData(api));
        }
        catch (Exception ex)
        {
            Diag.Error("Community stats unavailable", ex);
        }

        if (PoolClient.Available(settings))
        {
            try
            {
                meta = await PoolClient.OverlayAsync(http, PoolClient.Endpoint(settings), meta);
            }
            catch (Exception ex)
            {
                Diag.Error("Stats pool unavailable", ex);
            }
        }

        return meta;
    }

    /// <summary>
    /// Loads the community stats from the Hearthpulse API (HSReplay data): hero results for each MMR slice the API
    /// publishes (<see cref="HeroSlices"/>) and minion results per bracket (<see cref="CardSlices"/>). Comps, guides,
    /// trinkets, tribe impact, turn results and MMR floors are not in the API: they stay empty until the stats pool
    /// supplies them (<see cref="PoolClient.OverlayAsync"/>). Null when no hero slice could be had (offline, no cache).
    /// </summary>
    public static async Task<BattlegroundsMeta?> LoadAsync(Api.HearthpulseData api, CancellationToken cancellationToken = default)
    {
        var heroTasks = HeroSlices.Select(s => api.LoadHeroesAsync(s.Mmr, cancellationToken)).ToList();
        var cardTasks = CardSlices.Select(s => api.LoadMinionsAsync(s.Mmr, cancellationToken)).ToList();
        await Task.WhenAll(heroTasks.Concat<Task>(cardTasks)).ConfigureAwait(false);

        var heroes = new Dictionary<int, IReadOnlyDictionary<string, MetaHero>>();
        DateTime updated = default;
        for (var i = 0; i < HeroSlices.Length; i++)
        {
            var slice = await heroTasks[i].ConfigureAwait(false);
            if (slice.Heroes.Count > 0)
            {
                heroes[HeroSlices[i].Bracket] = slice.Heroes;
                updated = slice.FetchedAt > updated ? slice.FetchedAt : updated;
            }
        }

        if (heroes.Count == 0)
        {
            return null;
        }

        var cards = new Dictionary<int, IReadOnlyDictionary<string, MetaCard>>();
        for (var i = 0; i < CardSlices.Length; i++)
        {
            if (await cardTasks[i].ConfigureAwait(false) is { Count: > 0 } minions)
            {
                cards[CardSlices[i].Bracket] = minions;
            }
        }

        Diag.Info($"Battlegrounds meta from the Hearthpulse API: heroes in brackets {string.Join('/', heroes.Keys)}, minions in {string.Join('/', cards.Keys)}");
        return new BattlegroundsMeta(heroes, [], [], new Dictionary<string, MetaTrinket>(StringComparer.Ordinal), cards,
            updated == default ? DateTime.UtcNow : updated);
    }

    internal static string Prettify(string archetype) =>
        string.Join(' ', archetype.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w == "apm" ? "APM" : char.ToUpperInvariant(w[0]) + w[1..]));
}
