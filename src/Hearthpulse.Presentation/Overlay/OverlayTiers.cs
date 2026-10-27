using Hearthpulse.Core.Tracking;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Overlay;
#else
namespace Hearthpulse.UI.Overlay;
#endif

/// <summary>
/// Tier letters for overlay numbers that have no letter of their own, on the scales hearthpulse.net uses, so the overlay
/// and the site agree. Arena card and Battlegrounds tiers come from Core (ArenaMeta, ArenaDraft, BattlegroundsAdvisor).
/// </summary>
public static class OverlayTiers
{
    /// <summary>z of a two-sided 90% interval: the site's meta tiers use the lower end of that Wilson interval.</summary>
    private const double MetaZ = 1.6448536;

    /// <summary>
    /// Tier of an archetype's meta win rate as on the site's meta page ("Тиры архетипов"): by the lower bound of the
    /// 90% Wilson interval, S from 53%, A from 51%, B from 49%, C from 47%, else D; so few games rank a tier lower.
    /// Null without games.
    /// </summary>
    public static string? Meta(double winRate, int games)
    {
        if (games <= 0)
        {
            return null;
        }

        var p = Math.Clamp(winRate, 0, 1);
        var z2 = MetaZ * MetaZ;
        var lower = (p + z2 / (2 * games) - MetaZ * Math.Sqrt(p * (1 - p) / games + z2 / (4.0 * games * games))) / (1 + z2 / games);
        return lower switch
        {
            >= 0.53 => "S",
            >= 0.51 => "A",
            >= 0.49 => "B",
            >= 0.47 => "C",
            _ => "D",
        };
    }

    /// <summary>
    /// Tier of keeping a mulligan card, from its edge over a replacement in win rate: S from +3 points, A from +1.5,
    /// B from the close-call margin, C a close call, D a card to replace. Null without numbers.
    /// </summary>
    public static string? Keep(double? edge) => edge switch
    {
        null => null,
        >= 0.03 => "S",
        >= 0.015 => "A",
        >= MulliganGuide.CloseCall => "B",
        > -MulliganGuide.CloseCall => "C",
        _ => "D",
    };
}
