namespace Hearthpulse.Core;

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

/// <summary>The counts of a finished move from another tracker (<see cref="AppSettings.LastMigration"/>).</summary>
public sealed class MigrationRecord
{
    public DateTime At { get; set; }

    public int Added { get; set; }

    public int Already { get; set; }

    public int Skipped { get; set; }

    public int Decks { get; set; }

    public int Replays { get; set; }
}

/// <summary>The choices of <see cref="AppSettings.StreamWindowBackground"/>.</summary>
public static class StreamBackgrounds
{
    public const string Theme = "Theme";

    public const string Green = "Green";

    public const string Magenta = "Magenta";

    public static readonly IReadOnlyList<string> All = [Theme, Green, Magenta];
}

/// <summary>An OBS capture window's bounds in device-independent pixels (pixels at 100 %), top-left corner first.</summary>
public sealed class StreamWindowPlacement
{
    /// <summary>Saved coordinates beyond this are clamped (no desktop reaches that far).</summary>
    public const double MaxCoordinate = 100_000;

    /// <summary>Saved sizes beyond this are clamped; there is no other maximum.</summary>
    public const double MaxSize = 20_000;

    public double X { get; set; }

    public double Y { get; set; }

    public double Width { get; set; }

    public double Height { get; set; }
}

/// <summary>Top-left corner as fractions of the overlay surface, plus a per-panel size multiplier.</summary>
public sealed class PanelPlacement
{
    public double X { get; set; }

    public double Y { get; set; }

    public double Scale { get; set; } = 1.0;
}
