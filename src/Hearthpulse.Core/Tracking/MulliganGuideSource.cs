using Hearthpulse.Core.Api;
using Hearthpulse.Core.Constructed;
using Hearthpulse.Core.Cards;

namespace Hearthpulse.Core.Tracking;

/// <summary>
/// Finds the API archetype for the deck being played and loads its mulligan numbers in the background, so the
/// tracker thread never waits on the network. Only Standard: the API has Standard Legend (EU) data only.
/// </summary>
public sealed class MulliganGuideSource(HearthpulseData data)
{
    /// <summary>A failed or unmatched lookup is tried again after this long (the deck may still be in the cache then).</summary>
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(5);

    private readonly object _gate = new();
    private DeckDefinition? _deck;
    private string? _key;
    private GuideArchetype? _result;
    private DateTime _retryAt;

    /// <summary>
    /// Coin / opponent contexts whose slices were asked for (for the current deck), with the time they may be asked
    /// again: a lookup that brought nothing new (offline, a timeout) is tried again after <see cref="RetryAfter"/>, like
    /// the guide itself; answers and 404s come from the disk cache then.
    /// </summary>
    private readonly Dictionary<string, DateTime> _contexts = new(StringComparer.Ordinal);

    /// <summary>Raised on a worker thread when a lookup finished with a result.</summary>
    public event Action? Changed;

    /// <summary>
    /// The guide for this deck if it is ready; otherwise starts loading it and returns null. Cheap to call on every
    /// update: the deck's key is computed once per deck. Once the coin or the opponent's class is known, their HSGuru
    /// slices load in the background too (<see cref="Changed"/> again when they land).
    /// </summary>
    public GuideArchetype? For(string playerClass, DeckDefinition deck, string formatType, CardDb cards, string? opponentClass = null, bool? goingFirst = null)
    {
        var format = formatType switch { "FT_STANDARD" => "standard", "FT_WILD" => "wild", _ => null };
        if (format is null || playerClass.Length == 0 || deck.Cards.Count == 0)
        {
            return null;
        }

        lock (_gate)
        {
            if (!ReferenceEquals(deck, _deck))
            {
                var key = format + "|" + playerClass + "|" + string.Join(",", deck.Cards.OrderBy(c => c.Key).Select(c => $"{c.Key}x{c.Value}"));
                _deck = deck;
                if (key != _key)
                {
                    _key = key;
                    _result = null;
                    _retryAt = DateTime.MinValue;
                    _contexts.Clear();
                }
            }

            var context = $"{goingFirst}|{opponentClass}";
            if (_result is { HsguruArchetype: not null } ready && (goingFirst is not null || opponentClass is { Length: > 0 })
                && DateTime.UtcNow >= _contexts.GetValueOrDefault(context, DateTime.MinValue))
            {
                _contexts[context] = DateTime.UtcNow + RetryAfter;
                var requested = _key!;
                Task.Run(() => LoadSlicesAsync(requested, ready, format, deck, cards, opponentClass, goingFirst)).Forget("Mulligan guide slices");
            }

            if (_result is not null || DateTime.UtcNow < _retryAt)
            {
                return _result;
            }

            _retryAt = DateTime.UtcNow + RetryAfter;
            _contexts[context] = DateTime.UtcNow + RetryAfter;
            var lookupKey = _key;
            Task.Run(() => LoadAsync(lookupKey!, format, playerClass, deck, cards, opponentClass, goingFirst)).Forget("Mulligan guide lookup");
            return null;
        }
    }

    private async Task LoadSlicesAsync(string key, GuideArchetype guide, string format, DeckDefinition deck, CardDb cards, string? opponentClass, bool? goingFirst)
    {
        try
        {
            var withSlices = await WithSlicesAsync(data, guide, format, deck.Cards, cards, opponentClass, goingFirst).ConfigureAwait(false);
            if (withSlices.Slices.Count == guide.Slices.Count)
            {
                return;
            }

            lock (_gate)
            {
                if (key != _key || _result is null)
                {
                    return;
                }

                // Another context may have landed meanwhile: add only what is new to the current guide.
                var added = withSlices.Slices.Where(s => !_result.Slices.Any(o => o.Rank == s.Rank && o.Coin == s.Coin && o.OpponentClass == s.OpponentClass)).ToList();
                _result = _result with { Slices = [.. _result.Slices, .. added] };
            }

            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Diag.Error("Mulligan guide slices not loaded", ex);
        }
    }

