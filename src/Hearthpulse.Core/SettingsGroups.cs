using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hearthpulse.Core;

/// <summary>
/// The player's preferences in groups, as Settings shows them: each group can be reset to its defaults, and every group
/// together is what a settings file carries. What is not in a group stays as it is on reset and import: the history
/// and migration flags, the stats-pool key and consent, game and HDT folders, the open tab and page filters, and the
/// panels' positions (they have their own "reset positions" button).
/// </summary>
public static class SettingsGroups
{
    public const string Appearance = "Appearance";
    public const string General = "General";
    public const string Overlay = "Overlay";
    public const string Leaderboard = "Leaderboard";
    public const string Tracker = "Tracker";
    public const string Stream = "Stream";

    /// <summary>Format marker of an exported file; a newer version's file is still read for the properties it shares.</summary>
    public const string FileMarker = "hearthpulseSettings";

    public const int FileVersion = 1;

    public static IReadOnlyDictionary<string, string[]> Properties { get; } = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        [Appearance] =
        [
            nameof(AppSettings.Theme), nameof(AppSettings.ThemeAccentHue), nameof(AppSettings.ThemeBaseHue), nameof(AppSettings.ThemeBaseTint),
            nameof(AppSettings.ThemeAccent), nameof(AppSettings.PageHeroes), nameof(AppSettings.BackgroundHero), nameof(AppSettings.BackgroundIntensity),
            nameof(AppSettings.CornerStyle), nameof(AppSettings.Density), nameof(AppSettings.InterfaceScale), nameof(AppSettings.ReduceMotion),
        ],
        [General] =
        [
            nameof(AppSettings.Language), nameof(AppSettings.MinimizeToTray), nameof(AppSettings.StartMinimized),
            nameof(AppSettings.StartWithWindows), nameof(AppSettings.AutoUpdate), nameof(AppSettings.ShowGameToasts),
        ],
        [Overlay] =
        [
            nameof(AppSettings.OverlayEnabled), nameof(AppSettings.OverlayScale), nameof(AppSettings.OverlayOpacity),
            nameof(AppSettings.ShowTurnTimer), nameof(AppSettings.ShowOpponentArchetype), nameof(AppSettings.ShowLadderSession),
            nameof(AppSettings.LadderSessionBreakHours), nameof(AppSettings.ShowSecrets), nameof(AppSettings.ShowGraveyard),
            nameof(AppSettings.ShowLikelyDeck), nameof(AppSettings.ShowBattlegroundsBar), nameof(AppSettings.PickHints),
            nameof(AppSettings.ShowOpponentBoards), nameof(AppSettings.ShowLobbyPanel), nameof(AppSettings.ShowBgLobbyPanel),
            nameof(AppSettings.ShowBgLeaderboardBadges), nameof(AppSettings.ShowCombatOdds), nameof(AppSettings.ShowSessionPanel),
            nameof(AppSettings.OverlayPanelsUnlocked), nameof(AppSettings.PanelLooks),
        ],
        [Leaderboard] =
        [
            nameof(AppSettings.LeaderboardTop), nameof(AppSettings.LeaderboardSlotHeight), nameof(AppSettings.LeaderboardWidth),
            nameof(AppSettings.ShowLeaderboardGuides),
        ],
        [Tracker] =
        [
            nameof(AppSettings.ShowDeckTracker), nameof(AppSettings.ShowDrawChance), nameof(AppSettings.ShowOpponentTracker),
            nameof(AppSettings.ShowCardPool), nameof(AppSettings.ShowArenaDraft), nameof(AppSettings.ShowMulligan),
            nameof(AppSettings.ShowCounters), nameof(AppSettings.ShowOpponentCounters), nameof(AppSettings.CounterModes),
        ],
        [Stream] =
        [
            nameof(AppSettings.StreamerMode), nameof(AppSettings.DiscordStatus), nameof(AppSettings.StreamDeckWindow),
            nameof(AppSettings.StreamLobbyWindow), nameof(AppSettings.StreamOddsWindow), nameof(AppSettings.StreamWindowBackground),
            nameof(AppSettings.StreamWindowsOnTop),
        ],
    };

    /// <summary>What a settings file carries besides the groups: where the player put the panels and OBS windows.</summary>
    private static readonly string[] FileExtras =
        [nameof(AppSettings.Panels), nameof(AppSettings.StreamWindows), nameof(AppSettings.CardPoolBrowserWidth)];

    /// <summary>Puts the defaults of every property of <paramref name="group"/> back; false for an unknown group.</summary>
    public static bool Reset(AppSettings settings, string group)
    {
        if (!Properties.TryGetValue(group, out var names))
        {
            return false;
        }

        Copy(new AppSettings(), settings, names);
        return true;
    }

    /// <summary>Every group back to its defaults (positions, account, history and consent stay).</summary>
    public static void ResetAll(AppSettings settings)
    {
        var defaults = new AppSettings();
        foreach (var names in Properties.Values)
        {
            Copy(defaults, settings, names);
        }
    }

    /// <summary>The preferences as a settings file: indented JSON with a format marker.</summary>
    public static string Export(AppSettings settings)
    {
        var all = JsonNode.Parse(settings.ToJson())!.AsObject();
        var picked = new JsonObject();
        foreach (var name in Properties.Values.SelectMany(n => n).Concat(FileExtras))
        {
            if (all[name] is { } value)
            {
                picked[name] = value.DeepClone();
            }
        }

        var file = new JsonObject { [FileMarker] = FileVersion, ["settings"] = picked };
        return file.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Takes the preferences of a settings file into <paramref name="settings"/>; other values stay. Invalid values are
    /// repaired as when the app reads its own file.
    /// </summary>
    /// <returns>False (nothing changed) when the text is not a Hearthpulse settings file.</returns>
    public static bool Import(AppSettings settings, string json)
    {
        JsonObject? body;
        try
        {
            body = JsonNode.Parse(json) is JsonObject { } root && root[FileMarker] is not null ? root["settings"] as JsonObject : null;
        }
        catch (JsonException)
        {
            return false;
        }

        if (body is null)
        {
            return false;
        }

        AppSettings read;
        try
        {
            read = AppSettings.FromJson(body.ToJsonString());
        }
        catch (JsonException)
        {
            return false;
        }

        Copy(read, settings, Properties.Values.SelectMany(n => n).Concat(FileExtras).Where(n => body.ContainsKey(n)));
        return true;
    }

    private static void Copy(AppSettings from, AppSettings to, IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            var property = typeof(AppSettings).GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
                ?? throw new InvalidOperationException("No setting " + name);
            property.SetValue(to, property.GetValue(from));
        }
    }
}
