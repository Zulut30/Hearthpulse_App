using System.Globalization;
using Hearthpulse.Core.Battlegrounds;
using Hearthpulse.Core.Battlegrounds.Effects;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Memory;
using Hearthpulse.Core.Power;

namespace Hearthpulse.Core.Tracking;

/// <summary>The Battlegrounds view built from the game: lobby, boards, offers, trinkets and Discover options.</summary>
public sealed partial class BattlegroundsTracker
{
    public BattlegroundsView BuildView(GameState game)
    {
        ApplyRefinedOdds();
        EndStaleCombat(game);
        // Combat as the player sees it: from the server resolving it until the client finishes playing it.
        // If the client never sent the closing BOARD_VISUAL_STATE line (build-dependent), the latch is released
        // by the next round's shop or by the timeout so the overlay cannot stay stuck on Combat in the tavern.
        var combat = _inCombat || (game.CombatOnScreen && !CombatLatchStale(game));
        var localHero = game.LocalHero;
        var heroChosen = localHero is not null && localHero.CardId != PlaceholderHero && !localHero.InHand;
        var options = heroChosen
            ? new List<BgHeroOption>()
            : game.Controlled(game.LocalPlayerId, "HAND")
                .Where(e => e.IsHero && e.CardId.Length > 0 && e.CardId != PlaceholderHero)
                .OrderBy(e => e.ZonePosition)
                .Select(e => new BgHeroOption(
                    e.CardId,
                    Cards.NameOr(e.CardId, e.Name),
                    e.GetInt(GameTag.Armor) is > 0 and var a ? a : Cards.Get(e.CardId)?.Armor ?? 0,
                    HeroStatsProvider?.Invoke(Cards.BaseHeroId(e.CardId))))
                .ToList();

        var nextOpponent = localHero?.GetInt(GameTag.NextOpponentPlayerId) ?? 0;
        if (nextOpponent == 0)
        {
            nextOpponent = game.LocalPlayerEntity?.GetInt(GameTag.NextOpponentPlayerId) ?? 0;
        }

        var techLevel = game.LocalPlayerEntity?.GetInt(GameTag.PlayerTechLevel) ?? 0;
        if (techLevel == 0)
        {
            techLevel = localHero?.GetInt(GameTag.PlayerTechLevel) ?? 0;
        }

        var localBgId = localHero?.GetInt(GameTag.PlayerId) ?? 0;
        var opponents = LeaderboardHeroes(game)
            .Where(h => h.GetInt(GameTag.PlayerId) != localBgId)
            .Select(h =>
            {
                var playerId = h.GetInt(GameTag.PlayerId);
                _boards.TryGetValue(playerId, out var seen);
                var last = _combats.LastOrDefault(c => c.OpponentPlayerId == playerId);
                return new BgOpponent(
                    playerId,
                    h.CardId,
                    Cards.NameOr(h.CardId, h.Name),
                    h.Health,
                    h.GetInt(GameTag.Armor),
                    h.GetInt(GameTag.PlayerTechLevel),
                    h.GetInt(GameTag.PlayerLeaderboardPlace),
                    h.Health <= 0,
                    h.GetInt(GameTag.PlayerTriples),
                    seen?.Minions ?? Array.Empty<BgMinion>(),
                    seen?.Turn ?? 0,
                    last?.Result,
                    last is null ? 0 : last.Result == "Loss" ? last.DamageTaken : last.DamageDealt,
                    playerId == nextOpponent);
            })
            .OrderBy(o => o.Place == 0 ? 99 : o.Place)
            .ToList();

        var player = game.LocalPlayerEntity;
        var darkGift = game.Controlled(game.LocalPlayerId, "PLAY").FirstOrDefault(e => e.CardId.Contains("DarkGift", StringComparison.Ordinal));
        var anomalyDbf = game.GameEntity?.GetInt(GameTag.BaconAnomaly) ?? 0;
        var maxTier = game.LocalPlayerEntity?.GetInt(GameTag.BaconMaxTechLevel) ?? 0;

        // During combat the tavern is gone and the boards are copies, so counters keep what was visible before the fight.
        if (!combat && heroChosen)
        {
            _lastVisibleCards = ShopCards(game)
                .Concat(game.Controlled(game.LocalPlayerId, "HAND"))
                .Concat(game.Controlled(game.LocalPlayerId, "PLAY"))
                .Where(e => e.CardId.Length > 0)
                .Select(e => e.CardId)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        return new BattlegroundsView(
            game.BattlegroundsTurn,
            Math.Max(1, techLevel),
            maxTier > 0 ? maxTier : 6,
            heroChosen ? localHero!.CardId : null,
            localHero?.Health ?? 0,
            localHero?.GetInt(GameTag.Armor) ?? 0,
            localHero?.GetInt(GameTag.PlayerLeaderboardPlace) ?? 0,
            game.IsDuos,
            combat,
            !heroChosen && options.Count > 0,
            options,
            MemoryRaces ?? _shopRaces.Order(StringComparer.Ordinal).ToList(),
            MemoryRaces is not null,
            opponents,
            _combats.ToList(),
            _tiers.ToList(),
            _inCombat ? _lastOwnBoard : Minions(game, game.LocalPlayerId).Select(ToMinion).ToList(),
            anomalyDbf > 0 ? Cards.Get(anomalyDbf)?.Id : null,
            game.LocalPlayerEntity?.GetInt(GameTag.PlayerTriples) ?? 0,
            // Outside combat the opponent slot holds Bob's current offer (minions and tavern spells).
            combat || !heroChosen ? Array.Empty<BgMinion>() : ShopCards(game).Select(ToMinion).ToList(),
            game.Controlled(game.LocalPlayerId, "HAND").Where(e => e.IsMinion && e.CardId.Length > 0).Select(e => e.CardId).ToList())
        {
            // A choice offered while the fight is still on screen waits until the player can see it.
            TrinketOptions = heroChosen && !game.CombatOnScreen ? PendingTrinkets() : Array.Empty<BgTrinketOption>(),
            DiscoverOptions = heroChosen && !game.CombatOnScreen ? PendingDiscover(game) : Array.Empty<BgDiscoverOption>(),
            DiscoverSource = _discoverOffer.Count == 0 ? null : DiscoverSourceLabel(_discoverSource),
            CombatOdds = combat ? _combatOdds : null,
            Trinkets = game.Controlled(game.LocalPlayerId, "PLAY").Where(IsTrinket).Select(e => e.CardId).Distinct().ToList(),
            DarkGiftReady = darkGift is not null && darkGift.GetInt("EXHAUSTED") == 0 && !combat,
            DarkGiftCost = darkGift?.GetInt(GameTag.Cost) ?? 0,
            Gold = player is null ? 0 : Math.Max(0, player.GetInt("RESOURCES") + player.GetInt("TEMP_RESOURCES") - player.GetInt("RESOURCES_USED")),
            TavernUpgradeCost = LiveButtonCosts?.Upgrade,
            ButtonCosts = LiveButtonCosts,
            Counters = BattlegroundsCounters.Build(game, Counters, Cards, _lastVisibleCards),
            Choices = _choices.ToList(),
            Plays = _plays.ToList(),
        };
    }

    private static bool IsTrinket(Entity e) =>
        e.CardType == "BATTLEGROUND_TRINKET" || e.CardId.Contains("_MagicItem_", StringComparison.Ordinal);

    private IReadOnlyList<BgTrinketOption> PendingTrinkets()
    {
        // The offer closes once one of the options is taken (moves to play) or combat starts.
        if (_trinketOffer.Count == 0 || _inCombat || _trinketOffer.Any(e => e.Zone is "PLAY" or "GRAVEYARD" or "REMOVEDFROMGAME"))
        {
            _trinketOffer = Array.Empty<Entity>();
            return Array.Empty<BgTrinketOption>();
        }

        return _trinketOffer
            .Select(e => new BgTrinketOption(e.CardId, Cards.NameOr(e.CardId, e.Name)))
            .ToList();
    }

    private IReadOnlyList<BgDiscoverOption> PendingDiscover(GameState game)
    {
        // The choice closes when the player picks (or the pick leaves set-aside) or combat starts.
        if (_discoverOffer.Count == 0 || _inCombat || _discoverOffer.Any(e => e.Zone != "SETASIDE"))
        {
            _discoverOffer = Array.Empty<Entity>();
            return Array.Empty<BgDiscoverOption>();
        }

        return _discoverOffer
            .Select(e =>
            {
                var gift = game.Entities.GetValueOrDefault(e.GetInt("DARK_GIFT_ENTITY"));
                var giftName = gift is null || gift.CardId.Length == 0 ? null : Cards.NameOr(gift.CardId, gift.Name);
                var card = Cards.Get(e.CardId);
                var tier = e.GetInt(GameTag.TechLevel) is > 0 and var t ? t : card?.TechLevel ?? 0;
                var type = card?.Type is { Length: > 0 } cardType ? cardType : e.CardType;
                return new BgDiscoverOption(e.CardId, Cards.NameOr(e.CardId, e.Name), tier, giftName, type);
            })
            .ToList();
    }

    /// <summary>"Kind · name" of what offers the open choice, e.g. "Hero power · Murloc King".</summary>
    private string? DiscoverSourceLabel(Entity? source)
    {
        if (source is null || source.CardId.Length == 0)
        {
            return null;
        }

        var card = Cards.Get(source.CardId);
        var name = card?.Name is { Length: > 0 } n ? n : source.Name;
        if (source.CardId.StartsWith("TB_BaconShop_Triples", StringComparison.Ordinal))
        {
            return Loc.T("Core.Bg.Source.TripleReward");
        }

        if (source.CardId.StartsWith("BG36_MidGameEffect", StringComparison.Ordinal))
        {
            return Loc.T("Core.Bg.Source.DarkGift");
        }

        var kind = Loc.T((card?.Type ?? source.CardType) switch
        {
            "HERO_POWER" => "Core.Bg.Source.HeroPower",
            "MINION" => "Core.Bg.Source.Minion",
            "BATTLEGROUND_TRINKET" => "Core.Bg.Source.Trinket",
            "SPELL" or "BATTLEGROUND_SPELL" => "Core.Bg.Source.Spell",
            _ => "Core.Bg.Source.Discover",
        });
        return $"{kind} · {name}";
    }
}
