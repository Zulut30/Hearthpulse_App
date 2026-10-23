using Hearthpulse.Core.Api;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Tracking;

namespace Hearthpulse.Core.Constructed;

/// <summary>One archetype of an HSGuru meta slice (rates as fractions).</summary>
public sealed record HsguruMetaItem(string Archetype, double WinRate, double Popularity, int Games);

/// <summary>An HSGuru meta slice and the periods the API currently resolves "expansion" and "patch" to.</summary>
public sealed record HsguruMetaSlice(IReadOnlyList<HsguruMetaItem> Items, DateTime FetchedAt, string? CurrentPeriod, string? CurrentPatchPeriod);

/// <summary>An HSGuru decklist (class as HSGuru writes it, e.g. "DemonHunter").</summary>
public sealed record HsguruDeck(string Code, string Class, int Games, double WinRate, DateTime Updated);

/// <summary>An HSGuru archetype (current expansion, all ranks) and its most played lists.</summary>
public sealed record HsguruArchetype(string Name, int Games, double WinRate, IReadOnlyList<HsguruDeck> Decks);

/// <summary>
/// HSGuru's numbers for one card of an archetype: games it was in the mulligan, games it was kept, and the win rate
/// differences (fractions) when kept and when in the mulligan, against the archetype's win rate.
/// </summary>
public sealed record HsguruCardStat(string CardId, int DbfId, int InMulligan, int Kept, double? KeptImpact, double? MulliganImpact);

/// <summary>HSGuru's per-archetype analysis (Legend, past week): card numbers and results against each class.</summary>
public sealed record HsguruAnalysis(IReadOnlyList<HsguruCardStat> Cards, IReadOnlyList<GuideMatchup> ClassMatchups)
{
    /// <summary>The archetype's win rate in the same games: the class results weighted by games.</summary>
    public double? WinRate
    {
        get
        {
            var games = ClassMatchups.Sum(m => m.Games);
            return games > 0 ? ClassMatchups.Sum(m => m.WinRate * m.Games) / games : null;
        }
    }
}

/// <summary>
/// Builds <see cref="ConstructedMeta"/> from the Hearthpulse API. The tier list is HSGuru's meta for the chosen format,
/// rank and period; decklists and core cards come from HSGuru's archetype lists (current expansion, all ranks). The
/// mulligan numbers are HSReplay's (Standard, Legend, EU) for the HSReplay archetype an HSGuru archetype matches (by
/// name and class, else by its most played list's cards); for Wild and for archetypes HSReplay does not have they are
/// HSGuru's own (Legend, past week), loaded for the most played ones.
/// </summary>
public static class ApiConstructedMeta
{
    public const string Attribution = "HearthPulse · HSGuru / HSReplay данные";

    /// <summary>The 11 classes; HSGuru writes "Neutral" when it cannot tell, which is not one.</summary>
    internal static readonly HashSet<string> Classes = new(StringComparer.Ordinal)
    {
        "DEATHKNIGHT", "DEMONHUNTER", "DRUID", "HUNTER", "MAGE", "PALADIN", "PRIEST", "ROGUE", "SHAMAN", "WARLOCK", "WARRIOR",
    };

    /// <summary>A card in at least this share of an archetype's lists (by games) is one of its core cards.</summary>
    private const double CoreShare = 0.6;

    /// <summary>Parallel requests while loading many archetypes (the server is small).</summary>
    private const int Parallel = 6;

    /// <summary>HSGuru analyses loaded per meta load (one request each), most played archetypes first.</summary>
    private const int MaxAnalyses = 30;

    public const string HsguruMulliganSource = "Hearthpulse API (HSGuru), Legend, 7 days";

