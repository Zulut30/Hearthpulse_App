using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Tracking;

namespace Hearthpulse.Core.Battlegrounds;

/// <summary>Which comps the lobby and the patch allow and how they rank for this hero, with the reason shown.</summary>
public sealed partial class BattlegroundsAdvisor
{
    /// <summary>
    /// A comp is worth showing when at least one of its cards is in this patch's pool (and, outside Duos, is not a
    /// Duos-exclusive card). Comps built only from cards the patch rotated out cannot be played.
    /// </summary>
    private static bool Playable(MetaComp comp, CardDb cards, bool duos) =>
        comp.Cards.Count == 0 || comp.Cards.Any(card => Available(card.CardId, cards, duos));

    private static bool Available(string cardId, CardDb cards, bool duos) =>
        cards.InBattlegroundsPool(cardId)
        && (duos || cards.Get(cardId)?.IsBattlegroundsDuosExclusive != true);

    private static double HeroCompPlacement(MetaComp comp, string baseHeroId, int bracket)
    {
        var general = comp.PlacementAtMmr.TryGetValue(bracket, out var p) ? p : comp.AveragePlacement;
        if (comp.HeroPlacement.TryGetValue(baseHeroId, out var hero) && hero.DataPoints >= 40)
        {
            // Shrink small hero samples toward the comp's general placement.
            var weight = Math.Min(1.0, hero.DataPoints / 200.0);
            return (hero.Placement * weight) + (general * (1 - weight));
        }

        return general;
    }

    private IReadOnlyList<CompAdvice> RankComps(
        BattlegroundsView view, List<MetaComp> viable, string baseHero, int bracket, Dictionary<string, int> owned, CardDb cards, bool lobbyKnown)
    {
        var focus = CompFocus is { Length: > 0 } wanted && view.Races.Contains(wanted) ? wanted : null;
        var ranked = viable
            .Select(c =>
            {
                var core = c.Cards.Count(card => card.Status == "CORE");
                var ownedCore = c.Cards.Count(card => card.Status == "CORE" && owned.ContainsKey(card.CardId));
                var ownedAddon = c.Cards.Count(card => card.Status != "CORE" && owned.ContainsKey(card.CardId));
                var placement = HeroCompPlacement(c, baseHero, bracket);
                // Most comps sit within a few tenths of a place of each other, so what you already own has to weigh more
                // than that gap: half of a comp's core moves it past almost any other, and the list follows your board.
                var fit = core == 0 ? 0 : ownedCore / (double)core;
                var focused = focus is null ? 0 : c.Tribes.Contains(focus) ? -1.0 : 0.5;
                return (Comp: c, Placement: placement, OwnedCore: ownedCore, Weight: (ownedCore * 2) + ownedAddon,
                    Score: placement - (0.35 * ownedCore) - (0.1 * ownedAddon) - (0.6 * fit) + focused);
            })
            .OrderBy(x => x.Score)
            .ToList();

        if (ranked.Count == 0)
        {
            return Array.Empty<CompAdvice>();
        }

        // The player's choice comes first. In Manual mode it is the only possible target: a comp this lobby cannot
        // play (or no choice at all) leaves no target, and the choice itself is kept for later games.
        var target = ranked.FirstOrDefault(x => x.Comp.Id == PinnedCompId);
        if (CompMode == CompMode.Manual)
        {
            _autoTargetId = null;
        }
        else
        {
            if (target.Comp is null)
            {
                target = ranked.Where(x => x.Weight >= 2 && x.Comp.Cards.Count > 0).OrderByDescending(x => x.Weight).ThenBy(x => x.Score).FirstOrDefault();
            }

            if (target.Comp is null)
            {
                target = ranked.FirstOrDefault(x => x.Comp.Cards.Count > 0);
            }

            // Stay on the previous suggestion unless the new one is ahead by at least one core card.
            var previous = ranked.FirstOrDefault(x => x.Comp.Id == _autoTargetId);
            if (PinnedCompId is null && previous.Comp is not null && target.Comp is not null && previous.Comp.Id != target.Comp.Id
                && target.Weight - previous.Weight < 2)
            {
                target = previous;
            }

            if (PinnedCompId is null)
            {
                _autoTargetId = target.Comp?.Id;
            }
        }

        // At most two comps of the same tribes unless you already own part of them, so the list offers real choices
        // instead of five versions of the strongest tribe.
        var shown = ranked.Take(0).ToList();
        var perTribes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var x in ranked)
        {
            var compTribes = x.Comp.Tribes;
            if (compTribes.Count == 0 && BattlegroundsMeta.TribeForComp(x.Comp.Id) is { } singleTribe)
            {
                compTribes = [singleTribe];
            }

            var key = string.Join('+', compTribes.Order(StringComparer.Ordinal));
            perTribes.TryGetValue(key, out var count);
            if (count >= 2 && x.Weight == 0)
            {
                continue;
            }

            perTribes[key] = count + 1;
            shown.Add(x);
            if (shown.Count == ShownComps)
            {
                break;
            }
        }

