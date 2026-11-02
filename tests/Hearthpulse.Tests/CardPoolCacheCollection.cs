namespace Hearthpulse.Tests;

/// <summary>
/// Tests that delete and write the card pool's cache files in the run's data folder (<c>cache/api/card-pool.json</c>,
/// <c>cache/cards.enUS.collectible.json</c>): one at a time, so one never reads the other's fresh cache.
/// </summary>
[CollectionDefinition(nameof(CardPoolCacheCollection))]
public sealed class CardPoolCacheCollection;