    private async Task LoadAsync(string key, string format, string playerClass, DeckDefinition deck, CardDb cards, string? opponentClass, bool? goingFirst)
    {
        try
        {
            var guide = await LoadGuideAsync(data, format, playerClass, deck.Cards, cards, opponentClass, goingFirst).ConfigureAwait(false);
            if (guide is null)
            {
                return;
            }

            lock (_gate)
            {
                if (key != _key)
                {
                    return;
                }

                _result = guide;
            }

            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Diag.Error("Mulligan guide not loaded", ex);
        }
    }

    /// <summary>
    /// The guide for a deck: HSReplay's numbers for the HSReplay archetype it matches (Standard only), else HSGuru's for
    /// the HSGuru archetype whose lists it matches (Standard and Wild). Null when it matches neither.
    /// </summary>
    /// <param name="opponentClass">The opponent's class when known: its HSGuru mulligan slice is loaded too.</param>
    /// <param name="goingFirst">True going first, false on the coin, null when unknown: that slice is loaded too.</param>
    public static async Task<GuideArchetype?> LoadGuideAsync(HearthpulseData data, string format, string playerClass, IReadOnlyDictionary<int, int> deck, CardDb cards,
        string? opponentClass = null, bool? goingFirst = null, CancellationToken cancellationToken = default)
    {
        var guide = format == "standard" ? await LoadHsreplayGuideAsync(data, playerClass, deck, cancellationToken).ConfigureAwait(false) : null;
        if (guide is null)
        {
            guide = await LoadHsguruGuideAsync(data, format, playerClass, deck, cards, cancellationToken).ConfigureAwait(false);
        }
        else if (await MatchHsguruAsync(data, format, playerClass, deck, cancellationToken).ConfigureAwait(false) is { } hsguru)
        {
            // HSReplay has no coin or class split: HSGuru's slices of the same deck add it.
            guide = guide with { HsguruArchetype = hsguru.Name };
        }

        if (guide is null)
        {
            return null;
        }

        guide = await WithClassMatchupsAsync(data, guide, format, cancellationToken).ConfigureAwait(false);
        return await WithSlicesAsync(data, guide, format, deck, cards, opponentClass, goingFirst, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The guide with HSGuru's class results as its matchups when it has none (an HSReplay archetype the server holds no
    /// matchups for): they move the baseline against the opponent's class. Legend first, else all ranks; a rank whose
    /// results do not reach <see cref="MulliganGuide.MinClassGames"/> for any class is skipped.
    /// </summary>
    public static async Task<GuideArchetype> WithClassMatchupsAsync(HearthpulseData data, GuideArchetype guide, string format, CancellationToken cancellationToken = default)
    {
        if (guide.Matchups.Count > 0 || guide.HsguruArchetype is not { } archetype)
        {
            return guide;
        }

        foreach (var rank in MulliganGuide.SliceRanks)
        {
            var matchups = await data.LoadHsguruClassMatchupsAsync(archetype, format, rank, cancellationToken).ConfigureAwait(false);
            if (matchups.Any(m => m.Games >= MulliganGuide.MinClassGames))
            {
                Diag.Info($"Mulligan guide: no HSReplay matchups for {guide.Name}, HSGuru {archetype} {rank} class results ({matchups.Sum(m => m.Games)} games)");
                return guide with { Matchups = matchups };
            }
        }

        return guide;
    }

    /// <summary>
    /// The guide with the HSGuru mulligan slices for this game added (all games, the coin, the opponent's class; Legend
    /// and all ranks). Slices already in the guide are not asked for again; a slice the server lacks is simply missing.
    /// </summary>
    public static async Task<GuideArchetype> WithSlicesAsync(HearthpulseData data, GuideArchetype guide, string format, IReadOnlyDictionary<int, int> deck, CardDb cards,
        string? opponentClass, bool? goingFirst, CancellationToken cancellationToken = default)
    {
        if (guide.HsguruArchetype is not { } archetype)
        {
            return guide;
        }

        var keys = new List<(string Coin, string Opponent)> { (MulliganGuide.AnyCoin, MulliganGuide.AnyOpponent) };
        if (goingFirst is { } first)
        {
            keys.Add((first ? "going_first" : "on_coin", MulliganGuide.AnyOpponent));
        }

        if (opponentClass is { Length: > 0 })
        {
            keys.Add((MulliganGuide.AnyCoin, opponentClass.ToUpperInvariant()));
        }

        var wanted = keys.SelectMany(k => MulliganGuide.SliceRanks.Select(rank => (Rank: rank, k.Coin, k.Opponent)))
            .Where(k => !guide.Slices.Any(s => s.Rank == k.Rank && s.Coin == k.Coin && string.Equals(s.OpponentClass, k.Opponent, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (wanted.Count == 0)
        {
            return guide;
        }

        var loaded = await Task.WhenAll(wanted.Select(k => data.LoadHsguruMulliganAsync(archetype, format, k.Rank, k.Coin, k.Opponent, cancellationToken))).ConfigureAwait(false);
        var added = loaded.OfType<MulliganSlice>().Select(s => Alias(s, cards, deck.Keys)).ToList();
        if (added.Count > 0)
        {
            Diag.Info($"Mulligan guide: HSGuru {archetype} slices {string.Join(", ", added.Select(s => $"{s.Rank}/{s.Coin}/{s.OpponentClass} ({s.Games} games)"))}");
        }

        return guide with { Slices = [.. guide.Slices, .. added] };
    }

    /// <summary>
    /// HSGuru names some cards by their original id ("CS1_130") where the deck holds the reprint ("CORE_CS1_130"): a row
    /// goes to the deck's cards of the same name, like <see cref="ApiConstructedMeta.GuideCardsOf"/>.
    /// </summary>
    private static MulliganSlice Alias(MulliganSlice slice, CardDb cards, IEnumerable<int> deckDbfs)
    {
        var byName = deckDbfs.Distinct().Select(cards.Get).OfType<Card>()
            .GroupBy(c => c.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(c => c.Id).ToList(), StringComparer.Ordinal);
        if (byName.Count == 0)
        {
            return slice;
        }

        var rows = new List<SliceCard>();
        foreach (var row in slice.Cards)
        {
            var card = cards.Get(row.DbfId) ?? cards.Get(row.CardId);
            IReadOnlyList<string> ids = card is not null && byName.TryGetValue(card.Name, out var same) ? same : [row.CardId];
            rows.AddRange(ids.Select(id => row with { CardId = id }));
        }

        // HSGuru may list both the original and the reprint: the deck's id keeps the row with more games.
        return slice with { Cards = rows.GroupBy(r => r.CardId, StringComparer.Ordinal).Select(g => g.MaxBy(r => r.Offered)!).ToList() };
    }

    /// <summary>The HSGuru archetype (current expansion, all ranks) whose lists the deck matches, or null.</summary>
    private static async Task<(MetaArchetype Archetype, IReadOnlyList<GuideDeck> Lists, double Score, string Name)?> MatchHsguruAsync(HearthpulseData data, string format, string playerClass,
        IReadOnlyDictionary<int, int> deck, CancellationToken cancellationToken)
    {
        var candidates = (await data.LoadHsguruArchetypesAsync(format, cancellationToken).ConfigureAwait(false))
            .Where(a => string.Equals(ApiConstructedMeta.ClassOf(a.Name, a.Decks), playerClass, StringComparison.OrdinalIgnoreCase))
            .Select(a => (Archetype: a, Lists: a.Decks.Select(d => DeckString.TryDecode(d.Code)).OfType<DeckDefinition>().ToList()))
            .Where(a => a.Lists.Count > 0)
            .Select(a => (Archetype: new MetaArchetype(ApiConstructedMeta.StableId(a.Archetype.Name), a.Archetype.Name, a.Archetype.Name, playerClass,
                    a.Archetype.Games, 0, a.Archetype.WinRate, 0, 0, 0, 0, 0, [], []),
                Decks: (IReadOnlyList<GuideDeck>)a.Lists.Select(l => new GuideDeck(0, l.Cards)).ToList(),
                Cards: (IReadOnlyList<GuideCard>)[]))
            .ToList();
        if (MulliganGuide.MatchArchetype(deck, candidates) is not { } match)
        {
            Diag.Info($"Mulligan guide: no HSGuru {format} {playerClass} archetype has a list sharing {MulliganGuide.MinDeckOverlap:P0} of this deck");
            return null;
        }

        return (match.Archetype, candidates.First(c => c.Archetype.Id == match.Archetype.Id).Decks, match.Score, match.Archetype.Key);
    }

    /// <summary>Matches the deck to an HSReplay archetype of its class and loads that archetype's mulligan numbers and matchups.</summary>
    public static async Task<GuideArchetype?> LoadHsreplayGuideAsync(HearthpulseData data, string playerClass, IReadOnlyDictionary<int, int> deck, CancellationToken cancellationToken = default)
    {
        var candidates = await ApiConstructedMeta.LoadHsreplayAsync(data, cancellationToken, playerClass).ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            return null;
        }

        if (MulliganGuide.MatchArchetype(deck, candidates) is not { } match)
        {
            var closest = candidates.MaxBy(c => Math.Max(MulliganGuide.Overlap(deck, c.Decks), MulliganGuide.Presence(deck, c.Cards)));
            Diag.Info($"Mulligan guide: no HSReplay {playerClass} archetype matches this deck (closest: {closest.Archetype.Name}, "
                      + $"{MulliganGuide.Overlap(deck, closest.Decks):P0} of a list, presence {MulliganGuide.Presence(deck, closest.Cards):P0})");
            return null;
        }

        var archetype = match.Archetype;
        var rows = candidates.First(c => c.Archetype.Id == archetype.Id).Cards;
        var matchups = await data.LoadArchetypeMatchupsAsync(archetype.Id, cancellationToken).ConfigureAwait(false);
        Diag.Info($"Mulligan guide: {archetype.Name} (match score {match.Score:0.00}), {rows.Count} cards");
        return new GuideArchetype(archetype.Id, archetype.Name, archetype.Class, rows, matchups, Localization.Loc.F("Source.MulliganLegendEu", HearthpulseData.Attribution));
    }

    /// <summary>
    /// Matches the deck to an HSGuru archetype of its class by its lists (current expansion, all ranks) and loads that
    /// archetype's HSGuru analysis: per-card numbers (Legend, past week) and results against each class.
    /// </summary>
    public static async Task<GuideArchetype?> LoadHsguruGuideAsync(HearthpulseData data, string format, string playerClass, IReadOnlyDictionary<int, int> deck, CardDb cards, CancellationToken cancellationToken = default)
    {
        if (await MatchHsguruAsync(data, format, playerClass, deck, cancellationToken).ConfigureAwait(false) is not { } match)
        {
            return null;
        }

        var name = match.Name;
        if (await data.LoadHsguruAnalysisAsync(name, format, cancellationToken).ConfigureAwait(false) is not { WinRate: { } baseline } analysis)
        {
            return null;
        }

        var rows = ApiConstructedMeta.GuideCardsOf(analysis, cards, deck.Keys.Concat(match.Lists.SelectMany(l => l.Cards.Keys)));
        Diag.Info($"Mulligan guide: HSGuru {name} (match score {match.Score:0.00}), {rows.Count} cards");
        return new GuideArchetype(match.Archetype.Id, name, playerClass, rows, analysis.ClassMatchups, ApiConstructedMeta.HsguruMulliganSource)
        {
            BaselineWinRate = baseline,
            HsguruArchetype = name,
        };
    }
}
