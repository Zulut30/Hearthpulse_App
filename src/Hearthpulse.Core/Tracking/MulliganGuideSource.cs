using Hearthpulse.Core.Api;
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
    public GuideArchetype? For(string playerClass, DeckDefinition deck, string formatType)
    {
        if (formatType != "FT_STANDARD" || playerClass.Length == 0 || deck.Cards.Count == 0)
        {
            return null;
        }

        lock (_gate)
        {
            if (!ReferenceEquals(deck, _deck))
            {
                var key = playerClass + "|" + string.Join(",", deck.Cards.OrderBy(c => c.Key).Select(c => $"{c.Key}x{c.Value}"));
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
            _ = Task.Run(() => LoadAsync(requested!, playerClass, deck));
            return null;
        }
    }

    private async Task LoadAsync(string key, string playerClass, DeckDefinition deck)
    {
        try
        {
            var guide = await LoadGuideAsync(data, playerClass, deck.Cards).ConfigureAwait(false);
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

    /// <summary>Matches the deck to an archetype of its class and loads that archetype's mulligan numbers and matchups.</summary>
    public static async Task<GuideArchetype?> LoadGuideAsync(HearthpulseData data, string playerClass, IReadOnlyDictionary<int, int> deck, CancellationToken cancellationToken = default)
    {
        var archetypes = (await data.LoadArchetypesAsync("standard", "legend", cancellationToken).ConfigureAwait(false))
            .Where(a => string.Equals(a.Class, playerClass, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (archetypes.Count == 0)
        {
            return null;
        }

        var decks = await Task.WhenAll(archetypes.Select(a => data.LoadArchetypeDecksAsync(a.Id, cancellationToken))).ConfigureAwait(false);
        if (MulliganGuide.MatchArchetype(deck, archetypes.Zip(decks, (a, d) => (a, d))) is not { } match)
        {
            Diag.Info($"Mulligan guide: no {playerClass} archetype shares {MulliganGuide.MinDeckOverlap:P0} of this deck");
            return null;
        }

        var archetype = match.Archetype;
        var cardsTask = data.LoadArchetypeMulliganAsync(archetype.Id, cancellationToken);
        var matchupsTask = data.LoadArchetypeMatchupsAsync(archetype.Id, cancellationToken);
        await Task.WhenAll(cardsTask, matchupsTask).ConfigureAwait(false);
        if (cardsTask.Result.Count == 0)
        {
            return null;
        }

        Diag.Info($"Mulligan guide: {archetype.Name} ({match.Overlap:P0} of the deck), {cardsTask.Result.Count} cards");
        return new GuideArchetype(archetype.Id, archetype.Name, archetype.Class, cardsTask.Result, matchupsTask.Result, HearthpulseData.Attribution);
    }
}
