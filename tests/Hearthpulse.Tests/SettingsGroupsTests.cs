using System.Text.Json.Nodes;
using Hearthpulse.Core;

namespace Hearthpulse.Tests;

public sealed class SettingsGroupsTests
{
    [Fact]
    public void Every_grouped_name_is_a_saved_setting_and_none_is_in_two_groups()
    {
        var saved = JsonNode.Parse(new AppSettings().ToJson())!.AsObject();
        var all = SettingsGroups.Properties.Values.SelectMany(n => n).ToList();
        Assert.Equal(all.Count, all.Distinct(StringComparer.Ordinal).Count());
        Assert.All(all, name => Assert.True(saved.ContainsKey(name), name));
    }

    [Fact]
    public void Resetting_a_group_restores_its_defaults_and_leaves_the_rest()
    {
        var settings = new AppSettings
        {
            Theme = "Void", ThemeAccent = "Rose", CornerStyle = ThemeLook.Sharp, InterfaceScale = 1.2, BackgroundIntensity = 0.5,
            OverlayScale = 1.4, ShowCounters = false, HistoryImported = true, PoolSecret = "key",
        };

        Assert.True(SettingsGroups.Reset(settings, SettingsGroups.Appearance));
        Assert.Equal("Tavern", settings.Theme);
        Assert.Equal("", settings.ThemeAccent);
        Assert.Equal(ThemeLook.Rounded, settings.CornerStyle);
        Assert.Equal(1.0, settings.InterfaceScale);
        Assert.Equal(1.0, settings.BackgroundIntensity);
        Assert.Equal(1.4, settings.OverlayScale);
        Assert.False(settings.ShowCounters);
        Assert.False(SettingsGroups.Reset(settings, "Nope"));
    }

    [Fact]
    public void Resetting_everything_keeps_history_consent_key_and_panel_positions()
    {
        var settings = new AppSettings { OverlayScale = 1.4, ShowCounters = false, StreamerMode = true, HistoryImported = true, PoolSecret = "key", PoolConsent = true };
        settings.Panels["deck"] = new PanelPlacement { X = 0.1, Y = 0.2, Scale = 1.1 };

        SettingsGroups.ResetAll(settings);

        Assert.Equal(1.0, settings.OverlayScale);
        Assert.True(settings.ShowCounters);
        Assert.False(settings.StreamerMode);
        Assert.True(settings.HistoryImported);
        Assert.Equal("key", settings.PoolSecret);
        Assert.True(settings.PoolConsent);
        Assert.True(settings.Panels.ContainsKey("deck"));
    }

    [Fact]
    public void An_exported_file_carries_preferences_but_no_key_consent_or_folders()
    {
        var source = new AppSettings { Theme = "Mech", ThemeAccent = "Sky", OverlayScale = 0.8, PoolSecret = "secret", PoolConsent = true, HearthstonePath = @"C:\Games\HS" };
        source.Panels["deck"] = new PanelPlacement { X = 0.3, Y = 0.4, Scale = 1 };
        var file = SettingsGroups.Export(source);

        Assert.DoesNotContain("secret", file, StringComparison.Ordinal);
        Assert.DoesNotContain("PoolConsent", file, StringComparison.Ordinal);
        Assert.DoesNotContain("HearthstonePath", file, StringComparison.Ordinal);

        var target = new AppSettings { PoolSecret = "mine", HistoryImported = true };
        Assert.True(SettingsGroups.Import(target, file));
        Assert.Equal("Mech", target.Theme);
        Assert.Equal("Sky", target.ThemeAccent);
        Assert.Equal(0.8, target.OverlayScale);
        Assert.Equal(0.3, target.Panels["deck"].X);
        Assert.Equal("mine", target.PoolSecret);
        Assert.True(target.HistoryImported);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"Theme\":\"Void\"}")]
    [InlineData("[1,2]")]
    public void Anything_but_a_settings_file_is_refused_without_changes(string text)
    {
        var settings = new AppSettings { Theme = "Fel" };
        Assert.False(SettingsGroups.Import(settings, text));
        Assert.Equal("Fel", settings.Theme);
    }

    [Fact]
    public void An_imported_file_with_values_out_of_range_is_repaired()
    {
        var settings = new AppSettings();
        Assert.True(SettingsGroups.Import(settings,
            "{\"hearthpulseSettings\":1,\"settings\":{\"InterfaceScale\":9,\"CornerStyle\":\"Wavy\",\"Theme\":\"Grove\"}}"));
        Assert.Equal(AppSettings.MaxInterfaceScale, settings.InterfaceScale);
        Assert.Equal(ThemeLook.Rounded, settings.CornerStyle);
        Assert.Equal("Grove", settings.Theme);
    }
}
