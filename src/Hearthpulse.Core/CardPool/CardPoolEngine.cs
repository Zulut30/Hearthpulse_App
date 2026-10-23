using System.Collections.Concurrent;
using Hearthpulse.Core.Cards;

namespace Hearthpulse.Core.CardPool;

/// <summary>An entry of the exception table (<see cref="CardPoolTables.Overrides"/>).</summary>
/// <param name="Skip">Not shown, with the reason (a pool the catalog cannot answer: "Cost equal to your hand size").</param>
/// <param name="Text">English text to parse instead of the card's own.</param>
public sealed record PoolOverride(string? Skip = null, string? Text = null);

/// <summary>
/// Pools for generator cards. Sources in order: the generator's Hearthstone Wiki pools (Standard, served by the
/// Hearthpulse API), the card text read by <see cref="PoolTextParser"/> (exception table first) over the catalog, and a
/// "Pool not computed" view with the reason. Builds the panel view; thread-safe, parsed texts are cached.
/// </summary>
public sealed class CardPoolEngine
{
    /// <summary><see cref="CardPoolView.Source"/> of a pool read from the card text.</summary>
    public const string TextSource = "card text";

    private readonly ConcurrentDictionary<string, PoolParse> _parsed = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _logged = new(StringComparer.Ordinal);

    // Views are rebuilt on every overlay update while a pool is up; the same pool must come back as the same object
    // (cheap, and the UI keeps its list and scroll position). A wiki answer that arrives later makes a new key.
    private readonly ConcurrentDictionary<(string CardId, PoolContext Context, PoolTrigger Trigger, bool Wiki), CardPoolView> _views = new();
    private readonly IReadOnlyDictionary<string, int> _metaGames;
    private readonly CardDb _cards;
    private readonly WikiPoolSource? _wiki;

