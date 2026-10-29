using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Constructed;
using Hearthpulse.Core.Memory;
using Hearthpulse.Core.Power;

namespace Hearthpulse.Core.Tracking;

/// <summary>Deck and opponent tracking for Standard/Wild/Arena/Brawl style games.</summary>
public sealed class ConstructedTracker : IPowerListener
{
    private static readonly HashSet<string> PlayedZones = ["PLAY", "GRAVEYARD", "SECRET"];
    private static readonly HashSet<string> IgnoredTypes = ["HERO", "HERO_POWER", "ENCHANTMENT", "PLAYER", "GAME", ""];

    private readonly List<(int EntityId, int Turn)> _opponentPlayed = new();
    private readonly HashSet<int> _opponentPlayedIds = new();

    /// <summary>The zone each opponent card was in before it last entered the hand (DECK, PLAY, SETASIDE...).</summary>
    private readonly Dictionary<int, string> _cameToHandFrom = new();

    /// <summary>
    /// Opponent cards drawn from a deck and not shown since. The log may still carry their card id (a card you shuffled
    /// into their deck, a card that was revealed and went back), but the player cannot tell which card back it is.
    /// </summary>
    private readonly HashSet<int> _hiddenDraws = new();

    public DeckDefinition? Deck { get; set; }

    /// <summary>Community stats by format key ("standard", "wild"); replaced whole, so the log thread reads a stable copy.</summary>
    private IReadOnlyDictionary<string, ConstructedMeta> _metas = new Dictionary<string, ConstructedMeta>();

    /// <summary>Formats whose stats this game asked the host for (log thread only).</summary>
    private readonly HashSet<string> _metaAsked = new(StringComparer.Ordinal);

    /// <summary>The FormatType of the game being tracked (Standard until the log tells).</summary>
    private string _formatType = "FT_STANDARD";

    /// <summary>
    /// Community stats for the mulligan panel and the deck's archetype: each game uses the stats of its own format, so
    /// the page the player last looked at (Standard or Wild) never takes them away. Reading gives the current game's
    /// format; setting adds stats for their format (null forgets all of them).
    /// </summary>
    public ConstructedMeta? Meta
    {
        get => MetaFor(_formatType);
        set => _metas = value is null
            ? new Dictionary<string, ConstructedMeta>()
            : new Dictionary<string, ConstructedMeta>(_metas) { [value.Format] = value };
    }

    /// <summary>
    /// Raised (on the log thread, once per format and game) when a game's format has no stats yet: the host downloads
    /// them and sets <see cref="Meta"/>. Argument: the format key ("standard", "wild").
    /// </summary>
    public event Action<string>? MetaNeeded;

    /// <summary>The stats of a game FormatType; null for formats without stats or before they are in.</summary>
    public ConstructedMeta? MetaFor(string? formatType) =>
        ConstructedMeta.FormatKeyOf(formatType) is { } key && _metas.TryGetValue(key, out var meta) ? meta : null;

    /// <summary>The Hearthpulse API mulligan guide (preferred over <see cref="Meta"/> when it knows the deck).</summary>
    public MulliganGuideSource? Guide { get; set; }

    /// <summary>Arena card results: the mulligan numbers of an Arena game (<see cref="MulliganArena"/>).</summary>
    public Arena.ArenaMeta? Arena { get; set; }

    /// <summary>What each player did this game, for the counters; registered as a Power.log listener by the session.</summary>
    public Counters.CounterTracker Counters { get; } = new();

    public RankInfo? LocalRank { get; set; }

    public RankInfo? OpponentRank { get; set; }

    public void OnGameStarted(GameState game)
    {
        _opponentPlayed.Clear();
        _opponentPlayedIds.Clear();
        _cameToHandFrom.Clear();
        _hiddenDraws.Clear();
        _mulliganNote = null;
        _metaAsked.Clear();
        _formatType = "FT_STANDARD";
        LocalRank = null;
        OpponentRank = null;
    }