    /// <param name="locale">The card language; "ruRU" shows the Russian archetype labels.</param>
    /// <param name="tierList">
    /// Gets the meta as soon as the tier list, lists and core cards are in, before the mulligan numbers (many more
    /// requests) are; the returned meta has them too.
    /// </param>
    /// <returns>The meta, empty when the slice has no archetypes; null when it could not be loaded.</returns>
    public static async Task<ConstructedMeta?> LoadAsync(HearthpulseData data, CardDb cards, string format, string rank, string period, string? locale,
        IProgress<ConstructedMeta>? tierList = null, CancellationToken cancellationToken = default)
    {
        var slice = await LoadSliceAsync(data, format, rank, period, cancellationToken).ConfigureAwait(false);
        if (slice is null)
        {
            return null;
        }

        var catalog = (await data.LoadHsguruArchetypesAsync(format, cancellationToken).ConfigureAwait(false))
            .GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var russian = string.Equals(locale, "ruRU", StringComparison.OrdinalIgnoreCase);
        var labels = russian
            ? ArchetypeNames.Distinct(slice.Items.Select(i => i.Archetype).Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(n => n, ArchetypeNames.RussianLabel, StringComparer.OrdinalIgnoreCase))
            : null;

        var built = new List<(MetaArchetype Archetype, IReadOnlyList<(HsguruDeck Deck, DeckDefinition Cards)> Lists)>();
        var skipped = new List<string>();
        foreach (var item in slice.Items)
        {
            var lists = catalog.GetValueOrDefault(item.Archetype)?.Decks ?? [];
            if (ClassOf(item.Archetype, lists) is not { } heroClass)
            {
                skipped.Add(item.Archetype);
                continue;
            }

            var decoded = lists.Select(d => (Deck: d, Cards: DeckString.TryDecode(d.Code))).Where(d => d.Cards is not null).Select(d => (d.Deck, Cards: d.Cards!)).ToList();
            built.Add((new MetaArchetype(
                StableId(item.Archetype),
                item.Archetype,
                labels?.GetValueOrDefault(item.Archetype) ?? item.Archetype,
                heroClass,
                item.Games,
                (int)Math.Round(item.Games * item.WinRate),
                item.WinRate,
                item.Popularity,
                0, 0, 0, 0,
                CoreCards(decoded.Select(d => (d.Deck.Games, d.Cards)).ToList(), cards),
                []), decoded));
        }

        if (skipped.Count > 0)
        {
            Diag.Info($"Constructed meta {format}: {skipped.Count} archetypes without a class left out ({string.Join(", ", skipped.Take(5))})");
        }

        var decks = built.SelectMany(b => b.Lists.Select(d => new MetaDeck(d.Deck.Code, b.Archetype.Id, b.Archetype.Name, b.Archetype.Class, d.Deck.Games,
            (int)Math.Round(d.Deck.Games * d.Deck.WinRate), d.Deck.WinRate, d.Deck.Updated))).OrderByDescending(d => d.Games).ToList();
        ConstructedMeta Meta(IEnumerable<MetaArchetype> archetypes)
        {
            var ordered = archetypes.OrderByDescending(a => a.Games).ToList();
            return new ConstructedMeta(format, rank, period, slice.FetchedAt, ordered.Sum(a => a.Games), ordered, decks) { Source = Attribution };
        }

        tierList?.Report(Meta(built.Select(b => b.Archetype)));

        // Mulligan numbers: HSReplay where an archetype matches (Standard only), HSGuru's own for the most played rest.
        var hsreplay = format == "standard" ? await LoadHsreplayAsync(data, cancellationToken).ConfigureAwait(false) : [];
        var withMulligan = new Dictionary<int, MetaArchetype>();
        foreach (var (archetype, lists) in built)
        {
            var top = lists.OrderByDescending(d => d.Deck.Games).Select(d => d.Cards.Cards).FirstOrDefault();
            if (Match(archetype.Key, archetype.Class, top, hsreplay) is { } match)
            {
                withMulligan[archetype.Id] = archetype with
                {
                    Mulligan = Mulligan(match.Cards),
                    MulliganBaseline = MulliganGuide.Baseline(match.Cards),
                    MulliganArchetypeId = match.Archetype.Id,
                };
            }
        }

        var rest = built.Where(b => !withMulligan.ContainsKey(b.Archetype.Id)).OrderByDescending(b => b.Archetype.Games).Take(MaxAnalyses).ToList();
        var analyses = await InParallelAsync(rest, b => data.LoadHsguruAnalysisAsync(b.Archetype.Key, format, cancellationToken), cancellationToken).ConfigureAwait(false);
        for (var i = 0; i < rest.Count; i++)
        {
            var (archetype, lists) = rest[i];
            if (analyses[i] is { WinRate: { } baseline } analysis)
            {
                var rows = GuideCardsOf(analysis, cards, lists.SelectMany(l => l.Cards.Cards.Keys));
                withMulligan[archetype.Id] = archetype with { Mulligan = Mulligan(rows), MulliganBaseline = baseline };
            }
        }

        var archetypes = built.Select(b => withMulligan.GetValueOrDefault(b.Archetype.Id) ?? b.Archetype).ToList();
        Diag.Info($"Constructed meta {format}/{rank}/{period}: {archetypes.Count} archetypes, {decks.Count} lists, mulligan for "
                  + $"{archetypes.Count(a => a.MulliganArchetypeId is not null)} (HSReplay) + {archetypes.Count(a => a.MulliganArchetypeId is null && a.Mulligan.Count > 0)} (HSGuru)");
        return Meta(archetypes);
    }

