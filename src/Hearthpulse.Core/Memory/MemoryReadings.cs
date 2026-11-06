using System.Globalization;

namespace Hearthpulse.Core.Memory;

public sealed record BattlegroundsRating(int Solo, int Duos);

/// <summary>
/// Live gold costs of the Battlegrounds game-mode buttons. Null when that button is not on screen;
/// the costs include hero-power and trinket discounts.
/// </summary>
public sealed record BattlegroundsButtonCosts(int? Freeze, int? Roll, int? Upgrade, int? DarkGift);

public sealed record RankInfo(string Format, int LeagueId, int StarLevel, int LegendRank, int SeasonId)
{
    private static readonly string[] Leagues = ["Bronze", "Silver", "Gold", "Platinum", "Diamond"];

    /// <summary>The star level the client gives Legend (Bronze 10 = 1 ... Diamond 1 = 50); it counts on past it.</summary>
    public const int LegendStarLevel = 51;

    public bool IsLegend => LegendRank > 0 || StarLevel >= LegendStarLevel;

    /// <summary>
    /// Rank as stored in the game history, culture-independent: "Diamond 4", "Legend 1,204" (invariant digit grouping).
    /// </summary>
    public string Text => FormatIn(CultureInfo.InvariantCulture);

    /// <summary>
    /// Rank for the UI in the interface language, the legend number grouped for the current culture ("Legend 1 204" on
    /// ru-RU, "Легенда 1 204" in Russian).
    /// </summary>
    public string Display => Translate(FormatIn(CultureInfo.CurrentCulture))!;

    private string FormatIn(IFormatProvider culture)
    {
        if (IsLegend)
        {
            return LegendRank > 0 ? string.Format(culture, "Legend {0:N0}", LegendRank) : "Legend";
        }

        if (StarLevel <= 0)
        {
            return "Unranked";
        }

        var league = Leagues[Math.Clamp((StarLevel - 1) / 10, 0, Leagues.Length - 1)];
        var rank = 10 - ((StarLevel - 1) % 10);
        return $"{league} {rank}";
    }

    /// <summary>
    /// Saved rank text in its stored, culture-independent form. Older builds formatted the legend number with the
    /// user's culture ("Legend 1 204" with a no-break space, "Legend 1.204"); those are rewritten as "Legend 1,204".
    /// </summary>
    public static string? Normalize(string? text)
    {
        if (LegendNumber(text) is not { } legend)
        {
            return text;
        }

        return string.Format(CultureInfo.InvariantCulture, "Legend {0:N0}", legend);
    }

    /// <summary>Saved rank text for the UI: the legend number grouped for <paramref name="culture"/> (current by default).</summary>
    public static string? Localize(string? text, CultureInfo? culture = null) =>
        Translate(LegendNumber(text) is { } legend ? string.Format(culture ?? CultureInfo.CurrentCulture, "Legend {0:N0}", legend) : text);

    /// <summary>
    /// The league word of an English rank text ("Gold 4", "Legend 1 204", "Unranked") in the interface language; the
    /// stored text stays English because <see cref="Stats.ConstructedStats"/> parses it.
    /// </summary>
    private static string? Translate(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var space = text.IndexOf(' ', StringComparison.Ordinal);
        var key = (space < 0 ? text : text[..space]) switch
        {
            "Bronze" => "Core.Rank.Bronze",
            "Silver" => "Core.Rank.Silver",
            "Gold" => "Core.Rank.Gold",
            "Platinum" => "Core.Rank.Platinum",
            "Diamond" => "Core.Rank.Diamond",
            "Legend" => "Core.Rank.Legend",
            "Unranked" => "Core.Rank.Unranked",
            _ => null,
        };
        return key is null ? text : Localization.Loc.T(key) + (space < 0 ? string.Empty : text[space..]);
    }

    /// <summary>The number of a saved "Legend N" text, whatever digit grouping it was written with.</summary>
    private static int? LegendNumber(string? text)
    {
        if (text is null || !text.StartsWith("Legend ", StringComparison.Ordinal))
        {
            return null;
        }

        var digits = new string(text.AsSpan(7).ToArray().Where(char.IsAsciiDigit).ToArray());
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var legend) && legend > 0 ? legend : null;
    }
}

public sealed record MatchPlayerInfo(string? Name, RankInfo? Standard, RankInfo? Wild, RankInfo? Classic, RankInfo? Twist);
