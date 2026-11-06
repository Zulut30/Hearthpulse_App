using System.Globalization;
using Hearthpulse.Core.Battlegrounds;
using Hearthpulse.Core.Battlegrounds.Effects;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Memory;
using Hearthpulse.Core.Power;

namespace Hearthpulse.Core.Tracking;

/// <summary>Reading entities: the shop, minions and leaderboard heroes, and the simulator's sides built from them.</summary>
public sealed partial class BattlegroundsTracker
{
    private void NoteShopCard(GameState game, Entity entity)
    {
        if (!game.IsBattlegrounds || entity.Controller != game.OpponentPlayerId || !entity.InPlay
            || (!entity.IsMinion && entity.CardType is not ("SPELL" or "BATTLEGROUND_SPELL")))
        {
            return;
        }

        // A shop card dealt in a later round than the combat start means the fight is over even if the
        // client's BOARD_VISUAL_STATE transition was never logged.
        if (_inCombat && game.BattlegroundsTurn != _combatTurn)
        {
            game.CombatOnScreen = false;
            EndCombat(game);
        }

        if (!_inCombat)
        {
            NoteShopOffer(game, entity);
        }

        // Mid-combat summons are opponent-controlled minions too: only a dealt shop counts for races.
        if (_inCombat || !entity.IsMinion)
        {
            return;
        }

        if (Cards.Get(entity.CardId) is { IsBattlegroundsPoolMinion: true } card)
        {
            foreach (var race in card.Races)
            {
                if (race != "ALL")
                {
                    _shopRaces.Add(race);
                }
            }
        }
    }

    private static bool IsLocal(GameState game, Entity entity) =>
        entity.Id == game.LocalPlayer?.EntityId || entity.Id == game.LocalHero?.Id;

    private static IEnumerable<Entity> Minions(GameState game, int controller) =>
        game.Entities.Values
            .Where(e => e.Controller == controller && e.InPlay && e.IsMinion)
            .OrderBy(e => e.ZonePosition);

    /// <summary>Bob's current offer: shop minions plus tavern spells, in row order.</summary>
    private static IEnumerable<Entity> ShopCards(GameState game)
    {
        var opponent = game.OpponentPlayerId;
        return game.Entities.Values
            .Where(e => e.Controller == opponent && e.InPlay
                        && (e.IsMinion || e.CardType is "SPELL" or "BATTLEGROUND_SPELL"))
            .OrderBy(e => e.ZonePosition);
    }

    /// <summary>Opponents' persistent leaderboard heroes (not combat copies or hero choices).</summary>
    private static IEnumerable<Entity> LeaderboardHeroes(GameState game) =>
        LeaderboardHeroes(game.Entities.Values.Where(e => e.IsHero), game.LocalHero);

    /// <summary>The leaderboard heroes among <paramref name="heroes"/> (every hero entity, in the game's order).</summary>
    private static IEnumerable<Entity> LeaderboardHeroes(IEnumerable<Entity> heroes, Entity? local)
    {
        return heroes
            .Where(e => e.GetInt(GameTag.PlayerId) > 0 && e.GetInt(GameTag.PlayerLeaderboardPlace) > 0
                        && (e.Zone == "SETASIDE" || e == local) && e.CardId != PlaceholderHero)
            .GroupBy(e => e.GetInt(GameTag.PlayerId))
            // Combat copies share PLAYER_ID; the persistent leaderboard entity is the oldest one.
            .Select(g => g.MinBy(e => e.Id)!);
    }

    private static Entity? LeaderboardHero(GameState game, int playerId) =>
        LeaderboardHeroes(game).FirstOrDefault(h => h.GetInt(GameTag.PlayerId) == playerId);

    /// <summary>
    /// A minion left the board during start of combat inside a TRIGGER block of another minion: remember it for that
    /// minion (Stitched Salvager's deathrattle summons a copy of the minion it destroyed). Inferred from the log's block
    /// structure; not yet checked against a real log.
    /// </summary>
    private void RememberRemoval(GameState game, Entity removed)
    {
        for (var i = game.OpenBlocks.Count - 1; i >= 0; i--)
        {
            var block = game.OpenBlocks[i];
            if (block.Type != "TRIGGER" || block.Source is not { IsMinion: true } source || source.Id == removed.Id)
            {
                continue;
            }

            var noEnchantments = Array.Empty<string>().ToLookup(_ => 0);
            var copy = ToSim(removed, removed.Controller != game.LocalPlayerId, noEnchantments) with { HasUnsimulatedEffect = false };
            if (!_removedBy.TryGetValue(source.Id, out var list))
            {
                _removedBy[source.Id] = list = new List<SimMinion>();
            }

            list.Add(copy);
            return;
        }
    }

