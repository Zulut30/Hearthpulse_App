using Hearthpulse.Core;

namespace Hearthpulse.Tests;

/// <summary>The Twitch card stays hidden from regular users until the wide release (owner, 2026-11-07).</summary>
public sealed class TwitchPreviewTests
{
    [Fact]
    public void The_twitch_card_shows_only_with_the_preview_setting_or_the_environment_switch()
    {
        Assert.False(new AppSettings().TwitchCardShown(_ => null));
        Assert.False(new AppSettings().TwitchCardShown(_ => "0"));
        Assert.True(new AppSettings().TwitchCardShown(_ => "1"));
        Assert.True(new AppSettings().TwitchCardShown(_ => "TRUE"));
        Assert.True(new AppSettings { TwitchPreview = true }.TwitchCardShown(_ => null));
        Assert.True(AppSettings.FromJson("""{ "TwitchPreview": true }""").TwitchCardShown(_ => null));
    }
}