    /// <summary>
    /// HSGuru's analysis rows in the guide's shape. HSGuru names cards by their original id ("CS1_130") where a deck
    /// holds the reprint ("CORE_CS1_130"), so a row goes to the cards of <paramref name="aliasDbfs"/> with the same name
    /// (the ids players actually have), else to HSGuru's id. Kept games stand for the opening hand: win rate when kept =
    /// the archetype's + the kept impact.
    /// </summary>
    public static IReadOnlyList<GuideCard> GuideCardsOf(HsguruAnalysis analysis, CardDb cards, IEnumerable<int> aliasDbfs)
    {
        if (analysis.WinRate is not { } baseline)
        {
            return [];
        }

        var byName = aliasDbfs.Distinct().Select(cards.Get).OfType<Card>()
            .GroupBy(c => c.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(c => c.Id).ToList(), StringComparer.Ordinal);
        var result = new List<GuideCard>();
        foreach (var row in analysis.Cards)
        {
            var card = cards.Get(row.DbfId) ?? cards.Get(row.CardId);
            var keepRate = row.InMulligan > 0 ? (double)row.Kept / row.InMulligan : 0;
            double? winRate = row.KeptImpact is { } impact ? baseline + impact : null;
            IReadOnlyList<string> ids = card is not null && byName.TryGetValue(card.Name, out var same) ? same : [row.CardId];
            result.AddRange(ids.Select(id => new GuideCard(id, cards.Get(id)?.DbfId ?? row.DbfId, card?.Cost ?? 0, row.InMulligan, keepRate, row.Kept, winRate, null)));
        }

        return result;
    }

    /// <summary>
    /// The HSGuru slice. "expansion" and "patch" are not API periods: the API names the current ones (for example
    /// "black_empire", "patch_36.6.3") in every answer, so a cheap slice is read first to learn them.
    /// </summary>
    private static async Task<HsguruMetaSlice?> LoadSliceAsync(HearthpulseData data, string format, string rank, string period, CancellationToken cancellationToken)
    {
        if (period is not ("expansion" or "patch"))
        {
            return await data.LoadHsguruMetaAsync(format, rank, period, cancellationToken).ConfigureAwait(false);
        }

        var probe = await data.LoadHsguruMetaAsync(format, rank, "past_day", cancellationToken).ConfigureAwait(false);
        var resolved = period == "expansion" ? probe?.CurrentPeriod : probe?.CurrentPatchPeriod;
        return resolved is { Length: > 0 } ? await data.LoadHsguruMetaAsync(format, rank, resolved, cancellationToken).ConfigureAwait(false) : null;
    }

    /// <summary>The class HSGuru's lists are written for (the most common real class), else the class the name spells out.</summary>
    internal static string? ClassOf(string archetype, IReadOnlyList<HsguruDeck> lists)
    {
        var fromLists = lists.Select(d => d.Class.Replace(" ", string.Empty).ToUpperInvariant()).Where(Classes.Contains)
            .GroupBy(c => c).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;
        return fromLists ?? ArchetypeNames.ClassOf(archetype);
    }