    /// <summary>Enchantment card ids by the minion they are attached to.</summary>
    private static ILookup<int, string> Enchantments(GameState game) =>
        game.Entities.Values
            .Where(e => e.CardType == "ENCHANTMENT" && e.InPlay && e.CardId.Length > 0 && e.GetInt(GameTag.Attached) > 0)
            .OrderBy(e => e.Id)
            .ToLookup(e => e.GetInt(GameTag.Attached), e => e.CardId);

    /// <summary>
    /// One side for the simulator: board minions plus what the player can see about that player. The opponent's hand
    /// and secrets are hidden information and are never read.
    /// </summary>
    private SimSide BuildSide(GameState game, int controller, bool isOpponent, ILookup<int, string> enchantments)
    {
        var minions = Minions(game, controller)
            .Select(e => ToSim(e, isOpponent, enchantments) with
            {
                CreatorCardId = e.GetInt(GameTag.Creator) is > 0 and var creator ? game.Entities.GetValueOrDefault(creator)?.CardId ?? string.Empty : string.Empty,
            })
            .ToList();
        var player = game.Players.TryGetValue(controller, out var slot) ? game.Entities.GetValueOrDefault(slot.EntityId) : null;
        var hero = game.HeroOf(player);
        var played = game.Controlled(controller, "PLAY").Where(e => e.CardId.Length > 0).OrderBy(e => e.Id).ToList();
        var heroPower = played.FirstOrDefault(e => e.CardType == "HERO_POWER");
        var extras = played.Where(e => ExtraCardTypes.Contains(e.CardType) || IsTrinket(e)).Select(e => e.CardId).ToList();
        // Lobby-wide cards: the anomaly and this season's Deity (tag name as hsdata names it; unverified in a real log).
        foreach (var tag in GlobalCardTags)
        {
            var dbf = game.GameEntity?.GetInt(tag) ?? 0;
            if (dbf > 0 && Cards.Get(dbf) is { } global)
            {
                extras.Add(global.Id);
            }
        }

        var hand = isOpponent
            ? Array.Empty<SimMinion>()
            : game.Controlled(controller, "HAND")
                .Where(e => e.IsMinion && e.CardId.Length > 0)
                .OrderBy(e => e.ZonePosition)
                .Select(e => ToSim(e, isOpponent: false, enchantments) with { HasUnsimulatedEffect = false })
                .ToArray();
        return new SimSide(minions)
        {
            HeroCardId = hero?.CardId ?? string.Empty,
            HeroPowerCardId = heroPower?.CardId ?? string.Empty,
            Extras = extras.Distinct().ToList(),
            // Only the local player's own hand: it is on their screen. The opponent's is hidden.
            Hand = hand,
            Tags = NumericTags(player),
            TavernTier = hero?.GetInt(GameTag.PlayerTechLevel) is > 0 and var tier ? tier : player?.GetInt(GameTag.PlayerTechLevel) ?? 0,
        };
    }

    private static IReadOnlyDictionary<string, int> NumericTags(Entity? entity)
    {
        var tags = new Dictionary<string, int>(StringComparer.Ordinal);
        if (entity is null)
        {
            return tags;
        }

        foreach (var (tag, value) in entity.Tags)
        {
            if (int.TryParse(value, CultureInfo.InvariantCulture, out var number))
            {
                tags[tag] = number;
            }
        }

        return tags;
    }

