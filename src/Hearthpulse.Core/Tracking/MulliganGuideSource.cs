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

    private readonly Lock _gate = new();
    private DeckDefinition? _deck;
    private string? _rank;
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

    private string _rankFilter = MulliganGuide.DefaultRank;

    /// <summary>
    /// The mulligan rank filter (<see cref="MulliganGuide.Ranks"/>; the host keeps it in step with the settings). A change
    /// raises <see cref="Changed"/>, so the overlay asks <see cref="For"/> again at once and the guide reloads.
    /// </summary>
    public string Rank
    {
        get
        {
            lock (_gate)
            {
                return _rankFilter;
            }
        }

        set
        {
            lock (_gate)
            {
                if (_rankFilter == value)
                {
                    return;
                }

                _rankFilter = value;
            }

            Changed?.Invoke();
        }
    }

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
            var rank = _rankFilter;
            if (!ReferenceEquals(deck, _deck) || rank != _rank)
            {
                _deck = deck;
                _rank = rank;
                var key = rank + "|" + format + "|" + playerClass + "|" + string.Join(',', deck.Cards.OrderBy(c => c.Key).Select(c => $"{c.Key}x{c.Value}"));
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
            var guide = await LoadGuideAsync(data, format, playerClass, deck.Cards, cards, opponentClass, goingFirst, key.Split('|')[0]).ConfigureAwait(false);
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
    /// <param name="rank">The mulligan rank filter: below or above Legend the guide is HSGuru's slice of that rank (<see cref="LoadRankGuideAsync"/>).</param>
    public static async Task<GuideArchetype?> LoadGuideAsync(HearthpulseData data, string format, string playerClass, IReadOnlyDictionary<int, int> deck, CardDb cards,
        string? opponentClass = null, bool? goingFirst = null, string rank = MulliganGuide.DefaultRank, CancellationToken cancellationToken = default)
    {
        if (MulliganGuide.SourceRank(rank) != MulliganGuide.DefaultRank)
        {
            return await LoadRankGuideAsync(data, format, playerClass, deck, cards, rank, opponentClass, goingFirst, cancellationToken).ConfigureAwait(false);
        }

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

        foreach (var rank in MulliganGuide.SliceRanksFor(guide.Rank))
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

        var wanted = keys.SelectMany(k => MulliganGuide.SliceRanksFor(guide.Rank).Select(rank => (Rank: rank, k.Coin, k.Opponent)))
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

    /// <summary>
    /// The guide at a rank other than Legend (HSReplay's numbers are Legend only, so they are left out): the deck's HSGuru
    /// archetype and its mulligan slice of that rank (<see cref="MulliganGuide.SourceRank"/>), all games. A slice under
    /// <see cref="MulliganGuide.MinSliceGames"/> games gives way to all ranks, and the source line says so. Win rate when
    /// kept = the archetype's win rate at that rank (HSGuru's meta, 7 days; else its Legend analysis) + the kept impact.
    /// </summary>
    public static async Task<GuideArchetype?> LoadRankGuideAsync(HearthpulseData data, string format, string playerClass, IReadOnlyDictionary<int, int> deck, CardDb cards,
        string rank, string? opponentClass = null, bool? goingFirst = null, CancellationToken cancellationToken = default)
    {
        if (await MatchHsguruAsync(data, format, playerClass, deck, cancellationToken).ConfigureAwait(false) is not { } match)
        {
            return null;
        }

        var name = match.Name;
        if (await LoadRankSliceAsync(data, name, format, rank, cancellationToken).ConfigureAwait(false) is not { } found)
        {
            Diag.Info($"Mulligan guide: no HSGuru {name} slice at {MulliganGuide.SourceRank(rank)} or all ranks");
            return null;
        }

        var (used, wanted, baseline) = (found.Slice.Rank, MulliganGuide.SourceRank(rank), found.Baseline);
        var slice = Alias(found.Slice, cards, deck.Keys.Concat(match.Lists.SelectMany(l => l.Cards.Keys)));
        var rows = slice.Cards
            .Select(c => new GuideCard(c.CardId, cards.Get(c.CardId)?.DbfId ?? c.DbfId, cards.Get(c.CardId)?.Cost ?? 0, c.Offered, c.KeepRate ?? 0, c.Kept,
                c.KeptImpact is { } impact ? baseline + impact : null, null))
            .ToList();
        var label = Localization.Loc.T("Core.MulliganRankSource." + MulliganGuide.Ranks.FirstOrDefault(r => r == rank, "all"));
        var source = !found.AllRanks
            ? Localization.Loc.F("Source.MulliganRank", HearthpulseData.Attribution, label)
            : Localization.Loc.F("Source.MulliganRankFallback", HearthpulseData.Attribution, label);
        Diag.Info($"Mulligan guide: HSGuru {name} at {used} (asked {wanted}), {slice.Games} games, {rows.Count} cards");
        var guide = new GuideArchetype(match.Archetype.Id, name, playerClass, rows, [], source)
        {
            BaselineWinRate = baseline,
            HsguruArchetype = name,
            Rank = rank,
            Slices = [slice],
        };
        guide = await WithClassMatchupsAsync(data, guide, format, cancellationToken).ConfigureAwait(false);
        return await WithSlicesAsync(data, guide, format, deck, cards, opponentClass, goingFirst, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// An archetype's HSGuru mulligan slice (all games) at a filter rank other than Legend, with the win rate the cards'
    /// kept impacts add to; the overlay's guide and the meta page both read it, so they show the same numbers. A slice
    /// under <see cref="MulliganGuide.MinSliceGames"/> games gives way to all ranks (<see cref="RankSlice.AllRanks"/>).
    /// The win rate is the archetype's at the slice's rank (HSGuru's meta, 7 days), else its Legend analysis, else 50 %.
    /// Null when neither slice has enough games (offline included).
    /// </summary>
    public static async Task<RankSlice?> LoadRankSliceAsync(HearthpulseData data, string archetype, string format, string rank, CancellationToken cancellationToken = default)
    {
        var wanted = MulliganGuide.SourceRank(rank);
        var used = wanted;
        var slice = await data.LoadHsguruMulliganAsync(archetype, format, wanted, MulliganGuide.AnyCoin, MulliganGuide.AnyOpponent, cancellationToken).ConfigureAwait(false);
        if (slice is not { Games: >= MulliganGuide.MinSliceGames } && wanted != "all")
        {
            used = "all";
            slice = await data.LoadHsguruMulliganAsync(archetype, format, used, MulliganGuide.AnyCoin, MulliganGuide.AnyOpponent, cancellationToken).ConfigureAwait(false);
        }

        if (slice is not { Games: >= MulliganGuide.MinSliceGames })
        {
            return null;
        }

        var baseline = (await data.LoadHsguruMetaAsync(format, used, "past_week", cancellationToken).ConfigureAwait(false))?.Items
                           .FirstOrDefault(i => string.Equals(i.Archetype, archetype, StringComparison.OrdinalIgnoreCase))?.WinRate
                       ?? (await data.LoadHsguruAnalysisAsync(archetype, format, cancellationToken).ConfigureAwait(false))?.WinRate
                       ?? 0.5;
        return new RankSlice(slice with { Rank = used }, baseline, used != wanted);
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

/// <summary>
/// An HSGuru mulligan slice at a filter rank (<see cref="MulliganGuideSource.LoadRankSliceAsync"/>): the slice (its
/// <see cref="MulliganSlice.Rank"/> is the HSGuru rank read), the archetype win rate its kept impacts add to, and whether
/// it fell back to all ranks.
/// </summary>
public sealed record RankSlice(MulliganSlice Slice, double Baseline, bool AllRanks);