    /// <summary>The HSReplay archetype for an HSGuru one: same class and the same name once normalised, else the one its list matches.</summary>
    internal static (MetaArchetype Archetype, IReadOnlyList<GuideCard> Cards)? Match(string name, string heroClass,
        IReadOnlyDictionary<int, int>? list, IReadOnlyList<(MetaArchetype Archetype, IReadOnlyList<GuideDeck> Decks, IReadOnlyList<GuideCard> Cards)> hsreplay)
    {
        var sameClass = hsreplay.Where(h => string.Equals(h.Archetype.Class, heroClass, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var h in sameClass)
        {
            if (ArchetypeNames.SameArchetype(name, h.Archetype.Name))
            {
                return (h.Archetype, h.Cards);
            }
        }

        if (list is null || MulliganGuide.MatchArchetype(list, sameClass) is not { } match)
        {
            return null;
        }

        return (match.Archetype, sameClass.First(h => h.Archetype.Id == match.Archetype.Id).Cards);
    }

    /// <summary>Every HSReplay archetype (Standard, Legend) with its lists and mulligan rows, a few requests at a time.</summary>
    internal static async Task<IReadOnlyList<(MetaArchetype Archetype, IReadOnlyList<GuideDeck> Decks, IReadOnlyList<GuideCard> Cards)>> LoadHsreplayAsync(
        HearthpulseData data, CancellationToken cancellationToken, string? playerClass = null)
    {
        var archetypes = (await data.LoadArchetypesAsync("standard", "legend", cancellationToken).ConfigureAwait(false))
            .Where(a => playerClass is null || string.Equals(a.Class, playerClass, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var loaded = await InParallelAsync(archetypes, async a =>
        {
            var lists = await data.LoadArchetypeDecksAsync(a.Id, cancellationToken).ConfigureAwait(false);
            var rows = await data.LoadArchetypeMulliganAsync(a.Id, cancellationToken).ConfigureAwait(false);
            return (Archetype: a, Decks: lists, Cards: rows);
        }, cancellationToken).ConfigureAwait(false);
        return loaded.Where(r => r.Cards.Count > 0).ToList();
    }

    /// <summary>Runs <paramref name="load"/> for every item, at most <see cref="Parallel"/> at a time; results keep the items' order.</summary>
    internal static async Task<T[]> InParallelAsync<TItem, T>(IReadOnlyList<TItem> items, Func<TItem, Task<T>> load, CancellationToken cancellationToken)
    {
        using var gate = new SemaphoreSlim(Parallel);
        return await Task.WhenAll(items.Select(async item =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await load(item).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);
    }

    /// <summary>Card ids in at least <see cref="CoreShare"/> of the lists, weighting each list by its games.</summary>
    internal static IReadOnlyList<string> CoreCards(IReadOnlyList<(int Games, DeckDefinition Deck)> lists, CardDb cards)
    {
        var total = lists.Sum(l => Math.Max(1, l.Games));
        if (total == 0)
        {
            return [];
        }

        return lists
            .SelectMany(l => l.Deck.Cards.Keys.Select(dbf => (Dbf: dbf, Weight: Math.Max(1, l.Games))))
            .GroupBy(c => c.Dbf)
            .Where(g => g.Sum(c => c.Weight) >= CoreShare * total)
            .Select(g => cards.Get(g.Key)?.Id)
            .OfType<string>()
            .ToList();
    }

    /// <summary>The page's mulligan list: cards with enough games, best opening-hand win rate first.</summary>
    internal static IReadOnlyList<MetaMulliganCard> Mulligan(IReadOnlyList<GuideCard> rows) =>
        rows.Where(r => r is { Offered: >= MulliganGuide.MinSample, InOpeningHand: >= MulliganGuide.MinSample, OpeningHandWinRate: not null })
            .DistinctBy(r => r.CardId)
            .OrderByDescending(r => r.OpeningHandWinRate)
            .Select(r => new MetaMulliganCard(r.CardId, r.Offered, r.KeepRate, r.InOpeningHand, r.OpeningHandWinRate!.Value))
            .ToList();

    /// <summary>A positive id that stays the same across loads (FNV-1a of the lower-case name): the page keeps its selection.</summary>
    internal static int StableId(string name)
    {
        var hash = 2166136261u;
        foreach (var ch in name.ToLowerInvariant())
        {
            hash = (hash ^ ch) * 16777619u;
        }

        return (int)(hash & 0x7FFFFFFF);
    }
}
