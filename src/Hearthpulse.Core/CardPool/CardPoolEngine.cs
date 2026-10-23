using System.Collections.Concurrent;
using Hearthpulse.Core.Cards;

namespace Hearthpulse.Core.CardPool;

/// <summary>An entry of the exception table (<see cref="CardPoolTables.Overrides"/>).</summary>
/// <param name="Skip">Not shown, with the reason (a pool the catalog cannot answer: "Cost equal to your hand size").</param>
/// <param name="Text">English text to parse instead of the card's own.</param>
public sealed record PoolOverride(string? Skip = null, string? Text = null);

/// <summary>
/// Pools for generator cards: reads the generator's text (exception table first), filters the catalog for the game's
/// format and classes, and builds the panel view. Thread-safe; parsed texts are cached.
/// </summary>
public sealed class CardPoolEngine
{
    private readonly ConcurrentDictionary<string, PoolParse> _parsed = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _logged = new(StringComparer.Ordinal);

    // Views are rebuilt on every overlay update while a pool is up; the same pool must come back as the same object
    // (cheap, and the UI keeps its list and scroll position).
    private readonly ConcurrentDictionary<(string CardId, PoolContext Context, PoolTrigger Trigger), CardPoolView> _views = new();
    private readonly IReadOnlyDictionary<string, int> _metaGames;
    private readonly CardDb _cards;

    /// <param name="metaGames">Games of meta decks running each card id (see <see cref="MetaGames"/>); may be empty.</param>
    public CardPoolEngine(CardPoolCatalog catalog, CardDb cards, IReadOnlyDictionary<string, int>? metaGames = null)
    {
        Catalog = catalog;
        _cards = cards;
        _metaGames = metaGames ?? new Dictionary<string, int>();
    }

    public CardPoolCatalog Catalog { get; }

    /// <summary>The generator text of a card: exception table, then the catalog's English text, then the generated table.</summary>
    public PoolParse Parse(string cardId) => _parsed.GetOrAdd(cardId, id =>
    {
        if (CardPoolTables.Overrides.TryGetValue(id, out var o))
        {
            if (o.Skip is not null)
            {
                return new PoolParse(PoolParseStatus.Uncomputed, Detail: o.Skip);
            }

            if (o.Text is not null)
            {
                return PoolTextParser.Parse(o.Text);
            }
        }

        return PoolTextParser.Parse(TextOf(id));
    });

    /// <summary>The pool cards of a generator, or null when the card has no pool (see <see cref="Parse"/>).</summary>
    public IReadOnlyList<PoolCard>? Pool(string cardId, PoolContext context)
    {
        if (!context.IsSupported || Catalog.Count == 0 || Parse(cardId) is not { Status: PoolParseStatus.Pool, Generator: { } generator })
        {
            return null;
        }

        var generatorClass = Catalog.Get(cardId)?.Classes.FirstOrDefault(c => c != "NEUTRAL") ?? _cards.Get(cardId)?.CardClass;
        return Catalog.Cards.Where(c => generator.Query.Matches(c, context, generator.Verb, generatorClass)).ToList();
    }

    /// <summary>The panel view for a generator; null (and one log line per card) when there is no pool to show.</summary>
    public CardPoolView? Build(string cardId, PoolContext context, PoolTrigger trigger)
    {
        if (!context.IsSupported)
        {
            return null;
        }

        if (_views.TryGetValue((cardId, context, trigger), out var known))
        {
            return known;
        }

        // Another trigger of the same pool reuses its lists; the view itself is cached per trigger, so it stays the
        // same object on every overlay update.
        var same = _views.FirstOrDefault(v => v.Key.CardId == cardId && v.Key.Context == context).Value;
        var built = same is not null ? same with { Trigger = trigger } : BuildCore(cardId, context, trigger);
        if (built is not null)
        {
            _views[(cardId, context, trigger)] = built;
        }

        return built;
    }