    private SimMinion ToSim(Entity e, bool isOpponent, ILookup<int, string> enchantments)
    {
        // Golden minions have their own card id (golden stats and text); effects are keyed by the normal id + Golden.
        var card = Cards.Get(e.CardId);
        var (cardId, goldenId) = CombatRelevance.Normalize(e.CardId, Cards);
        var golden = goldenId || e.GetInt(GameTag.Premium) > 0;
        var mechanics = card?.Mechanics ?? Array.Empty<string>();
        var attached = enchantments[e.Id].ToList();
        // Known card: the generated table (or the text fallback for a newer card). Unknown card: its effect tags.
        var relevant = card is null && CombatRelevance.Find(e.CardId) is null
            ? CombatRelevance.TagsSuggestCombatEffect(e.Has)
            : CombatRelevance.IsCombatRelevant(e.CardId, card, Cards.Locale);
        // A deathrattle, Avenge or Rally the card does not print came from an enchantment (Dark Gift, Tamsin...).
        var granted = card is not null ? CombatRelevance.GrantedMechanics(e.Has, mechanics) : [];
        var unsimulated = CombatRelevance.IsUnsimulated(cardId, golden, relevant, granted, attached, isOpponent, Effects);

        var keywords = KeywordsOf(e);
        // A reborn copy is a fresh copy of the card. Without card data, keep what the minion has minus one-shot keywords.
        var baseKeywords = card is not null
            ? Traits.KeywordsFromMechanics(mechanics)
            : keywords & (Keyword.Taunt | Keyword.Poisonous | Keyword.Windfury | Keyword.MegaWindfury);
        return new SimMinion(
            e.Attack,
            e.Health,
            e.Has(GameTag.Taunt),
            e.Has(GameTag.DivineShield),
            e.Has(GameTag.Poisonous),
            e.Has(GameTag.Venomous),
            e.Has(GameTag.Reborn),
            Traits.AttacksPerTurn(keywords),
            e.Has(GameTag.Stealth),
            card is not null ? card.Attack : e.Attack,
            unsimulated)
        {
            CardId = cardId,
            Golden = golden,
            EntityId = e.Id,
            Tribes = Traits.ParseTribes(card?.Races) | TribeTag(e.GetString(GameTag.CardRace)),
            MaxHealth = e.GetInt(GameTag.Health),
            BaseHealth = card?.Health ?? 0,
            BaseKeywords = baseKeywords,
            Mechanics = Traits.MechanicsFrom(mechanics) | Traits.MechanicsFrom(GrantableTags.Where(e.Has)),
            TechLevel = e.GetInt(GameTag.TechLevel) is > 0 and var tier ? tier : card?.TechLevel ?? 0,
            ScriptData1 = e.GetInt(GameTag.ScriptData1),
            ScriptData2 = e.GetInt(GameTag.ScriptData2),
            Enchantments = attached,
            Remembered = _removedBy.TryGetValue(e.Id, out var removed) ? removed.ToArray() : Array.Empty<SimMinion>(),
        };
    }

    private static Keyword KeywordsOf(Entity e) =>
        (e.Has(GameTag.Taunt) ? Keyword.Taunt : 0)
        | (e.Has(GameTag.DivineShield) ? Keyword.DivineShield : 0)
        | (e.Has(GameTag.Poisonous) ? Keyword.Poisonous : 0)
        | (e.Has(GameTag.Venomous) ? Keyword.Venomous : 0)
        | (e.Has(GameTag.Reborn) ? Keyword.Reborn : 0)
        | (e.Has(GameTag.Windfury) ? Keyword.Windfury : 0)
        | (e.Has(GameTag.MegaWindfury) ? Keyword.MegaWindfury : 0)
        | (e.Has(GameTag.Stealth) ? Keyword.Stealth : 0);

    /// <summary>CARDRACE as Power.log prints it: a name ("BEAST") or, for races the client does not name, a number.</summary>
    private static Tribe TribeTag(string value) =>
        int.TryParse(value, CultureInfo.InvariantCulture, out var id) ? Traits.ParseTribe(Races.ById.GetValueOrDefault(id)) : Traits.ParseTribe(value);

    private BgMinion ToMinion(Entity e)
    {
        var keywords = KeywordTags.Where(k => e.Has(k.Tag)).Select(k => k.Label).ToList();
        var card = Cards.Get(e.CardId);
        var cost = e.GetInt(GameTag.Cost);
        if (cost <= 0 && card?.Cost is > 0)
        {
            cost = card.Cost;
        }

        return new BgMinion(
            e.CardId,
            Cards.NameOr(e.CardId, e.Name),
            e.Attack,
            e.Health,
            e.GetInt(GameTag.Premium) > 0,
            e.GetInt(GameTag.TechLevel),
            keywords)
        {
            CardType = e.CardType is { Length: > 0 } type ? type : card?.Type ?? "MINION",
            Cost = cost,
        };
    }

    /// <summary>What the last combat simulation was given (for tests and diagnostics).</summary>
    internal CombatCapture? LastCapture { get; private set; }
}
