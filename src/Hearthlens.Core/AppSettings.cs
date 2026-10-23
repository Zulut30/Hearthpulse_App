using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hearthlens.Core;

/// <summary>How the overlay presents pick suggestions: heroes, trinkets, tavern minions, Discover choices and the game plan.</summary>
public enum PickHints
{
    /// <summary>Marks the single best option (BEST PICK and BUY badges, gold styling, Discover ranks, the "AIM FOR" comp).</summary>
    Highlight,

    /// <summary>The same stats on every option and nothing singled out: the player decides.</summary>
    StatsOnly,

    /// <summary>No hero, trinket, tavern or Discover badges and no game plan.</summary>
    Off,
}

/// <summary>Who sets the game-plan comp that the tavern and Discover scores count cards for.</summary>
public enum CompMode
{
    /// <summary>The player chooses a comp (kept across games); the advisor never picks one by itself.</summary>
    Manual,

    /// <summary>The advisor picks the comp; a comp the player locks overrides it until the next game.</summary>
    Auto,
}

public sealed class AppSettings
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    /// <summary>"auto" follows the installed client language.</summary>
    public string Locale { get; set; } = "auto";

    public string? HearthstonePath { get; set; }

    public double OverlayScale { get; set; } = 1.0;

    public double OverlayOpacity { get; set; } = 1.0;

    public bool OverlayEnabled { get; set; } = true;

    /// <summary>
    /// Draw the overlay with software rendering. Some Windows 10 GPUs and drivers never compose the transparent
    /// layered window in hardware, so the overlay is invisible; this is the workaround (needs a restart).
    /// </summary>
    public bool OverlaySoftwareRendering { get; set; }

    public bool ShowDeckTracker { get; set; } = true;

    public bool ShowOpponentTracker { get; set; } = true;

    public bool ShowDrawChance { get; set; } = true;

    public bool ShowBattlegroundsBar { get; set; } = true;

    public bool ShowOpponentBoards { get; set; } = true;

    /// <summary>How pick suggestions are shown. Settings files written before it held <see cref="ShowHeroPick"/> instead.</summary>
    public PickHints PickHints { get; set; } = PickHints.StatsOnly;

    /// <summary>
    /// Legacy on/off switch for every pick hint, only read from older settings files: <see cref="Migrate"/> turns false
    /// into <see cref="PickHints.Off"/> (true becomes the default) and clears it, so it is never written again.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ShowHeroPick { get; set; }

    /// <summary>Who sets the game-plan comp.</summary>
    public CompMode CompMode { get; set; } = CompMode.Manual;

    /// <summary>Comp id (Firestone) the player chose in <see cref="CompMode.Manual"/>, kept across games; null when none.</summary>
    public string? ChosenCompId { get; set; }

    /// <summary>The tribe the player wants to build around (a race key such as "BEAST"); null for any.</summary>
    public string? CompFocus { get; set; }

    // Filters of the Constructed meta stats (Firestone keys such as "standard", "legend", "past-7").
    public string MetaFormat { get; set; } = "standard";

    public string MetaRank { get; set; } = "all";

    public string MetaPeriod { get; set; } = "past-7";

    // One bool per option of the two choices above, for the settings page's segmented radio buttons. Setting false is
    // ignored: the radio group unchecks the old option after the new one is checked.

    /// <summary>Pick suggestions: "Mark the best".</summary>
    [JsonIgnore]
    public bool PickHintsHighlight { get => PickHints == PickHints.Highlight; set => SetIf(value, () => PickHints = PickHints.Highlight); }

    /// <summary>Pick suggestions: "Stats only".</summary>
    [JsonIgnore]
    public bool PickHintsStatsOnly { get => PickHints == PickHints.StatsOnly; set => SetIf(value, () => PickHints = PickHints.StatsOnly); }

    /// <summary>Pick suggestions: "Off".</summary>
    [JsonIgnore]
    public bool PickHintsOff { get => PickHints == PickHints.Off; set => SetIf(value, () => PickHints = PickHints.Off); }

    /// <summary>Game plan comp: "I choose".</summary>
    [JsonIgnore]
    public bool CompModeManual { get => CompMode == CompMode.Manual; set => SetIf(value, () => CompMode = CompMode.Manual); }

    /// <summary>Game plan comp: "Automatic".</summary>
    [JsonIgnore]
    public bool CompModeAuto { get => CompMode == CompMode.Auto; set => SetIf(value, () => CompMode = CompMode.Auto); }

    public bool ShowGameToasts { get; set; } = true;

    public bool ShowLobbyPanel { get; set; } = true;

    public bool ShowSessionPanel { get; set; } = true;

    /// <summary>Show simulated win / tie / loss odds while a Battlegrounds combat plays out.</summary>
    public bool ShowCombatOdds { get; set; } = true;

    /// <summary>User-placed overlay panels by id; panels without an entry use their default spot.</summary>
    public Dictionary<string, PanelPlacement> Panels { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Fallback leaderboard hover areas, as fractions of the client height (the leaderboard scales with height).
    /// X is measured from the left edge of the 4:3 game area, where Hearthstone draws the leaderboard.
    /// Defaults match Hearthstone's layout; the live hover normally comes from the game's memory instead.
    /// </summary>
    public double LeaderboardTop { get; set; } = DefaultLeaderboardTop;

    public double LeaderboardSlotHeight { get; set; } = DefaultLeaderboardSlotHeight;

    public double LeaderboardWidth { get; set; } = DefaultLeaderboardWidth;

    /// <summary>Bumped when the meaning of the leaderboard fractions changes, so stale calibrations are reset.
    /// Absent from older settings files, so it defaults to 0 and <see cref="Migrate"/> brings it up to date.</summary>
    public int LeaderboardGeometryVersion { get; set; }

    public const double DefaultLeaderboardTop = 0.15;
    public const double DefaultLeaderboardSlotHeight = 0.69 / 8;
    public const double DefaultLeaderboardWidth = 0.10;
    private const int CurrentLeaderboardGeometry = 2;

    public bool ShowLeaderboardGuides { get; set; }

    public bool MinimizeToTray { get; set; } = true;

    public bool StartMinimized { get; set; }

    public bool StartWithWindows { get; set; }

    /// <summary>
    /// Look for a newer release on GitHub in the background, download it and install it when the app next closes.
    /// This is what keeps installed copies up to date without downloading the setup wizard by hand again.
    /// </summary>
    public bool AutoUpdate { get; set; } = true;

    public bool HistoryImported { get; set; }

    /// <summary>Colour theme: a preset name, or "Custom" with the three values below.</summary>
    public string Theme { get; set; } = "Tavern";

    public double ThemeAccentHue { get; set; } = 42;

    public double ThemeBaseHue { get; set; } = 28;

    public double ThemeBaseTint { get; set; } = 1.0;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.Settings))
            {
                return FromJson(File.ReadAllText(AppPaths.Settings));
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            Diag.Error("Settings unreadable, using defaults", ex);
        }

        var fresh = new AppSettings();
        fresh.Migrate();
        return fresh;
    }

    /// <summary>Reads settings saved by this or an older version and brings them up to date.</summary>
    public static AppSettings FromJson(string json)
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(json, Json) ?? new AppSettings();
        settings.Migrate();
        return settings;
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    private static void SetIf(bool value, Action apply)
    {
        if (value)
        {
            apply();
        }
    }

    private void Migrate()
    {
        // One switch used to turn every pick hint on or off: off stays off, on becomes the "stats only" default.
        if (ShowHeroPick is { } showPicks)
        {
            if (!showPicks)
            {
                PickHints = PickHints.Off;
            }

            ShowHeroPick = null;
        }

        // Version 1 measured X from the window edge, which misses the leaderboard on wide screens.
        if (LeaderboardGeometryVersion < CurrentLeaderboardGeometry)
        {
            LeaderboardTop = DefaultLeaderboardTop;
            LeaderboardSlotHeight = DefaultLeaderboardSlotHeight;
            LeaderboardWidth = DefaultLeaderboardWidth;
            LeaderboardGeometryVersion = CurrentLeaderboardGeometry;
        }
    }

    public void Save()
    {
        try
        {
            var temp = AppPaths.Settings + ".tmp";
            File.WriteAllText(temp, ToJson());
            File.Move(temp, AppPaths.Settings, overwrite: true);
        }
        catch (IOException ex)
        {
            Diag.Error("Settings not saved", ex);
        }
    }
}

/// <summary>Top-left corner as fractions of the overlay surface, plus a per-panel size multiplier.</summary>
public sealed class PanelPlacement
{
    public double X { get; set; }

    public double Y { get; set; }

    public double Scale { get; set; } = 1.0;
}