    public void OnTagChanged(GameState game, Entity entity, string tag, string previous, string value)
    {
        if (tag != GameTag.Zone || game.LocalPlayerId == 0 || entity.Controller == game.LocalPlayerId)
        {
            return;
        }

        if (value == "HAND")
        {
            _cameToHandFrom[entity.Id] = previous;
            if (previous == "DECK")
            {
                _hiddenDraws.Add(entity.Id);
            }
            else
            {
                _hiddenDraws.Remove(entity.Id);
            }
        }

        if (previous is "HAND" or "DECK" && PlayedZones.Contains(value) && !IgnoredTypes.Contains(entity.CardType)
            && entity.CardId.Length > 0 && _opponentPlayedIds.Add(entity.Id))
        {
            _opponentPlayed.Add((entity.Id, ToPlayerTurn(game.Turn)));
        }
    }

    public void OnEntityRevealed(GameState game, Entity entity)
    {
        // SHOW_ENTITY of a card in the opponent's hand: the client showed it (Casts When Drawn, a reveal effect).
        if (entity.InHand && entity.Controller != game.LocalPlayerId)
        {
            _hiddenDraws.Remove(entity.Id);
        }
    }

    public ConstructedView BuildView(GameState game, CardDb cards)
    {
        var local = game.LocalPlayerId;
        var opponent = game.OpponentPlayerId;
        var playerHero = game.LocalHero?.CardId ?? string.Empty;
        var opponentHero = game.OpponentHero?.CardId ?? string.Empty;

        var deckCount = game.Controlled(local, "DECK").Count();
        var rows = BuildDeckRows(game, cards, local, deckCount);

        var played = _opponentPlayed
            .Select(p => (Entity: game.Entities.GetValueOrDefault(p.EntityId), p.Turn))
            .Where(p => p.Entity is { CardId.Length: > 0 })
            .GroupBy(p => p.Entity!.CardId)
            .Select(g =>
            {
                var card = cards.Get(g.Key);
                return new PlayedCard(
                    g.Key,
                    card?.Name ?? g.First().Entity!.Name,
                    card?.Cost ?? 0,
                    g.Count(),
                    g.Min(x => x.Turn),
                    g.All(x => !x.Entity!.IsOriginalDeckCard));
            })
            .OrderBy(p => p.Cost).ThenBy(p => p.Name, StringComparer.CurrentCulture)
            .ToList();

        var hand = game.Controlled(opponent, "HAND")
            .OrderBy(e => e.ZonePosition)
            .Select(e => HandSlotOf(game, cards, e))
            .ToList();

        var playerClass = HeroClass.Of(game, game.LocalPlayerEntity, cards);
        var opponentClass = HeroClass.Of(game, game.OpponentPlayerEntity, cards);
        var meta = GameMeta(game);
        // Asked on every update so the lookup starts as soon as the deck is known, before the mulligan begins.
        // During the mulligan the coin and the opponent's class are known: their slices load then.
        var mulligan = Mulligan.InProgress(game);
        var guide = Deck is not null && game.IsConstructedLike && !MulliganArena.IsArena(game)
            ? Guide?.For(playerClass, Deck, game.FormatType, cards, mulligan ? cards.Get(game.OpponentHero?.CardId)?.CardClass : null, mulligan ? Mulligan.GoingFirst(game, cards) : null)
            : null;
        return new ConstructedView(
            ToPlayerTurn(game.Turn),
            Deck?.Name,
            Deck is not null,
            playerHero,
            playerClass,
            opponentHero,
            opponentClass,
            game.OpponentPlayer?.Name,
            rows,
            deckCount,
            game.Controlled(local, "HAND").Count(),
            played,
            hand,
            game.Controlled(opponent, "DECK").Count(),
            hand.Count,
            game.Controlled(opponent, "SECRET").Count(e => e.Has(GameTag.Secret)),
            LocalRank,
            OpponentRank)
        {
            Mulligan = NoteMulligan(game, meta, Mulligan.Build(game, cards, Deck, meta, playerClass, guide, Arena), guide is not null),
            Counters = Tracking.Counters.Counters.Build(game, Counters, cards, Deck),
            DeckId = Deck?.DeckId ?? 0,
            DeckCode = Deck?.Code,
            DeckMeta = DeckMetaFor(cards, playerClass, game.FormatType),
            OpponentArchetype = OpponentArchetypeFor(cards, opponentClass, game.GameType, game.FormatType, played),
        };
    }

