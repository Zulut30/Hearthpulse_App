namespace Hearthpulse.Core;

/// <summary>A density preset for the whole overlay (D5): one choice instead of the size slider and each panel's tag.</summary>
public enum OverlayDensityPreset
{
    /// <summary>Smaller panels with short card rows without art.</summary>
    Compact,

    /// <summary>The overlay as it starts.</summary>
    Normal,

    /// <summary>Larger panels, for a big or distant screen.</summary>
    Large,
}

/// <summary>
/// Overlay density presets (D5): each sets <see cref="AppSettings.OverlayScale"/> and the compact card rows of every
/// panel that has them, leaving each panel's own opacity alone. A size or a panel tag changed by hand afterwards is
/// simply no preset (<see cref="Of"/> returns null).
/// </summary>
public static class OverlayDensity
{
    /// <summary>The overlay panels with compact card rows (<c>CanCompact</c> in both overlay views).</summary>
    public static readonly IReadOnlyList<string> CompactPanels = ["deck", "opponent", "graveyard", "opponent-deck"];

    /// <summary>The overlay size a preset sets.</summary>
    public static double Scale(OverlayDensityPreset preset) => preset switch
    {
        OverlayDensityPreset.Compact => 0.85,
        OverlayDensityPreset.Large => 1.2,
        _ => 1.0,
    };

    /// <summary>The preset the settings match, or null when the size or a panel's rows were set by hand.</summary>
    public static OverlayDensityPreset? Of(AppSettings settings)
    {
        foreach (var preset in Enum.GetValues<OverlayDensityPreset>())
        {
            var compact = preset == OverlayDensityPreset.Compact;
            if (Math.Abs(settings.OverlayScale - Scale(preset)) < 0.001
                && CompactPanels.All(id => (settings.PanelLooks.GetValueOrDefault(id)?.Compact ?? false) == compact))
            {
                return preset;
            }
        }

        return null;
    }

    /// <summary>Sets the size and the compact rows of <paramref name="preset"/>; a panel left with nothing set loses its entry.</summary>
    public static void Apply(AppSettings settings, OverlayDensityPreset preset)
    {
        settings.OverlayScale = Scale(preset);
        var compact = preset == OverlayDensityPreset.Compact;
        foreach (var id in CompactPanels)
        {
            var look = settings.PanelLooks.GetValueOrDefault(id) ?? new PanelLook();
            look.Compact = compact;
            if (look.IsDefault)
            {
                settings.PanelLooks.Remove(id);
            }
            else
            {
                settings.PanelLooks[id] = look;
            }
        }
    }
}
