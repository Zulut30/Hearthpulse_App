using Hearthlens.Core.Cards;
using Hearthlens.Core.Memory;
using Hearthlens.Core.Power;

namespace Hearthlens.Core.Tracking;

/// <summary>Deck and opponent tracking for Standard/Wild/Arena/Brawl style games.</summary>
public sealed class ConstructedTracker : IPowerListener
{
    private static readonly HashSet<string> PlayedZones = ["PLAY", "GRAVEYARD", "SECRET"];
    private static readonly HashSet<string> IgnoredTypes = ["HERO", "HERO_POWER", "ENCHANTMENT", "PLAYER", "GAME", ""];

    private readonly List<(int EntityId, int Turn)> _opponentPlayed = new();
    private readonly HashSet<int> _opponentPlayedIds = new();

    public DeckDefinition? Deck { get; set; }

    public RankInfo? LocalRank { get; set; }

    public RankInfo? OpponentRank { get; set; }

    public void OnGameStarted(GameState game)
    {
        _opponentPlayed.Clear();
        _opponentPlayedIds.Clear();
        LocalRank = null;
        OpponentRank = null;
    }

    public void OnTagChanged(GameState game, Entity entity, string tag, string previous, string value)
    {
        if (tag != GameTag.Zone || game.LocalPlayerId == 0 || entity.Controller == game.LocalPlayerId)
        {
            return;
        }

        if (previous is "HAND" or "DECK" && PlayedZones.Contains(value) && !IgnoredTypes.Contains(entity.CardType)
            && entity.CardId.Length > 0 && _opponentPlayedIds.Add(entity.Id))
        {
            _opponentPlayed.Add((entity.Id, ToPlayerTurn(game.Turn)));
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
            .Select(e =>
            {
                var creator = game.Entities.GetValueOrDefault(e.GetInt(GameTag.DisplayedCreator) is > 0 and var d ? d : e.GetInt(GameTag.Creator));
                var createdBy = creator is { CardId.Length: > 0 } && creator.CardType is not ("PLAYER" or "GAME")
                    ? cards.NameOf(creator.CardId)
                    : null;
                return new HandSlot(
                    e.ZonePosition,
                    ToPlayerTurn(e.EnteredHandTurn),
                    e.CardId.Length > 0 ? e.CardId : null,
                    e.CardId.Length > 0 ? cards.NameOf(e.CardId) : null,
                    e.EnteredHandTurn == 0,
                    e.CardId == "GAME_005",
                    createdBy);
            })
            .ToList();

        return new ConstructedView(
            ToPlayerTurn(game.Turn),
            Deck?.Name,
            Deck is not null,
            playerHero,
            cards.Get(playerHero)?.CardClass ?? string.Empty,
            opponentHero,
            cards.Get(opponentHero)?.CardClass ?? string.Empty,
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
            OpponentRank);
    }

    private List<DeckCardRow> BuildDeckRows(GameState game, CardDb cards, int local, int deckCount)
    {
        var owned = game.Entities.Values.Where(e => e.Controller == local || e.InitialController == local).ToList();
        var removed = owned
            .Where(e => e.IsOriginalDeckCard && e.InitialController == local && e.CardId.Length > 0 && !e.InDeck)
            .GroupBy(e => e.CardId).ToDictionary(g => g.Key, g => g.Count());
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

    private static int ToPlayerTurn(int turn) => turn <= 0 ? 0 : (turn + 1) / 2;
}