    private (ConstructedMeta? Meta, string Key, OpponentArchetypeView? View) _opponentArchetype;

    /// <summary>The archetype odds, worked out again only when the meta or the shown starting-deck cards change.</summary>
    private OpponentArchetypeView? OpponentArchetypeFor(CardDb cards, string? opponentClass, string gameType, string formatType, IReadOnlyList<PlayedCard> played)
    {
        var shown = played.Where(p => !p.IsCreated).Select(p => p.CardId).ToList();
        var key = $"{opponentClass}|{gameType}|{formatType}|{string.Join(',', shown)}";
        if (ReferenceEquals(_opponentArchetype.Meta, Meta) && _opponentArchetype.Key == key)
        {
            return _opponentArchetype.View;
        }

        var view = ArchetypeOdds.Build(Meta, gameType, formatType, opponentClass, shown, cards);
        _opponentArchetype = (Meta, key, view);
        return view;
    }

    private string? _mulliganNote;

    /// <summary>Logs whether the mulligan panel has something to show, once per change while the mulligan is on.</summary>
    private MulliganView? NoteMulligan(GameState game, ConstructedMeta? meta, MulliganView? view, bool hasGuide)
    {
        if (!Mulligan.InProgress(game))
        {
            return view;
        }

        var note = Mulligan.Explain(game, view, Deck, hasGuide, meta);
        if (note != _mulliganNote)
        {
            _mulliganNote = note;
            Diag.Info(char.ToUpperInvariant(note[0]) + note[1..]);
        }

        return view;
    }

    /// <summary>The stats of the game's format; asks the host once per game for a format that has none yet.</summary>
    private ConstructedMeta? GameMeta(GameState game)
    {
        if (!string.IsNullOrEmpty(game.FormatType))
        {
            _formatType = game.FormatType;
        }

        var meta = MetaFor(game.FormatType);
        if (meta is null && game.IsConstructedLike && ConstructedMeta.FormatKeyOf(game.FormatType) is { } key && _metaAsked.Add(key))
        {
            MetaNeeded?.Invoke(key);
        }

        return meta;
    }

    private (DeckDefinition? Deck, ConstructedMeta? Meta, DeckMetaStat? Stat) _deckMeta;

    /// <summary>The deck's archetype in the community stats; matched once per deck and stats download.</summary>
    private DeckMetaStat? DeckMetaFor(CardDb cards, string playerClass, string formatType)
    {
        // Only the game's own format: a Wild deck against Standard stats would quote the wrong archetype numbers.
        if (Deck is null || MetaFor(formatType) is not { } meta)
        {
            return null;
        }

        if (ReferenceEquals(_deckMeta.Deck, Deck) && ReferenceEquals(_deckMeta.Meta, meta))
        {
            return _deckMeta.Stat;
        }

        var ids = Deck.Cards.Keys.Select(cards.Get).OfType<Card>().Select(c => c.Id).ToList();
        var archetype = meta.Match(playerClass, ids);
        var source = meta.Source;
        var stat = archetype is null
            ? null
            : new DeckMetaStat(archetype.Name, archetype.WinRate, archetype.Games, ConstructedMeta.Label(ConstructedMeta.Ranks, meta.Rank), source)
            {
                Format = ConstructedMeta.Label(ConstructedMeta.Formats, meta.Format),
            };
        _deckMeta = (Deck, meta, stat);
        return stat;
    }

