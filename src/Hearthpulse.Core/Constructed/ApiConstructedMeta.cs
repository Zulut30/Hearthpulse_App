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
/// Builds <see cref="ConstructedMeta"/> from the Hearthpulse API. The tier list is HSGuru's meta for the chosen format,
/// rank and period; decklists and core cards come from HSGuru's archetype lists (current expansion, all ranks); the
/// mulligan numbers are HSReplay's (Standard, Legend, EU) for the HSReplay archetype each HSGuru archetype matches:
/// by name and class first, else by its most played list's cards. Archetypes with no match have no mulligan data.
/// </summary>
public static class ApiConstructedMeta
{
    public const string Attribution = "HearthPulse · HSGuru / HSReplay данные";

    /// <summary>A card in at least this share of an archetype's lists (by games) is one of its core cards.</summary>
    private const double CoreShare = 0.6;

    /// <summary>Parallel requests while loading every HSReplay archetype (the server is small).</summary>
    private const int Parallel = 6;

    /// <param name="locale">The card language; "ruRU" shows the Russian archetype labels.</param>
    public static async Task<ConstructedMeta?> LoadAsync(HearthpulseData data, CardDb cards, string format, string rank, string period, string? locale, CancellationToken cancellationToken = default)
    {
        var slice = await LoadSliceAsync(data, format, rank, period, cancellationToken).ConfigureAwait(false);
        if (slice is null || slice.Items.Count == 0)
        {
            return null;
        }

        var catalog = (await data.LoadHsguruArchetypesAsync(format, cancellationToken).ConfigureAwait(false))
            .GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var hsreplay = format == "standard" ? await LoadHsreplayAsync(data, cancellationToken).ConfigureAwait(false) : [];

        var russian = string.Equals(locale, "ruRU", StringComparison.OrdinalIgnoreCase);
        var labels = russian
            ? ArchetypeNames.Distinct(slice.Items.Select(i => i.Archetype).Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(n => n, ArchetypeNames.RussianLabel, StringComparer.OrdinalIgnoreCase))
            : null;

        var archetypes = new List<MetaArchetype>();
        var decks = new List<MetaDeck>();
        var mapped = 0;
        foreach (var item in slice.Items)
        {
            var lists = catalog.GetValueOrDefault(item.Archetype)?.Decks ?? [];
            var heroClass = ClassOf(item.Archetype, lists);
            if (heroClass is null)
            {
                continue;
            }

            var id = StableId(item.Archetype);
            var name = labels?.GetValueOrDefault(item.Archetype) ?? item.Archetype;
            var decoded = lists.Select(d => (Deck: d, Cards: DeckString.TryDecode(d.Code))).Where(d => d.Cards is not null).ToList();
            var match = Match(item.Archetype, heroClass, decoded.OrderByDescending(d => d.Deck.Games).FirstOrDefault().Cards?.Cards, hsreplay);
            mapped += match is null ? 0 : 1;
            archetypes.Add(new MetaArchetype(
                id,
                item.Archetype,
                name,
                heroClass,
                item.Games,
                (int)Math.Round(item.Games * item.WinRate),
                item.WinRate,
                item.Popularity,
                0, 0, 0, 0,
                CoreCards(decoded.Select(d => (d.Deck.Games, d.Cards!)).ToList(), cards),
                match is null ? [] : Mulligan(match.Value.Cards))
            {
                MulliganBaseline = match is null ? null : MulliganGuide.Baseline(match.Value.Cards),
                MulliganArchetypeId = match?.Archetype.Id,
            });

            decks.AddRange(decoded.Select(d => new MetaDeck(d.Deck.Code, id, name, heroClass, d.Deck.Games, (int)Math.Round(d.Deck.Games * d.Deck.WinRate), d.Deck.WinRate, d.Deck.Updated)));
        }

        Diag.Info($"Constructed meta {format}/{rank}/{period}: {archetypes.Count} archetypes, {decks.Count} lists, HSReplay mulligan for {mapped}");
        var ordered = archetypes.OrderByDescending(a => a.Games).ToList();
        return new ConstructedMeta(format, rank, period, slice.FetchedAt, ordered.Sum(a => a.Games), ordered, decks.OrderByDescending(d => d.Games).ToList())
        {
            Source = Attribution,
        };
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

    /// <summary>The class HSGuru's lists are written for (the most common one), else the class the name spells out.</summary>
    internal static string? ClassOf(string archetype, IReadOnlyList<HsguruDeck> lists)
    {
        var fromLists = lists.Where(d => d.Class.Length > 0).GroupBy(d => d.Class.Replace(" ", string.Empty).ToUpperInvariant())
            .OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;
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
    private static async Task<IReadOnlyList<(MetaArchetype, IReadOnlyList<GuideDeck>, IReadOnlyList<GuideCard>)>> LoadHsreplayAsync(HearthpulseData data, CancellationToken cancellationToken)
    {
        var archetypes = await data.LoadArchetypesAsync("standard", "legend", cancellationToken).ConfigureAwait(false);
        using var gate = new SemaphoreSlim(Parallel);
        var loads = archetypes.Select(async a =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var lists = await data.LoadArchetypeDecksAsync(a.Id, cancellationToken).ConfigureAwait(false);
                var rows = await data.LoadArchetypeMulliganAsync(a.Id, cancellationToken).ConfigureAwait(false);
                return (a, lists, rows);
            }
            finally
            {
                gate.Release();
            }
        });
        return (await Task.WhenAll(loads).ConfigureAwait(false)).Where(r => r.rows.Count > 0).ToList();
    }

    /// <summary>Card ids in at least <see cref="CoreShare"/> of the lists, weighting each list by its games.</summary>
    private static IReadOnlyList<string> CoreCards(IReadOnlyList<(int Games, DeckDefinition Deck)> lists, CardDb cards)
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
    private static IReadOnlyList<MetaMulliganCard> Mulligan(IReadOnlyList<GuideCard> rows) =>
        rows.Where(r => r is { Offered: >= MulliganGuide.MinSample, InOpeningHand: >= MulliganGuide.MinSample, OpeningHandWinRate: not null })
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
