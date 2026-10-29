using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Hearthpulse.Core;

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
    private string? _unpreservedFile;

    /// <summary>
    /// Card data language (HearthstoneJSON locale such as "ruRU"); "auto" decides by <see cref="CardLocale"/>.
    /// </summary>
    public string Locale { get; set; } = "auto";

    /// <summary>Interface language: "en", "ru", or "auto" for the operating system's language (English when it is neither).</summary>
    public string Language { get; set; } = Localization.Loc.Auto;

    /// <summary>The Settings page tab shown last ("General", "Overlay", "Tracker", "Account", "Data", "About").</summary>
    public string SettingsTab { get; set; } = "General";

    /// <summary>
    /// Text locale of the installed Hearthstone client ("ruRU", "deDE"...; <c>HearthstoneInstall.DetectLocale</c>), set by
    /// the hosts once they found the install; null until then.
    /// </summary>
    [JsonIgnore]
    public string? ClientLocale { get; set; }

    /// <summary>
    /// The card locale to load: <see cref="Locale"/> when set by hand; else the chosen interface language's ("ru" ->
    /// ruRU, "en" -> enUS); with the language on "auto", the installed client's language, as before the interface had
    /// languages (a German client keeps German cards), or the interface language while the client is unknown.
    /// </summary>
    public string CardLocale() =>
        Locale is { Length: > 0 } locale && locale != "auto" ? locale
        : Language is Localization.Loc.English or Localization.Loc.Russian ? Localization.Loc.CardLocaleOf(Language)
        : ClientLocale ?? Localization.Loc.CardLocaleOf(InterfaceLanguage());

    /// <summary>The interface language to show: <see cref="Language"/>, with "auto" resolved by the client and the system.</summary>
    public string InterfaceLanguage() => Localization.Loc.Resolve(Language, clientLocale: ClientLocale);

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

    /// <summary>The opponent's likely archetype and the cards its lists usually run that it has not shown yet.</summary>
    public bool ShowOpponentArchetype { get; set; } = true;

    /// <summary>The turn timers by the hero portraits: the running turn, the rope warning and the average turn.</summary>
    public bool ShowTurnTimer { get; set; } = true;

    public bool ShowDrawChance { get; set; } = true;

    /// <summary>Keep rate and win rate over your opening hand while you mulligan.</summary>
    public bool ShowMulligan { get; set; } = true;

    /// <summary>The counters panel (C'Thun abilities, Herald, Corpses, quests...).</summary>
    public bool ShowCounters { get; set; } = true;

    public bool ShowOpponentCounters { get; set; } = true;

    /// <summary>
    /// The card pool panel: for a card that Discovers or creates random cards, the pool it draws from (during the
    /// Discover, right after it is played, or while hovered in hand).
    /// </summary>
    public bool ShowCardPool { get; set; } = true;

    /// <summary>
    /// The Arena draft helper (beta): an ArenaSmith score over each offered card (class win rate over each hero) and the
    /// draft panel with the deck so far, its mana curve, average score and class results. Off by default until the
    /// Arena.log lines and the memory read are checked on a real draft (docs/arena-draft.md).
    /// </summary>
    public bool ShowArenaDraft { get; set; }

    /// <summary>
    /// The Secrets helper panel: while the opponent has Secrets in play (Constructed and Arena), the Secrets of their
    /// class and format, crossed out as the game rules them out.
    /// </summary>
    public bool ShowSecrets { get; set; } = true;

    public const double DefaultCardPoolBrowserWidth = 350;

    /// <summary>Width of the card pool browser (the full-height list of a pool's cards), in overlay units.</summary>
    public double CardPoolBrowserWidth { get; set; } = DefaultCardPoolBrowserWidth;

    /// <summary>Per counter id: "Auto" (when a card that cares is in play), "Always" or "Off". Missing means Auto.</summary>
    public Dictionary<string, string> CounterModes { get; set; } = new(StringComparer.Ordinal);

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

    /// <summary>Comp id the player chose in <see cref="CompMode.Manual"/>, kept across games; null when none.</summary>
    public string? ChosenCompId { get; set; }

    /// <summary>The tribe the player wants to build around (a race key such as "BEAST"); null for any.</summary>
    public string? CompFocus { get; set; }

    // Filters of the Constructed meta stats (Hearthpulse API keys such as "standard", "legend", "past_week"; keys of
    // earlier versions, e.g. "past-7", map onto them in ConstructedMeta.Key).
    public string MetaFormat { get; set; } = "standard";

    public string MetaRank { get; set; } = "all";

    public string MetaPeriod { get; set; } = "past_week";

    /// <summary>
    /// Send finished games to the player's profile on hearthpulse.net ("My statistics"). On by default: games are collected
    /// automatically while the player is signed in; the switch in Settings turns it off (Accounts/ProfileUploader,
    /// docs/profile-upload.md).
    /// </summary>
    public bool UploadGamesToProfile { get; set; } = true;

    /// <summary>Whether the player shares Battlegrounds results with the stats pool; null until they answer.</summary>
    public bool? PoolConsent { get; set; }

    /// <summary>Which list of shared fields the answer was given for (see <see cref="Pool.PoolClient.ConsentVersion"/>).</summary>
    public int PoolConsentVersion { get; set; }

    /// <summary>Key for shared game ids (see <see cref="Pool.PoolGame"/>). Never sent anywhere.</summary>
    public string? PoolSecret { get; set; }

    /// <summary>Earlier games were shared once, after the player agreed.</summary>
    public bool PoolHistoryShared { get; set; }

    /// <summary>Overrides <see cref="Pool.PoolClient.DefaultEndpoint"/>, for testing a pool of your own.</summary>
    public string? PoolEndpoint { get; set; }

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

    /// <summary>Interface language: follow the system.</summary>
    [JsonIgnore]
    public bool LanguageAuto { get => Language is not (Localization.Loc.English or Localization.Loc.Russian); set => SetIf(value, () => Language = Localization.Loc.Auto); }

    /// <summary>Interface language: English.</summary>
    [JsonIgnore]
    public bool LanguageEnglish { get => Language == Localization.Loc.English; set => SetIf(value, () => Language = Localization.Loc.English); }

    /// <summary>Interface language: Russian.</summary>
    [JsonIgnore]
    public bool LanguageRussian { get => Language == Localization.Loc.Russian; set => SetIf(value, () => Language = Localization.Loc.Russian); }

    public bool ShowGameToasts { get; set; } = true;

    /// <summary>The overlay told once, over the game, that Ctrl held over a panel moves and resizes it.</summary>
    public bool OverlayMoveTipShown { get; set; }

    public bool ShowLobbyPanel { get; set; } = true;

    /// <summary>
    /// The compact Battlegrounds lobby panel: the lobby's tribes, every player's tavern tier and main tribe. Off by
    /// default: its player lines repeat the lobby list's (<see cref="ShowLobbyPanel"/>), which is on.
    /// </summary>
    public bool ShowBgLobbyPanel { get; set; }

    /// <summary>
    /// Battlegrounds: a badge beside every opponent's leaderboard tile with their tavern tier and, after a fight with
    /// them, the main tribe of their board. Placed by the leaderboard calibration; never takes the mouse.
    /// </summary>
    public bool ShowBgLeaderboardBadges { get; set; } = true;

    /// <summary>
    /// Battlegrounds opponents' MMR: the lobby names the client shows, read from memory, matched on this computer
    /// against the region's public Blizzard leaderboard (8000 and up), which is downloaded whole once a day. Off by
    /// default; names and ratings are never saved or sent (docs/bg-opponent-mmr.md).
    /// </summary>
    public bool ShowBgOpponentMmr { get; set; }

    public bool ShowSessionPanel { get; set; } = true;

    /// <summary>The ranked ladder session widget: in the overlay outside games and on the Overview page.</summary>
    public bool ShowLadderSession { get; set; } = true;

    /// <summary>Hours without a game after which the next game starts a new ladder session (Hearthstone starting does too).</summary>
    public double LadderSessionBreakHours { get; set; } = 2;

    /// <summary>Show simulated win / tie / loss odds while a Battlegrounds combat plays out.</summary>
    public bool ShowCombatOdds { get; set; } = true;

    /// <summary>User-placed overlay panels by id; panels without an entry use their default spot.</summary>
    public Dictionary<string, PanelPlacement> Panels { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// How each overlay panel looks, by id (opacity, compact lists); kept apart from <see cref="Panels"/> so a panel can
    /// be faded or made compact without being moved from its default spot. Missing = opaque and full size.
    /// </summary>
    public Dictionary<string, PanelLook> PanelLooks { get; set; } = new(StringComparer.Ordinal);

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

    /// <summary>
    /// The kept logs were read again once to fill the My Stats fields (who went first, Battlegrounds combats and hero
    /// offers) of games saved before the app recorded them.
    /// </summary>
    public bool HistoryStatsFilled { get; set; }

    /// <summary>
    /// The kept logs were read again once to fill the deck page's card facts (mulligan, cards drawn and played, the
    /// opponent's played cards) of games saved before the app recorded them.
    /// </summary>
    public bool HistoryCardsFilled { get; set; }

    /// <summary>Colour theme: a preset name, or "Custom" with the three values below.</summary>
    public string Theme { get; set; } = "Tavern";

    public double ThemeAccentHue { get; set; } = 42;

    public double ThemeBaseHue { get; set; } = 28;

    public double ThemeBaseTint { get; set; } = 1.0;

    public static AppSettings Load() => Load(AppPaths.Settings);

    /// <summary>Reads a specific file without changing the process-wide data folder.</summary>
    internal static AppSettings Load(string path)
    {
        string? unpreserved = null;
        try
        {
            if (File.Exists(path))
            {
                return FromJson(ReadWithRetry(path));
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Diag.Error("Settings unreadable, using defaults", ex);
            if (!PreserveUnreadable(path))
            {
                unpreserved = path;
            }
            else if (ex is JsonException && Salvage(path) is { } salvaged)
            {
                return salvaged;
            }
        }

        var fresh = new AppSettings { _unpreservedFile = unpreserved };
        fresh.Migrate();
        return fresh;
    }

    /// <summary>
    /// The file's text. A file another program holds for a moment (an antivirus scan, a cloud sync) is read again for up
    /// to a second before it counts as unreadable: defaults would otherwise be saved over it later.
    /// </summary>
    private static string ReadWithRetry(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return File.ReadAllText(path);
            }
            catch (IOException) when (attempt < 10 && File.Exists(path))
            {
                Thread.Sleep(100);
            }
        }
    }

    /// <summary>
    /// Keeps every setting of a file that failed to load except the values that cannot be read (a null number, an enum
    /// name from another version...): one bad value must not reset the overlay layout, the pool id and the rest. Null
    /// when the file is not a JSON object at all.
    /// </summary>
    private static AppSettings? Salvage(string path)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject file)
            {
                return null;
            }

            var kept = new JsonObject();
            foreach (var (name, value) in file)
            {
                var one = new JsonObject { [name] = value?.DeepClone() };
                try
                {
                    JsonSerializer.Deserialize<AppSettings>(one.ToJsonString(), Json);
                    kept[name] = value?.DeepClone();
                }
                catch (JsonException)
                {
                    Diag.Info($"Setting {name} unreadable, using its default");
                }
            }

            return FromJson(kept.ToJsonString());
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Reads settings saved by this or an older version and brings them up to date.</summary>
    public static AppSettings FromJson(string json)
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(json, Json) ?? new AppSettings();
        settings.Normalize();
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

    // Nullable annotations do not reject explicit JSON nulls. Repair them before migrations and UI bindings use
    // these values; keep valid choices and dictionary keys, including panels/counters from a newer version.
    private void Normalize()
    {
        Locale = DefaultIfBlank(Locale, "auto");
        Language = DefaultIfBlank(Language, Localization.Loc.Auto);
        SettingsTab = DefaultIfBlank(SettingsTab, "General");
        MetaFormat = DefaultIfBlank(MetaFormat, "standard");
        MetaRank = DefaultIfBlank(MetaRank, "all");
        MetaPeriod = DefaultIfBlank(MetaPeriod, "past_week");
        Theme = DefaultIfBlank(Theme, "Tavern");
        CounterModes ??= new(StringComparer.Ordinal);
        foreach (var key in CounterModes.Where(p => p.Value is null).Select(p => p.Key).ToArray())
        {
            CounterModes.Remove(key);
        }

        Panels ??= new(StringComparer.Ordinal);
        foreach (var (key, panel) in Panels.ToArray())
        {
            if (panel is null || !double.IsFinite(panel.X) || !double.IsFinite(panel.Y) || !double.IsFinite(panel.Scale))
            {
                Panels.Remove(key);
                continue;
            }

            panel.X = Math.Clamp(panel.X, 0, 1);
            panel.Y = Math.Clamp(panel.Y, 0, 1);
            panel.Scale = Math.Clamp(panel.Scale, 0.6, 1.6);
        }

        PanelLooks ??= new(StringComparer.Ordinal);
        foreach (var (key, look) in PanelLooks.ToArray())
        {
            if (look is null || look.IsDefault)
            {
                PanelLooks.Remove(key);
                continue;
            }

            look.Opacity = Bounded(look.Opacity, PanelLook.MinOpacity, 1, 1);
        }

        if (!Enum.IsDefined(PickHints)) PickHints = PickHints.StatsOnly;
        if (!Enum.IsDefined(CompMode)) CompMode = CompMode.Manual;
        OverlayScale = Bounded(OverlayScale, 0.6, 1.6, 1);
        OverlayOpacity = Bounded(OverlayOpacity, 0.3, 1, 1);
        CardPoolBrowserWidth = Bounded(CardPoolBrowserWidth, 280, 640, DefaultCardPoolBrowserWidth);
        LadderSessionBreakHours = Bounded(LadderSessionBreakHours, 1, 12, 2);
        LeaderboardTop = Bounded(LeaderboardTop, 0, 1, DefaultLeaderboardTop);
        LeaderboardSlotHeight = Bounded(LeaderboardSlotHeight, 0.01, 1, DefaultLeaderboardSlotHeight);
        LeaderboardWidth = Bounded(LeaderboardWidth, 0.01, 1, DefaultLeaderboardWidth);
        ThemeAccentHue = double.IsFinite(ThemeAccentHue) ? ThemeAccentHue : 42;
        ThemeBaseHue = double.IsFinite(ThemeBaseHue) ? ThemeBaseHue : 28;
        ThemeBaseTint = Bounded(ThemeBaseTint, 0, 1.5, 1);
    }

    private static string DefaultIfBlank(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;

    private static double Bounded(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

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

        // The mulligan strip under the cards became bars over them ("mulligan-bars"): its old spot does not fit them, and
        // a placement always pins a position, so its scale cannot move over alone. The stale entry goes.
        Panels.Remove("mulligan-strip");
    }

    public void Save() => Save(AppPaths.Settings);

    internal void Save(string path)
    {
        // If recovery could not make a diagnostic copy (for example the disk was full), do not silently replace the
        // original on shutdown. Retry copying first, so normal saving resumes when the storage problem is resolved.
        if (_unpreservedFile is { } unreadable)
        {
            if (!PreserveUnreadable(unreadable)) return;
            _unpreservedFile = null;
        }

        try
        {
            var temp = path + ".tmp";
            File.WriteAllText(temp, ToJson());
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diag.Error("Settings not saved", ex);
        }
    }

    private static bool PreserveUnreadable(string path)
    {
        try
        {
            var backup = path + ".invalid";
            if (File.Exists(backup)) backup += "." + Guid.NewGuid().ToString("N");
            File.Copy(path, backup, overwrite: false);
            Diag.Info($"Unreadable settings preserved at {backup}");
            return true;
        }
        catch (FileNotFoundException)
        {
            // The original was removed since the read failed; there is no longer anything to overwrite.
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diag.Error("Unreadable settings could not be preserved; saving is paused", ex);
            return false;
        }
    }
}

/// <summary>How the player wants one overlay panel to look: see-through, or lists without card art and shorter rows.</summary>
public sealed class PanelLook
{
    /// <summary>The faintest a panel can be made: below this it can no longer be found on a bright board.</summary>
    public const double MinOpacity = 0.3;

    public double Opacity { get; set; } = 1.0;

    /// <summary>Card lists in the panel use short rows without art.</summary>
    public bool Compact { get; set; }

    /// <summary>Nothing differs from a panel without an entry.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsDefault => !Compact && (Opacity >= 0.999 || !double.IsFinite(Opacity));
}

/// <summary>Top-left corner as fractions of the overlay surface, plus a per-panel size multiplier.</summary>
public sealed class PanelPlacement
{
    public double X { get; set; }

    public double Y { get; set; }

    public double Scale { get; set; } = 1.0;
}
