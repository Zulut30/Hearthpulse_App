using System.Globalization;
using Hearthpulse.Core.Localization;

namespace Hearthpulse.Core.Battlegrounds;

/// <summary>Short overlay texts for the hero damage of <see cref="CombatOdds"/>, shared by both UIs.</summary>
public static class CombatOddsText
{
    /// <summary>"+8 ±2" (damage we deal when we win) or "−6 ±2" (damage we take when we lose); empty without a spread.</summary>
    public static string Damage(DamageSpread? spread, bool dealt)
    {
        if (spread is null)
        {
            return string.Empty;
        }

        var mean = (int)Math.Round(spread.Mean, MidpointRounding.AwayFromZero);
        var spreadHalf = (int)Math.Round((spread.High - spread.Low) / 2.0, MidpointRounding.AwayFromZero);
        var text = (dealt ? "+" : "−") + mean.ToString(CultureInfo.CurrentCulture);
        return spreadHalf > 0 ? $"{text} ±{spreadHalf}" : text;
    }

    public static string DamageTip(DamageSpread? spread, bool dealt) =>
        spread is null
            ? string.Empty
            : Loc.F(dealt ? "Overlay.Odds.DamageWinTip" : "Overlay.Odds.DamageLossTip", spread.Mean, spread.Low, spread.High, spread.Max);
}
