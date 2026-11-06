namespace Hearthpulse.Core;

/// <summary>Features kept from regular users until their wide release (no switch in Settings: settings.json or an environment variable).</summary>
public sealed partial class AppSettings
{
    /// <summary>Environment switch that shows the Twitch extension's card in Settings → Stream: <c>HEARTHPULSE_TWITCH=1</c>.</summary>
    public const string TwitchPreviewVariable = "HEARTHPULSE_TWITCH";

    /// <summary>
    /// Shows the Twitch extension's card in Settings → Stream before its wide release (owner, 2026-11-07). Off by default
    /// and not in the interface: <c>"TwitchPreview": true</c> in settings.json or <see cref="TwitchPreviewVariable"/>.
    /// </summary>
    public bool TwitchPreview { get; set; }

    /// <summary>The Twitch card shows: <see cref="TwitchPreview"/> or the environment switch set to 1 / true.</summary>
    public bool TwitchCardShown(Func<string, string?>? environment = null)
    {
        var value = (environment ?? Environment.GetEnvironmentVariable)(TwitchPreviewVariable);
        return TwitchPreview || value is "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }
}
