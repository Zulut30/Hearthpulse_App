using Hearthpulse.Core.CardPool;
using Hearthpulse.Core.Memory;
using Hearthpulse.Core.Power;

namespace Hearthpulse.Core.Tracking;

/// <summary>The card pool panel: which generator's pool to show (see <see cref="CardPoolTracker"/>).</summary>
public sealed partial class GameSession
{
    private static readonly TimeSpan HoverInterval = TimeSpan.FromMilliseconds(200);

    private readonly CardPoolTracker _cardPool = new();
    private readonly object _cardPoolGate = new();
    private CardPoolCatalog _cardPoolCatalog = CardPoolCatalog.Empty;
    private (CardPoolCatalog Catalog, Cards.CardDb Cards, Constructed.ConstructedMeta? Meta)? _cardPoolInputs;
    private volatile bool _cardPoolEnabled = true;
    private volatile bool _cardPoolHoverWanted;
    private Thread? _hoverThread;

    /// <summary>The collectible card catalog pools are cut from; the host passes it after loading.</summary>
    public void SetCardPoolCatalog(CardPoolCatalog catalog)
    {
        lock (_cardPoolGate)
        {
            _cardPoolCatalog = catalog;
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
        _cardPoolHoverWanted = _cardPoolEnabled && HearthstoneMemory.IsSupported;
        if (_cardPoolHoverWanted)
        {
            EnsureHoverThread();
        }

        if (!_cardPoolEnabled)
        {
            return view;
        }

        _cardPool.Engine = CardPoolEngineNow();
        try
        {
            return view with { CardPool = _cardPool.BuildView(game, _cards, DateTime.Now) };
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            Diag.Error("Building the card pool failed", ex);
            return view;
        }
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
                _cardPool.Engine = new CardPoolEngine(_cardPoolCatalog.WithRaces(_cards), _cards, CardPoolEngine.MetaGames(_constructed.Meta, _cards));
            }

            return _cardPool.Engine;
        }
    }

    // The card under the mouse is read from game memory on its own thread: the log thread must never wait on it.
    private void EnsureHoverThread()
    {
        if (_hoverThread is not null)
        {
            return;
        }

        _hoverThread = new Thread(HoverLoop) { IsBackground = true, Name = "Hearthpulse card hover" };
        _hoverThread.Start();
    }

    private void HoverLoop()
    {
        while (true)
        {
            try
            {
                // Attaching is MemoryPoller's job; only read once it is done.
                var hovered = _cardPoolHoverWanted && Current.Constructed is not null && _memory.IsAttached ? _memory.GetHoveredCardId() : null;
                if (_cardPool.SetHovered(hovered))
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
    }
}