        if (target.Comp is not null && !shown.Exists(x => x.Comp.Id == target.Comp.Id))
        {
            shown[^1] = target;
        }

        return shown.Select(x => new CompAdvice(
                x.Comp.Id,
                x.Comp.Name,
                x.Comp.PowerLevel,
                x.Comp.Difficulty,
                x.Comp.PlacementAtMmr.TryGetValue(bracket, out var p) ? p : x.Comp.AveragePlacement,
                x.Comp.HeroPlacement.TryGetValue(baseHero, out var h) && h.DataPoints >= 40 ? h.Placement : null,
                x.Comp.Tribes,
                x.Comp.Cards
                    .Where(card => Available(card.CardId, cards, view.IsDuos))
                    .Select(card => new CompCard(card.CardId, cards.NameOf(card.CardId), card.Status, cards.Get(card.CardId)?.TechLevel ?? 0, owned.ContainsKey(card.CardId)))
                    .OrderBy(card => card.Status switch { "CORE" => 0, "ADDON" => 1, _ => 2 }).ThenBy(card => card.TechLevel)
                    .ToList(),
                x.Comp.WhenToCommit,
                x.Comp.Tip,
                x.OwnedCore,
                target.Comp is not null && x.Comp.Id == target.Comp.Id,
                x.Comp.Id == PinnedCompId)
            {
                Why = WhyComp(x.Comp, x.OwnedCore, x.Comp.Id == PinnedCompId, CompMode, lobbyKnown, owned, baseHero),
                LookFor = x.Comp.Cards
                    .Where(card => card.Status == "CORE" && !owned.ContainsKey(card.CardId))
                    .Where(card => Available(card.CardId, cards, view.IsDuos))
                    .Select(card => (Name: cards.NameOf(card.CardId), Tier: cards.Get(card.CardId)?.TechLevel ?? 0))
                    .Where(card => card.Name.Length > 0 && card.Tier > 0 && card.Tier <= Math.Max(view.TechLevel, 1) + 1)
                    .OrderBy(card => card.Tier)
                    .Take(3)
                    .Select(card => Loc.F("Core.Bg.CardTier", card.Name, card.Tier))
                    .ToList(),
            })
            .ToList();
    }

    private static string WhyComp(MetaComp comp, int ownedCore, bool pinned, CompMode mode, bool lobbyKnown, Dictionary<string, int> owned, string baseHero)
    {
        if (pinned)
        {
            return mode == CompMode.Manual
                ? Loc.T("Core.Bg.Why.Choice")
                : Loc.T("Core.Bg.Why.Locked");
        }

        var core = comp.Cards.Count(card => card.Status == "CORE");
        var addons = comp.Cards.Count(card => card.Status != "CORE" && owned.ContainsKey(card.CardId));
        if (ownedCore > 0)
        {
            return Loc.F("Core.Bg.Why.Have", ownedCore, core) + (addons > 0 ? Loc.P("Core.Bg.Why.AndSupport", addons) : string.Empty);
        }

        if (addons > 0)
        {
            return Loc.F("Core.Bg.Why.HaveSupport", addons);
        }

        var hero = comp.HeroPlacement.TryGetValue(baseHero, out var h) && h.DataPoints >= 40 ? Loc.T("Core.Bg.Why.WithHero") : string.Empty;
        return lobbyKnown
            ? Loc.F("Core.Bg.Why.BestLobby", hero)
            : Loc.F("Core.Bg.Why.BestUnknownLobby", hero);
    }
}
