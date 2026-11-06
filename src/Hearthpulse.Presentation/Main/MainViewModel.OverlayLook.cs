using Hearthpulse.Core;

#if HEARTHPULSE_WPF
using Hearthpulse.App.Overlay;

namespace Hearthpulse.App.Main;
#else
using Hearthpulse.UI.Overlay;

namespace Hearthpulse.UI.Main;
#endif

/// <summary>
/// Settings -> Overlay, how the overlay looks (D5): the density presets and the live preview of a deck panel next to
/// them. The views apply a preset through <see cref="ApplyOverlayDensity"/>, refresh their size slider (the settings
/// object does not notify) and save; a slider moved by hand calls <see cref="RefreshOverlayLook"/>.
/// </summary>
public sealed partial class MainViewModel
{
    private OverlayViewModel? _overlayPreview;

    public bool DensityCompact => OverlayDensity.Of(Settings) == OverlayDensityPreset.Compact;

    public bool DensityNormal => OverlayDensity.Of(Settings) == OverlayDensityPreset.Normal;

    public bool DensityLarge => OverlayDensity.Of(Settings) == OverlayDensityPreset.Large;

    /// <summary>The preview's deck panel has short rows when the deck panel in the game has.</summary>
    public bool PreviewCompact => Settings.PanelLooks.GetValueOrDefault("deck")?.Compact ?? false;

    /// <summary>The preview's size: the overlay's size, as on a 1080p screen.</summary>
    public double PreviewScale => Settings.OverlayScale;

    /// <summary>The sample game the preview's deck panel shows; built once the card database is in.</summary>
    public OverlayViewModel OverlayPreviewModel => _overlayPreview ??= OverlayPreview.Build(_cards, Settings);

    /// <summary>Applies a preset ("Compact", "Normal" or "Large") and refreshes the choice and the preview.</summary>
    public void ApplyOverlayDensity(string preset)
    {
        if (Enum.TryParse<OverlayDensityPreset>(preset, out var value))
        {
            OverlayDensity.Apply(Settings, value);
            RefreshOverlayLook();
        }
    }

    /// <summary>The size, opacity or a panel's rows changed: the choice and the preview follow.</summary>
    public void RefreshOverlayLook()
    {
        Raise(nameof(DensityCompact));
        Raise(nameof(DensityNormal));
        Raise(nameof(DensityLarge));
        Raise(nameof(PreviewCompact));
        Raise(nameof(PreviewScale));
    }

    /// <summary>A new card database (language, first load): the preview's names and art follow.</summary>
    private void RebuildOverlayPreview()
    {
        _overlayPreview = null;
        Raise(nameof(OverlayPreviewModel));
    }
}
