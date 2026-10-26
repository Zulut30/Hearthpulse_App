using Hearthpulse.Core.CardPool;
using Hearthpulse.Core.Memory;
using Hearthpulse.Core.Power;

namespace Hearthpulse.Core.Tracking;

/// <summary>The card pool panel: which generator's pool to show (see <see cref="CardPoolTracker"/>).</summary>
public sealed partial class GameSession
{
    /// <summary>How often the card under the mouse is read while it can matter; a hover usually lasts well over 300 ms.</summary>
    internal static readonly TimeSpan HoverInterval = TimeSpan.FromMilliseconds(250);

    private static readonly TimeSpan HoverIdle = TimeSpan.FromSeconds(10);

    private readonly CardPoolTracker _cardPool = new();
    private readonly object _cardPoolGate = new();
    private CardPoolCatalog _cardPoolCatalog = CardPoolCatalog.Empty;
    private WikiPoolSource? _cardPoolWiki;
    private Pool.DiscoverPickSource? _discoverPicks;
    private Cards.DeckDefinition? _wikiPrefetched;
    private (CardPoolCatalog Catalog, Cards.CardDb Cards, Constructed.ConstructedMeta? Meta)? _cardPoolInputs;
    private volatile bool _cardPoolEnabled = true;
    private volatile bool _cardPoolHoverWanted;
    private Thread? _hoverThread;

    /// <summary>Whether the card under the mouse is being read from the game now; internal for tests and the bench.</summary>
    internal bool HoverReadsWanted => _cardPoolHoverWanted;

    /// <summary>The collectible card catalog pools are cut from; the host passes it after loading.</summary>
    public void SetCardPoolCatalog(CardPoolCatalog catalog)
    {
        lock (_cardPoolGate)
        {
            _cardPoolCatalog = catalog;
        }

        _dirty = true;
    }

    /// <summary>Hearthstone Wiki pools (the first source of a pool); the host passes it once at start-up.</summary>
    public void SetCardPoolWiki(WikiPoolSource? wiki)
    {
        lock (_cardPoolGate)
        {
            if (_cardPoolWiki is { } previous)
            {
                previous.Changed -= MarkDirty;
            }

            _cardPoolWiki = wiki;
            if (wiki is not null)
            {
                wiki.Changed += MarkDirty;
            }

            _cardPool.Engine = null;
        }

        _dirty = true;
    }

    /// <summary>The stats pool's pick totals for the pick hint of an open choice; the host passes it once at start-up.</summary>
    public void SetDiscoverPicks(Pool.DiscoverPickSource? picks)
    {
        lock (_cardPoolGate)
        {
            if (_discoverPicks is { } previous)
            {
                previous.Changed -= MarkDirty;
            }

            _discoverPicks = picks;
            if (picks is not null)
            {
                picks.Changed += MarkDirty;
            }
        }

        _dirty = true;
    }

    /// <summary>The "Show card pool" setting: off stops the memory hover reads and hides the pool.</summary>
    public void SetCardPoolEnabled(bool enabled)
    {
        _cardPoolEnabled = enabled;
        _dirty = true;
    }

    /// <summary>
    /// Shows the pool of <paramref name="cardId"/> on the overlay until called with null - for hover over deck and
    /// hand rows. Generators without a known pool show nothing.
    /// </summary>
    public void ShowCardPool(string? cardId)
    {
        _cardPool.Request(cardId);
        _dirty = true;
    }

    /// <summary>The pool view of any card for a format and classes, outside the live game (null when it has no pool).</summary>
    public CardPoolView? BuildCardPool(string cardId, PoolContext context) =>
        CardPoolEngineNow()?.Build(cardId, context, PoolTrigger.Request);

    /// <summary>Adds the current pool to a Constructed view; called while publishing.</summary>
    private ConstructedView WithCardPool(ConstructedView view, GameState game)
    {
        // Memory is read only when a hover can show a pool: setting on, catalog loaded, a mode and format with a known
        // pool, and a card with a pool in your hand or on your board.
        var engine = _cardPoolEnabled ? CardPoolEngineNow() : null;
        _cardPoolHoverWanted = engine is not null && HearthstoneMemory.IsSupported
                               && CardPoolTracker.ContextOf(game, _cards).IsSupported
                               && _cardPool.HandHasPool(game, _cards);
        if (_cardPoolHoverWanted)
        {
            EnsureHoverThread();
        }

        if (engine is null)
        {
            return view;
        }

        _cardPool.Engine = engine;
        PrefetchWiki(engine, game);
        try
        {
            var pool = _cardPool.BuildView(game, _cards);
            return view with { CardPool = pool, CardPoolAnchor = _cardPool.AnchorOf(game, pool), DiscoverPicks = PicksOf(game, pool) };
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            Diag.Error("Building the card pool failed", ex);
            return view;
        }
    }

