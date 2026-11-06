#if HEARTHPULSE_WPF
using Hearthpulse.App.Infrastructure;
#else
using Hearthpulse.UI.Infrastructure;
#endif
using System.ComponentModel;
using System.Globalization;
using Hearthpulse.Core.Constructed;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Stats;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Main;
#else
namespace Hearthpulse.UI.Main;
#endif

public sealed partial class MainViewModel
{
    /// <summary>The heat map: every archetype of the matrix against every other, rows and columns in the same order.</summary>
    private void RebuildMatchupMatrix(MetaMatchups matchups)
    {
        var archetypes = matchups.Archetypes;
        MatchupHeads = archetypes
            .Select(a => new MatchupHead(Short(a.Name), ClassHero(a.Class), a.Name))
            .ToList();
        MatchupMatrix = archetypes
            .Select(a => new MatchupMatrixRow(
                a.Name,
                ClassHero(a.Class),
                a.WinRate.ToString("P1", CultureInfo.CurrentCulture),
                a.WinRate - 0.5,
                archetypes.Select(o => CellOf(a, o, matchups.Cell(a.Key, o.Key))).ToList()))
            .ToList();
    }

    private static MatchupCellView CellOf(MatchupArchetype archetype, MatchupArchetype opponent, MatchupCell? cell)
    {
        if (cell is null || cell.Games <= 0)
        {
            return new MatchupCellView("—", 0, 0, Loc.F("Main.Meta.MatchupNoGames", archetype.Name, opponent.Name));
        }

        var edge = cell.WinRate - 0.5;
        return new MatchupCellView(
            (cell.WinRate * 100).ToString("0", CultureInfo.CurrentCulture),
            edge,
            // Even a mirror (50 %) gets a faint tint so the grid reads as one; 15 points off even is the full tint.
            Math.Clamp(Math.Abs(edge) / 0.15, 0.1, 1) * 0.6,
            Loc.P("Main.Meta.MatchupTip", cell.Games, archetype.Name, opponent.Name, cell.WinRate.ToString("P1", CultureInfo.CurrentCulture)));
    }

    /// <summary>A column head fits about ten characters: longer names are cut, the tooltip has the whole one.</summary>
    private static string Short(string name) => name.Length <= 11 ? name : name[..10].TrimEnd() + "…";

    private void RebuildArchetypeDetail()
    {
        var archetype = PageMeta?.Archetypes.FirstOrDefault(a => a.Id == _selectedArchetype?.Id);
        if (archetype is null)
        {
            ArchetypeSummary = string.Empty;
            ArchetypeDecks = [];
            _mulliganLoad?.Cancel();
            Mulligan = [];
            MulliganSource = string.Empty;
            return;
        }

        // A source without play-rate data reports a 0 % share: that is left out rather than shown as "0.0 % of the meta".
        ArchetypeSummary = Loc.P(archetype.Share > 0 ? "Main.Meta.Summary" : "Main.Meta.SummaryNoShare", archetype.Games, ConstructedMeta.ClassName(archetype.Class),
            archetype.WinRate.ToString("P1", CultureInfo.CurrentCulture), archetype.Share.ToString("P1", CultureInfo.CurrentCulture));

        ArchetypeDecks = PageMeta!.Decks
            .Where(d => d.ArchetypeId == archetype.Id)
            .OrderByDescending(d => d.Games >= 200 ? 1 : 0)
            .ThenByDescending(d => d.WinRate)
            .Take(5)
            .Select(DeckRowOf)
            .ToList();

        // At a rank other than Legend the list is HSGuru's slice of that rank (MainViewModel.MulliganRank.cs).
        if (ShowRankMulligan(archetype))
        {
            return;
        }

        // Best cards to keep first, by the win rate of games where the card stayed in the opening hand.
        Mulligan = archetype.Mulligan
            .Take(12)
            .Select(c => new MulliganRow(
                c.CardId,
                _cards.NameOf(c.CardId),
                c.InHandWinRate.ToString("P0", CultureInfo.CurrentCulture),
                c.InHandWinRate - (archetype.MulliganBaseline ?? archetype.WinRate),
                Loc.F("Main.Meta.Kept", c.KeepRate.ToString("P0", CultureInfo.CurrentCulture)),
                Loc.P("Main.Meta.SeenIn", c.InHand)))
            .ToList();
    }

    private MetaDeckRow DeckRowOf(MetaDeck d) => new(
        d.Archetype,
        ConstructedMeta.ClassName(d.Class),
        ClassHero(d.Class),
        d.WinRate.ToString("P1", CultureInfo.CurrentCulture),
        d.WinRate - 0.5,
        d.Games.ToString("N0", CultureInfo.CurrentCulture),
        DustOf(d.DeckString) is var dust and > 0 ? dust.ToString("N0", CultureInfo.CurrentCulture) : "—",
        d.DeckString);

    /// <summary>Dust to craft every card of the deck; Core set cards are free for everyone.</summary>
    private int DustOf(string deckCode)
    {
        if (Hearthpulse.Core.Cards.DeckString.TryDecode(deckCode) is not { } deck)
        {
            return 0;
        }

        var dust = 0;
        foreach (var (dbf, count) in deck.Cards)
        {
            if (_cards.Get(dbf) is not { } card || card.Set == "CORE")
            {
                continue;
            }

            dust += count * card.Rarity switch
            {
                "COMMON" => 40,
                "RARE" => 100,
                "EPIC" => 400,
                "LEGENDARY" => 1600,
                _ => 0,
            };
        }

        return dust;
    }
}
