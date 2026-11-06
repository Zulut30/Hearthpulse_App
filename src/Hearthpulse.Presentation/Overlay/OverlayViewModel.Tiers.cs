using System.Globalization;
#if HEARTHPULSE_WPF
using Hearthpulse.App.Infrastructure;
#else
using Hearthpulse.UI.Infrastructure;
#endif
using Hearthpulse.Core;
using Hearthpulse.Core.Battlegrounds;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Tracking;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Overlay;
#else
namespace Hearthpulse.UI.Overlay;
#endif

public sealed partial class OverlayViewModel
{
    private void RebuildTiers()
    {
        if (_bg is not { } bg)
        {
            return;
        }

        var max = Math.Max(6, bg.MaxTechLevel);
        // Runs on every Battlegrounds snapshot: only a button whose state moved is replaced.
        Sync(ref _tiers, nameof(Tiers), Enumerable.Range(1, max)
            .Select(t => new TierButton(t, t == bg.TechLevel, t == SelectedTier, t > 6 && bg.MaxTechLevel < 7))
            .ToList());
    }

    private void RebuildBrowser()
    {
        UpdateBrowserHeading();
        if (_bg is not { } bg || SelectedTier == 0)
        {
            Browser = Array.Empty<BrowserGroup>();
            return;
        }

        _fingerprints["BrowserRaces"] = string.Join(',', bg.Races);
        var lobby = bg.Races.ToHashSet(StringComparer.Ordinal);

        var minions = _cards.BattlegroundsMinions
            .Where(c => c.TechLevel == SelectedTier && (bg.IsDuos || !c.IsBattlegroundsDuosExclusive))
            .Where(c => lobby.Count == 0 || c.Races.Count == 0 || c.Races.Any(r => r == "ALL" || lobby.Contains(r)))
            .Where(c => InChosenTribe(c.Races))
            .ToList();

        // Before the lobby is known, show every tribe in the pool.
        var order = lobby.Count > 0
            ? bg.Races.ToList()
            : minions.SelectMany(c => c.Races).Where(r => r != "ALL").Distinct().Order(StringComparer.Ordinal).ToList();
        var known = order.ToHashSet(StringComparer.Ordinal);
        var groups = new List<BrowserGroup>();
        foreach (var race in order.Append("ALL").Append(string.Empty))
        {
            var cards = minions
                .Where(c => race.Length == 0 ? c.Races.Count == 0 : race == "ALL" ? c.Races.Contains("ALL") : c.Races.Contains(race) && !c.Races.Contains("ALL"))
                .Where(c => race.Length == 0 || race == "ALL" || c.Races.First(known.Contains) == race)
                .OrderBy(c => c.Name, StringComparer.CurrentCulture)
                .Select(c => new BrowserCard(c.Id, c.Name))
                .ToList();
            if (cards.Count > 0)
            {
                var title = race.Length == 0 ? Loc.T("Overlay.Browser.Neutral") : race == "ALL" ? Loc.T("Overlay.Browser.AllTribes") : Races.Display(race, _locale);
                groups.Add(new BrowserGroup(title, cards));
            }
        }

        // A tribe chosen on the bar shows its minions only; tavern spells belong to no tribe.
        var spells = _cards.BattlegroundsSpells.Where(c => c.TechLevel == SelectedTier && _selectedTribe.Length == 0).OrderBy(c => c.Name, StringComparer.CurrentCulture)
            .Select(c => new BrowserCard(c.Id, c.Name)).ToList();
        if (spells.Count > 0)
        {
            groups.Add(new BrowserGroup(Loc.T("Overlay.Browser.TavernSpells"), spells));
        }

        Browser = groups;
    }
}
