using Hearthpulse.Core.Cards;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Main.Share;
#else
namespace Hearthpulse.UI.Main.Share;
#endif

/// <summary>
/// Fixed Warrior decks for the share pictures' render scenes (<c>--render-ui</c>, group <c>sharecards</c>): 21 rows as
/// on the owner's screenshot (TASK-2026-11-09-deck-cover), 30 different cards with the longest names, and 15 rows.
/// Cards missing from the card data are left out.
/// </summary>
public static class ShareSampleDecks
{
    /// <summary>The 21-row deck: card id and copies, long legendary names among them.</summary>
    private static readonly (string Id, int Copies)[] TwentyOne =
    [
        ("END_020", 2), ("CORE_EX1_391", 2), ("CATA_585", 1), ("BE_572", 2), ("CORE_EX1_606", 2), ("FIR_929", 2),
        ("TIME_750", 2), ("BE_561", 2), ("BE_055", 1), ("BE_861", 2), ("EDR_856", 1), ("TIME_715", 2), ("CATA_581", 1),
        ("BE_557", 1), ("TIME_850", 1), ("JAIL_384", 1), ("BE_017", 1), ("JAIL_852", 1), ("GDB_131", 1), ("BAR_025", 1),
        ("CATA_580", 2),
    ];

    /// <summary>The decks by their row count: 21, 30 and 15.</summary>
    public static IEnumerable<(int Rows, IReadOnlyDictionary<string, int> Deck)> All(CardDb cards)
    {
        ArgumentNullException.ThrowIfNull(cards);
        var known = TwentyOne.Where(c => cards.Get(c.Id) is not null).ToList();
        yield return (21, known.ToDictionary(c => c.Id, c => c.Copies));

        var longest = cards.All
            .Where(c => c.Collectible && c.DbfId > 0 && c.Set is not ("VANILLA" or "CORE" or "LEGACY") && c.CardClass is "WARRIOR" or "NEUTRAL"
                && c.Type is "MINION" or "SPELL" or "WEAPON")
            .OrderByDescending(c => c.Name.Length)
            .ThenBy(c => c.Id, StringComparer.Ordinal)
            .DistinctBy(c => c.Name, StringComparer.Ordinal)
            .Take(30);
        yield return (30, longest.ToDictionary(c => c.Id, _ => 1));

        yield return (15, known.Take(15).ToDictionary(c => c.Id, c => c.Copies));
    }
}