    private CardPoolView? BuildCore(string cardId, PoolContext context, PoolTrigger trigger)
    {

        var parse = Parse(cardId);
        if (parse.Status is PoolParseStatus.Unrecognized or PoolParseStatus.NotPool or PoolParseStatus.Uncomputed && _logged.TryAdd(cardId, 0))
        {
            var what = parse.Status switch
            {
                PoolParseStatus.Unrecognized => "not understood",
                PoolParseStatus.Uncomputed => "pool not computed (panel says so)",
                _ => "has no catalog pool",
            };
            Diag.Info($"Card pool: {NameOf(cardId)} ({cardId}) {what}: {parse.Detail} | {TextOf(cardId)}");
        }

        if (parse.Status == PoolParseStatus.Uncomputed)
        {
            return Uncomputed(cardId, trigger, parse.Detail ?? "the catalog cannot answer this pool");
        }

        if (parse.Generator is not { } generator || Pool(cardId, context) is not { Count: > 0 } pool)
        {
            if (parse.Generator is not null && _logged.TryAdd(cardId, 0))
            {
                Diag.Info($"Card pool: {NameOf(cardId)} ({cardId}) pool panel hidden: no catalog card matches \"{parse.Generator.Phrase}\" "
                          + $"({context.Format}, {Catalog.Count} cards in the catalog from {Catalog.Source})");
            }

            return null;
        }

        var (cost, attack, health) = PoolStats.Histograms(pool);
        var entries = pool
            .Select(c => new PoolEntry(c.Id, NameOf(c.Id, c), c.Cost, c.Attack, c.Type is "MINION" or "WEAPON" ? c.Health : null, c.Type, c.Rarity,
                c.Classes.Count == 1 ? c.Classes[0] : "MULTI", _metaGames.GetValueOrDefault(c.Id))
            {
                Mechanics = PoolKeywords.Shown.Where(m => PoolStats.Has(c, m.Tag)).Select(m => m.Tag).ToList(),
            })
            .OrderBy(e => e.Cost).ThenBy(e => e.Name, StringComparer.CurrentCulture)
            .ToList();
        var featured = entries.Where(e => e.MetaGames > 0).MaxBy(e => e.MetaGames);
        return new CardPoolView(
            cardId,
            NameOf(cardId),
            trigger,
            generator.Phrase,
            Describe(generator, context),
            pool.Count,
            cost,
            attack,
            health,
            PoolStats.Mechanics(pool),
            featured,
            featured is null ? null : "Most played in meta decks",
            entries)
        {
            Filters = PoolStats.Mechanics(pool, int.MaxValue),
        };
    }

    /// <summary>The "Pool not computed" view of a generator from the exception table: no cards, only the reason.</summary>
    private CardPoolView Uncomputed(string cardId, PoolTrigger trigger, string reason) =>
        new(cardId, NameOf(cardId), trigger, string.Empty, reason, 0, PoolStats.Histogram("Cost", [], 0, 7), null, null, [], null, null, [])
        {
            Uncomputed = reason,
        };

    /// <summary>Games per card id over a meta's decks (a deck counts once per card it runs).</summary>
    public static IReadOnlyDictionary<string, int> MetaGames(Constructed.ConstructedMeta? meta, CardDb cards)
    {
        var games = new Dictionary<string, int>(StringComparer.Ordinal);
        if (meta is null)
        {
            return games;
        }

        foreach (var deck in meta.Decks)
        {
            if (DeckString.TryDecode(deck.DeckString, deck.Archetype) is not { } definition)
            {
                continue;
            }

            foreach (var dbfId in definition.Cards.Keys)
            {
                if (cards.Get(dbfId)?.Id is { } id)
                {
                    games[id] = games.GetValueOrDefault(id) + deck.Games;
                }
            }
        }

        return games;
    }

    private string TextOf(string cardId) =>
        Catalog.Get(cardId)?.Text is { Length: > 0 } text ? text
        : CardPoolTables.GeneratorTexts.TryGetValue(cardId, out var generated) ? generated
        : _cards.Locale == "enUS" ? PoolText.Normalize(_cards.Get(cardId)?.Text)
        : string.Empty;

    private string NameOf(string cardId, PoolCard? card = null) =>
        _cards.Get(cardId)?.Name is { Length: > 0 } local ? local
        : (card ?? Catalog.Get(cardId)) is { } c ? (_cards.Locale == "ruRU" && c.NameRu is { Length: > 0 } ru ? ru : c.Name)
        : cardId;

    /// <summary>"Battlecry minions · your class + Neutral · Standard".</summary>
    internal static string Describe(PoolGenerator generator, PoolContext context)
    {
        var q = generator.Query;
        var classes = q.Scope switch
        {
            ClassScope.Any => "any class",
            ClassScope.Named => string.Join(" / ", q.Classes.Select(Title)),
            ClassScope.Own => Title(context.OwnClass ?? "your class"),
            ClassScope.Other => "other classes",
            ClassScope.Opponent => Title(context.OpponentClass ?? "opponent's class"),
            _ => generator.Verb == PoolVerb.Discover ? $"{Title(context.OwnClass ?? "your class")} + Neutral" : "any class",
        };
        var format = q.Past ? "from the past" : context.IsStandard ? "Standard" : "Wild";
        return string.Join(" · ", new[] { classes, format }.Where(s => s is not null));
    }

    private static string Title(string cardClass) => cardClass switch
    {
        "DEATHKNIGHT" => "Death Knight",
        "DEMONHUNTER" => "Demon Hunter",
        { Length: > 1 } c when c.All(char.IsUpper) => c[0] + c[1..].ToLowerInvariant(),
        var c => c,
    };
}