    private HandSlot HandSlotOf(GameState game, CardDb cards, Entity e)
    {
        // Only what the player can see: a drawn card stays a card back until it is shown, and a creator is named only
        // when its own card is revealed (CREATOR_DBID names hidden creators too, so it is never used).
        var hidden = _hiddenDraws.Contains(e.Id);
        var cardId = hidden ? string.Empty : e.CardId;
        var creator = hidden ? null : game.Entities.GetValueOrDefault(e.GetInt(GameTag.DisplayedCreator) is > 0 and var d ? d : e.GetInt(GameTag.Creator));
        var creatorId = creator is { CardId.Length: > 0 } && creator.CardType is not ("PLAYER" or "GAME") ? creator.CardId : null;
        // Only the opening-hand Coin means they went second; a Coin made later is just a created card. The Coin can be a
        // skin (EDR_COIN1, JAIL_COIN3...): COIN_CARD marks it.
        var isCoin = (cardId == "GAME_005" || e.GetInt("COIN_CARD") == 1) && e.EnteredOpeningHand;
        var from = _cameToHandFrom.GetValueOrDefault(e.Id, e.InitialZone);
        var origin = isCoin ? HandCardOrigin.Coin
            : e.InitialController == game.LocalPlayerId && game.LocalPlayerId != 0 ? HandCardOrigin.Stolen
            : hidden ? (e.EnteredOpeningHand ? HandCardOrigin.Mulligan : HandCardOrigin.Drawn)
            : from is "PLAY" or "GRAVEYARD" or "SECRET" ? HandCardOrigin.Returned
            : from == "DECK" && !e.IsOriginalDeckCard ? HandCardOrigin.Shuffled
            : from != "DECK" && (creatorId is not null || e.CreatedTurn > 0) ? HandCardOrigin.Created
            : e.EnteredOpeningHand ? HandCardOrigin.Mulligan
            : HandCardOrigin.Drawn;
        return new HandSlot(
            e.ZonePosition,
            ToPlayerTurn(e.EnteredHandTurn),
            cardId.Length > 0 ? cardId : null,
            cardId.Length > 0 ? cards.NameOf(cardId) : null,
            e.EnteredOpeningHand,
            isCoin,
            creatorId is null ? null : cards.NameOf(creatorId))
        {
            EntityId = e.Id,
            CreatedById = creatorId,
            CreatorIsYours = creator is not null && creatorId == creator.CardId && creator.Controller == game.LocalPlayerId,
            Origin = origin,
        };
    }

    private List<DeckCardRow> BuildDeckRows(GameState game, CardDb cards, int local, int deckCount)
    {
        var owned = game.Entities.Values.Where(e => e.Controller == local || e.InitialController == local).ToList();
        // A card that transformed in hand still left the deck as the card it was drawn as.
        var removed = owned
            .Where(e => e.IsOriginalDeckCard && e.InitialController == local && e.FirstCardId.Length > 0 && !e.InDeck)
            .GroupBy(e => e.FirstCardId).ToDictionary(g => g.Key, g => g.Count());
        var added = owned
            .Where(e => e.Controller == local && e.InDeck && !e.IsOriginalDeckCard && e.CardId.Length > 0)
            .GroupBy(e => e.CardId).ToDictionary(g => g.Key, g => g.Count());

        var rows = new List<DeckCardRow>();
        if (Deck is not null)
        {
            foreach (var (dbf, total) in Deck.Cards)
            {
                var card = cards.Get(dbf);
                if (card is null)
                {
                    continue;
                }

                var remaining = Math.Max(0, total - removed.GetValueOrDefault(card.Id));
                rows.Add(Row(card, remaining, total, deckCount, false));
            }
        }
        else
        {
            foreach (var (cardId, count) in removed)
            {
                if (cards.Get(cardId) is { } card)
                {
                    rows.Add(Row(card, 0, count, deckCount, false));
                }
            }
        }

        foreach (var (cardId, count) in added)
        {
            if (cards.Get(cardId) is { } card)
            {
                rows.Add(Row(card, count, count, deckCount, true));
            }
        }

        return rows
            .OrderBy(r => r.Cost).ThenBy(r => r.Name, StringComparer.CurrentCulture)
            .ToList();
    }

    private static DeckCardRow Row(Card card, int remaining, int total, int deckCount, bool added) =>
        new(card.Id, card.Name, card.Cost, remaining, total, card.Rarity,
            deckCount > 0 ? Math.Min(1.0, (double)remaining / deckCount) : 0, added);

    private static int ToPlayerTurn(int turn) => GameState.PlayerTurnOf(turn);
}