    /// <param name="metaGames">Games of meta decks running each card id (see <see cref="MetaGames"/>); may be empty.</param>
    /// <param name="wiki">Hearthstone Wiki pools; without it only the card text is read.</param>
    public CardPoolEngine(CardPoolCatalog catalog, CardDb cards, IReadOnlyDictionary<string, int>? metaGames = null, WikiPoolSource? wiki = null)
    {
        Catalog = catalog;
        _cards = cards;
        _metaGames = metaGames ?? new Dictionary<string, int>();
        _wiki = wiki;
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

    /// <summary>The pool cards the card text describes, or null when the text has no pool (see <see cref="Parse"/>).</summary>
    public IReadOnlyList<PoolCard>? Pool(string cardId, PoolContext context)
    {
        if (!context.IsSupported || Catalog.Count == 0 || Parse(cardId) is not { Status: PoolParseStatus.Pool, Generator: { } generator })
        {
            return null;
        }

        var generatorClass = Catalog.Get(cardId)?.Classes.FirstOrDefault(c => c != "NEUTRAL") ?? _cards.Get(cardId)?.CardClass;
        return Catalog.Cards.Where(c => generator.Query.Matches(c, context, generator.Verb, generatorClass)).ToList();
    }

    /// <summary>
    /// The generator's wiki pools for this game, with the catalog's cards: Standard only (the wiki queries are), and only
    /// pools queried for the player's class. Null while the answer is loading or when there are none; a pool's cards that
    /// are not in Standard any more are left out.
    /// </summary>
    public IReadOnlyList<(WikiPool Pool, IReadOnlyList<PoolCard> Cards)>? WikiPoolsOf(string cardId, PoolContext context)
    {
        // A pool from your deck or hand, or a copy, is not a list the wiki can know.
        if (_wiki is null || !context.IsStandard || Catalog.Count == 0 || Parse(cardId).Status is PoolParseStatus.NotGenerator or PoolParseStatus.NotPool
            || !_wiki.TryGet(cardId, out var wiki))
        {
            return null;
        }

        var pools = wiki.Pools
            .Where(p => p.Format is null || p.Format.Equals("Standard", StringComparison.OrdinalIgnoreCase))
            .Where(p => p.HeroClasses.Count == 0 || context.OwnClass is null || p.HeroClasses.Contains(context.OwnClass))
            .Select(p => (Pool: p, Cards: (IReadOnlyList<PoolCard>)p.CardIds.Select(Catalog.Get).OfType<PoolCard>().Where(c => c.Standard).ToList()))
            .Where(p => p.Cards.Count > 0)
            .ToList();
        return pools.Count > 0 ? pools : null;
    }

    /// <summary>The panel view for a generator; null (and one log line per card) when there is no pool to show.</summary>
    public CardPoolView? Build(string cardId, PoolContext context, PoolTrigger trigger)
    {
        if (!context.IsSupported)
        {
            return null;
        }

        var wiki = WikiPoolsOf(cardId, context);
        var key = (cardId, context, trigger, wiki is not null);
        if (_views.TryGetValue(key, out var known))
        {
            return known;
        }

        // Another trigger of the same pool reuses its lists; the view itself is cached per trigger, so it stays the
        // same object on every overlay update.
        var same = _views.FirstOrDefault(v => v.Key.CardId == cardId && v.Key.Context == context && v.Key.Wiki == key.Item4).Value;
        var built = same is not null ? same with { Trigger = trigger } : BuildCore(cardId, context, trigger, wiki);
        if (built is not null)
        {
            _views[key] = built;
        }

        return built;
    }

    private CardPoolView? BuildCore(string cardId, PoolContext context, PoolTrigger trigger, IReadOnlyList<(WikiPool Pool, IReadOnlyList<PoolCard> Cards)>? wiki)
    {
        var parse = Parse(cardId);
        var text = Pool(cardId, context);
        if (wiki is not null)
        {
            var listed = wiki.SelectMany(w => w.Cards).DistinctBy(c => c.Id).ToList();
            // The wiki page is read now and then: a list the card text finds whole, and more, is an older one (Hematurge's
            // list of 2026-10-12 misses the 6 Blood Rune cards of the newest set). The text pool is the better list then.
            if (text is { Count: > 0 } && text.Count > listed.Count && listed.All(c => text.Any(t => t.Id == c.Id)))
            {
                LogOnce(cardId, $"Card pool: {NameOf(cardId)} ({cardId}) uses its card text: the Hearthstone Wiki list has {listed.Count} of its {text.Count} cards");
                return View(cardId, trigger, parse.Generator!.Phrase, Describe(parse.Generator, context), text, [],
                    $"{TextSource} (the Hearthstone Wiki list is older: {listed.Count} of {text.Count})");
            }

            var sections = wiki.Count > 1 ? wiki.Select(w => (w.Pool.Label, w.Cards)).ToList() : [];
            var phrase = parse.Generator?.Phrase ?? string.Join(", ", wiki.Select(w => w.Pool.Name));
            var description = parse.Generator is { } g ? Describe(g, context) : $"{Title(context.OwnClass ?? "your class")} · Standard";
            return View(cardId, trigger, phrase, description, listed, sections, WikiPools.Source);
        }

        if (parse.Status is PoolParseStatus.Unrecognized or PoolParseStatus.NotPool or PoolParseStatus.Uncomputed)
        {
            var what = parse.Status switch
            {
                PoolParseStatus.Unrecognized => "not understood",
                PoolParseStatus.Uncomputed => "pool not computed (panel says so)",
                _ => "has no catalog pool",
            };
            LogOnce(cardId, $"Card pool: {NameOf(cardId)} ({cardId}) {what}: {parse.Detail} | {TextOf(cardId)}");
        }

        if (parse.Status == PoolParseStatus.Uncomputed)
        {
            return Uncomputed(cardId, trigger, parse.Detail ?? "the catalog cannot answer this pool");
        }

        if (parse.Generator is not { } generator || text is not { Count: > 0 } pool)
        {
            if (parse.Generator is not null)
            {
                LogOnce(cardId, $"Card pool: {NameOf(cardId)} ({cardId}) pool panel hidden: no catalog card matches \"{parse.Generator.Phrase}\" "
                                + $"({context.Format}, {Catalog.Count} cards in the catalog from {Catalog.Source})");
            }

            return null;
        }

        return View(cardId, trigger, generator.Phrase, Describe(generator, context), pool, [], TextSource);
    }

    private void LogOnce(string cardId, string line)
    {
        if (_logged.TryAdd(cardId, 0))
        {
            Diag.Info(line);
        }
    }

    /// <summary>The panel view of a pool: stats over all its cards, each card tagged with the sections it is in.</summary>
    private CardPoolView View(string cardId, PoolTrigger trigger, string phrase, string description, IReadOnlyList<PoolCard> pool,
        IReadOnlyList<(string Label, IReadOnlyList<PoolCard> Cards)> sections, string source)
    {
        var (cost, attack, health) = PoolStats.Histograms(pool);
        var entries = pool
            .Select(c => new PoolEntry(c.Id, NameOf(c.Id, c), c.Cost, c.Attack, c.Type is "MINION" or "WEAPON" ? c.Health : null, c.Type, c.Rarity,
                c.Classes.Count == 1 ? c.Classes[0] : "MULTI", _metaGames.GetValueOrDefault(c.Id))
            {
                Mechanics = PoolKeywords.Shown.Where(m => PoolStats.Has(c, m.Tag)).Select(m => m.Tag).ToList(),
                Sections = sections.Where(s => s.Cards.Any(x => x.Id == c.Id)).Select(s => s.Label).ToList(),
            })
            .OrderBy(e => e.Cost).ThenBy(e => e.Name, StringComparer.CurrentCulture)
            .ToList();
        var featured = entries.Where(e => e.MetaGames > 0).MaxBy(e => e.MetaGames);
        return new CardPoolView(
            cardId,
            NameOf(cardId),
            trigger,
            phrase,
            description,
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
            Source = source,
            Sections = sections.Select(s => new PoolSection(s.Label, s.Cards.Count)).ToList(),
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
