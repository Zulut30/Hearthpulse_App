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

    /// <summary>Raised on a worker thread when a lookup finished with a result.</summary>
    public event Action? Changed;

    /// <summary>
    /// The guide for this deck if it is ready; otherwise starts loading it and returns null. Cheap to call on every
    /// update: the deck's key is computed once per deck.
    /// </summary>
    public GuideArchetype? For(string playerClass, DeckDefinition deck, string formatType, CardDb cards)
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
                }
            }

            if (_result is not null || DateTime.UtcNow < _retryAt)
            {
                return _result;
            }

            _retryAt = DateTime.UtcNow + RetryAfter;
            var requested = _key;
            Task.Run(() => LoadAsync(requested!, format, playerClass, deck, cards)).Forget("Mulligan guide lookup");
            return null;
        }
    }

    private async Task LoadAsync(string key, string format, string playerClass, DeckDefinition deck, CardDb cards)
    {
        try
        {
            var guide = await LoadGuideAsync(data, format, playerClass, deck.Cards, cards).ConfigureAwait(false);
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
    public static async Task<GuideArchetype?> LoadGuideAsync(HearthpulseData data, string format, string playerClass, IReadOnlyDictionary<int, int> deck, CardDb cards, CancellationToken cancellationToken = default)
    {
        if (format == "standard" && await LoadHsreplayGuideAsync(data, playerClass, deck, cancellationToken).ConfigureAwait(false) is { } guide)
        {
            return guide;
        }

        return await LoadHsguruGuideAsync(data, format, playerClass, deck, cards, cancellationToken).ConfigureAwait(false);
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
        return new GuideArchetype(archetype.Id, archetype.Name, archetype.Class, rows, matchups, HearthpulseData.Attribution + ", Legend EU");
    }

    /// <summary>
    /// Matches the deck to an HSGuru archetype of its class by its lists (current expansion, all ranks) and loads that
    /// archetype's HSGuru analysis: per-card numbers (Legend, past week) and results against each class.
    /// </summary>
    public static async Task<GuideArchetype?> LoadHsguruGuideAsync(HearthpulseData data, string format, string playerClass, IReadOnlyDictionary<int, int> deck, CardDb cards, CancellationToken cancellationToken = default)
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

        var name = match.Archetype.Key;
        if (await data.LoadHsguruAnalysisAsync(name, format, cancellationToken).ConfigureAwait(false) is not { WinRate: { } baseline } analysis)
        {
            return null;
        }

        var lists = candidates.First(c => c.Archetype.Id == match.Archetype.Id).Decks;
        var rows = ApiConstructedMeta.GuideCardsOf(analysis, cards, deck.Keys.Concat(lists.SelectMany(l => l.Cards.Keys)));
        Diag.Info($"Mulligan guide: HSGuru {name} (match score {match.Score:0.00}), {rows.Count} cards");
        return new GuideArchetype(match.Archetype.Id, name, playerClass, rows, analysis.ClassMatchups, ApiConstructedMeta.HsguruMulliganSource)
        {
            BaselineWinRate = baseline,
        };
    }
}