    /// <summary>
    /// The pick hint while the player's own choice is open and its pool is up: from the totals already in memory, so a
    /// choice never waits on (or tells) the server. Null outside a choice, without a pool address or in other formats.
    /// </summary>
    private DiscoverPicksView? PicksOf(GameState game, CardPoolView? pool)
    {
        if (pool?.Trigger != PoolTrigger.Choice || _discoverPicks is not { } source || _cardPool.OpenChoice is not { } open
            || DiscoverPicks.FormatOf(game.FormatType) is not { } format || source.TryGet(format) is not { } stats)
        {
            return null;
        }

        return DiscoverPicks.Build(stats, open.Source, open.Offered, _cards);
    }

    private CardPoolEngine? CardPoolEngineNow()
    {
        lock (_cardPoolGate)
        {
            var inputs = (_cardPoolCatalog, _cards, _constructed.Meta);
            if (_cardPoolCatalog.Count == 0)
            {
                return null;
            }

            if (_cardPoolInputs is not { } previous || !ReferenceEquals(previous.Catalog, inputs.Item1)
                || !ReferenceEquals(previous.Cards, inputs.Item2) || !ReferenceEquals(previous.Meta, inputs.Item3)
                || _cardPool.Engine is null)
            {
                _cardPoolInputs = inputs;
                _cardPool.Engine = new CardPoolEngine(_cardPoolCatalog.WithRaces(_cards), _cards, CardPoolEngine.MetaGames(_constructed.Meta, _cards), _cardPoolWiki);
            }

            return _cardPool.Engine;
        }
    }

    /// <summary>Asks for the wiki pools of the deck's generators once per deck, so they are on disk before they are played.</summary>
    private void PrefetchWiki(CardPoolEngine engine, GameState game)
    {
        if (_cardPoolWiki is not { } wiki || _constructed.Deck is not { } deck || ReferenceEquals(deck, _wikiPrefetched) || game.FormatType != "FT_STANDARD")
        {
            return;
        }

        _wikiPrefetched = deck;
        foreach (var dbfId in deck.Cards.Keys)
        {
            if (_cards.Get(dbfId)?.Id is { } id && engine.Parse(id).Status is PoolParseStatus.Pool or PoolParseStatus.Uncomputed)
            {
                wiki.Request(id);
            }
        }
    }

    // The card under the mouse is read from game memory on its own thread: the log thread must never wait on it.
    // It runs only while a pool can be shown and ends on its own (after HoverIdle without a Constructed game); the next
    // publish that wants it starts it again.
    private void EnsureHoverThread()
    {
        lock (_cardPoolGate)
        {
            if (_hoverThread is { IsAlive: true })
            {
                return;
            }

            _hoverThread = new Thread(HoverLoop) { IsBackground = true, Name = "Hearthpulse card hover" };
            _hoverThread.Start();
        }
    }

    private void HoverLoop()
    {
        var idleSince = DateTime.UtcNow;
        while (true)
        {
            var now = DateTime.UtcNow;
            try
            {
                var active = _cardPoolHoverWanted && Current.Constructed is not null;
                if (active)
                {
                    idleSince = now;
                }
                else if (now - idleSince > HoverIdle)
                {
                    break;
                }

                // Attaching is MemoryPoller's job; only read once it is done.
                var hovered = active && _memory.IsAttached ? _memory.GetHoveredCardId() : null;
                if (_cardPool.SetHovered(hovered, now))
                {
                    _dirty = true;
                }
            }
            catch (Exception ex)
            {
                Diag.Error("Reading the card under the mouse failed", ex);
                Thread.Sleep(TimeSpan.FromSeconds(5));
            }

            Thread.Sleep(HoverInterval);
        }

        lock (_cardPoolGate)
        {
            _hoverThread = null;
        }
    }
}
