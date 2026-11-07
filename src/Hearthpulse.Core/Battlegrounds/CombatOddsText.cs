using System.Globalization;
using Hearthpulse.Core.Localization;

namespace Hearthpulse.Core.Battlegrounds;

/// <summary>Short overlay texts for the hero damage of <see cref="CombatOdds"/>, shared by both UIs.</summary>
public static class CombatOddsText
{
    /// <summary>
    /// The damage range of 8 fights in 10 ("8–16", en dash, no sign; one number when both ends meet); empty without a
    /// spread. <paramref name="dealt"/> picks the side only for <see cref="DamageTip"/>, the range reads the same.
    /// </summary>
    public static string Damage(DamageSpread? spread, bool dealt)
    {
        _ = dealt;
        if (spread is null)
        {
            return string.Empty;
        }

        var low = spread.Low.ToString(CultureInfo.CurrentCulture);
        return spread.High > spread.Low ? $"{low}–{spread.High.ToString(CultureInfo.CurrentCulture)}" : low;
    }

    public static string DamageTip(DamageSpread? spread, bool dealt) =>
        spread is null
            ? string.Empty
            : Loc.F(dealt ? "Overlay.Odds.DamageWinTip" : "Overlay.Odds.DamageLossTip", spread.Mean, spread.Low, spread.High, spread.Max);
}
