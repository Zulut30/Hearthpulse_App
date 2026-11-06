using System.Globalization;
using System.Text;
using Hearthpulse.Core.Constructed;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;

namespace Hearthpulse.Core.Share;

/// <summary>
/// Chapters for a YouTube description (proposal C2): one line per game, its start measured from the recording's start,
/// "00:00 Game 1 — Paladin vs Shaman (win)". YouTube turns such lines into chapters when the first is 00:00, so a game
/// that starts later gets an "Intro" line at 00:00 before it.
/// </summary>
public static class YouTubeTimestamps
{
    /// <summary>The lead-in used when the player has not set one: the recording starts this long before the first game.</summary>
    public const int DefaultLeadInSeconds = 30;

    /// <summary>The longest lead-in the setting takes.</summary>
    public const int MaxLeadInSeconds = 3600;

    /// <summary>The recording start the window offers: the first game's start minus the lead-in.</summary>
    public static DateTime DefaultStart(IReadOnlyList<GameRecord> games, int leadInSeconds)
    {
        ArgumentNullException.ThrowIfNull(games);
        var first = games.Count == 0 ? DateTime.Now : games.Min(g => g.StartedAt);
        return first.AddSeconds(-Math.Clamp(leadInSeconds, 0, MaxLeadInSeconds));
    }

    /// <summary>
    /// The text to paste: the games that started at or after <paramref name="recordingStart"/>, oldest first, numbered
    /// from 1. Empty when there is none.
    /// </summary>
    /// <param name="heroName">Display name of a Battlegrounds hero card id.</param>
    public static string Build(IEnumerable<GameRecord> games, DateTime recordingStart, Func<string, string>? heroName = null)
    {
        ArgumentNullException.ThrowIfNull(games);
        var lines = new StringBuilder();
        var shown = games.Where(g => g.StartedAt >= recordingStart).OrderBy(g => g.StartedAt).ToList();
        if (shown.Count == 0)
        {
            return string.Empty;
        }

        var longest = shown[^1].StartedAt - recordingStart;
        if (shown[0].StartedAt - recordingStart >= TimeSpan.FromSeconds(1))
        {
            lines.Append(Offset(TimeSpan.Zero, longest)).Append(' ').AppendLine(Loc.T("Growth.Timestamps.Intro"));
        }

        for (var i = 0; i < shown.Count; i++)
        {
            lines.Append(Offset(shown[i].StartedAt - recordingStart, longest))
                .Append(' ')
                .AppendLine(Loc.F("Growth.Timestamps.Game", i + 1, Describe(shown[i], heroName)));
        }

        return lines.ToString().TrimEnd();
    }

    /// <summary>"mm:ss" while the video is under an hour, else "h:mm:ss" on every line (YouTube reads both).</summary>
    public static string Offset(TimeSpan offset, TimeSpan longest)
    {
        var seconds = (long)Math.Max(0, Math.Floor(offset.TotalSeconds));
        var (h, m, s) = (seconds / 3600, seconds / 60 % 60, seconds % 60);
        return longest >= TimeSpan.FromHours(1)
            ? string.Create(CultureInfo.InvariantCulture, $"{h}:{m:00}:{s:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{(h * 60) + m:00}:{s:00}");
    }

    /// <summary>"Paladin vs Shaman (win)", or for Battlegrounds "Hero (place 3)".</summary>
    public static string Describe(GameRecord game, Func<string, string>? heroName = null)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (game.IsBattlegrounds)
        {
            var hero = string.IsNullOrEmpty(game.HeroCardId) ? Loc.T("Growth.Timestamps.Battlegrounds") : heroName?.Invoke(game.HeroCardId) ?? game.HeroCardId;
            return game.Placement > 0 ? Loc.F("Growth.Timestamps.WithPlace", hero, game.Placement) : hero;
        }

        var versus = Loc.F("Growth.Timestamps.Versus", ClassOrUnknown(game.PlayerClass), ClassOrUnknown(game.OpponentClass));
        var result = game.Result switch
        {
            "Win" => Loc.T("Growth.Timestamps.Win"),
            "Loss" => Loc.T("Growth.Timestamps.Loss"),
            "Tie" => Loc.T("Growth.Timestamps.Tie"),
            _ => null,
        };
        return result is null ? versus : Loc.F("Growth.Timestamps.WithResult", versus, result);
    }

    private static string ClassOrUnknown(string? heroClass)
    {
        var name = ConstructedMeta.ClassName(heroClass);
        return string.IsNullOrEmpty(name) ? Loc.T("Growth.Timestamps.UnknownClass") : name;
    }
}
