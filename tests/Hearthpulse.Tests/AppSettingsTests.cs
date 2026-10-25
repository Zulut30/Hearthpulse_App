using Hearthpulse.Core;

namespace Hearthpulse.Tests;

public sealed class AppSettingsTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("hp-settings-").FullName;
    private string FilePath => Path.Combine(_directory, "settings.json");

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void Null_collections_and_required_strings_recover_without_losing_other_settings()
    {
        var settings = AppSettings.FromJson("""
            {"Panels":null,"CounterModes":null,"Locale":null,"Language":null,"SettingsTab":null,
             "MetaFormat":null,"MetaRank":null,"MetaPeriod":null,"Theme":null,"OverlayEnabled":false}
            """);

        Assert.Empty(settings.Panels);
        Assert.Empty(settings.CounterModes);
        Assert.Equal(("auto", "auto", "General", "standard", "all", "past_week", "Tavern"),
            (settings.Locale, settings.Language, settings.SettingsTab, settings.MetaFormat, settings.MetaRank, settings.MetaPeriod, settings.Theme));
        Assert.False(settings.OverlayEnabled);
        Assert.Equal(AppSettings.DefaultLeaderboardTop, settings.LeaderboardTop);
    }

    [Fact]
    public void Null_entries_are_removed_while_valid_and_unknown_panel_keys_survive_migration()
    {
        var settings = AppSettings.FromJson("""
            {"Panels":{"broken":null,"future-panel":{"X":0.25,"Y":0.75,"Scale":1.2},"mulligan-strip":null},
             "CounterModes":{"broken":null,"future-counter":"Off"},"Theme":"Frost","Language":"ru"}
            """);

        var panel = Assert.Single(settings.Panels);
        Assert.Equal("future-panel", panel.Key);
        Assert.Equal((0.25, 0.75, 1.2), (panel.Value.X, panel.Value.Y, panel.Value.Scale));
        Assert.Equal("Off", Assert.Single(settings.CounterModes).Value);
        Assert.Equal(("Frost", "ru"), (settings.Theme, settings.Language));
        Assert.Equal("ruRU", settings.CardLocale());
    }

    [Fact]
    public void Invalid_enums_and_geometry_recover_to_supported_values()
    {
        var settings = AppSettings.FromJson("""
            {"PickHints":999,"CompMode":999,"OverlayScale":-10,"OverlayOpacity":2,
             "CardPoolBrowserWidth":99999,"LadderSessionBreakHours":0,"ThemeBaseTint":-5,
             "Panels":{"deck":{"X":-1,"Y":2,"Scale":100}}}
            """);

        Assert.Equal(PickHints.StatsOnly, settings.PickHints);
        Assert.Equal(CompMode.Manual, settings.CompMode);
        Assert.Equal((0.6, 1.0, 640.0, 1.0, 0.0),
            (settings.OverlayScale, settings.OverlayOpacity, settings.CardPoolBrowserWidth, settings.LadderSessionBreakHours, settings.ThemeBaseTint));
        var panel = settings.Panels["deck"];
        Assert.Equal((0.0, 1.0, 1.6), (panel.X, panel.Y, panel.Scale));
    }

    [Fact]
    public void Non_finite_numbers_do_not_reach_layout_or_time_span_construction()
    {
        var settings = AppSettings.FromJson("""
            {"OverlayScale":1e999,"LadderSessionBreakHours":1e999,"ThemeAccentHue":1e999,
             "Panels":{"deck":{"X":1e999}}}
            """);

        Assert.Equal(1, settings.OverlayScale);
        Assert.Equal(TimeSpan.FromHours(2), TimeSpan.FromHours(settings.LadderSessionBreakHours));
        Assert.Equal(42, settings.ThemeAccentHue);
        Assert.Empty(settings.Panels);
        Assert.NotEmpty(settings.ToJson());
    }

    [Theory]
    [InlineData("{")]
    [InlineData("""{"Panels":[]} """)]
    [InlineData("""{"Panels":{"deck":5}}""")]
    [InlineData("""{"OverlayScale":"wide"}""")]
    public void Unreadable_files_are_preserved_before_defaults_can_replace_them(string original)
    {
        File.WriteAllText(FilePath, original);

        var settings = AppSettings.Load(FilePath);

        Assert.True(settings.OverlayEnabled);
        Assert.Empty(settings.Panels);
        Assert.Equal(original, File.ReadAllText(FilePath));
        Assert.Equal(original, File.ReadAllText(FilePath + ".invalid"));
        settings.OverlayEnabled = false;
        settings.Save(FilePath);
        Assert.False(AppSettings.Load(FilePath).OverlayEnabled);
        Assert.Equal(original, File.ReadAllText(FilePath + ".invalid"));
    }

    [Fact]
    public void A_previous_diagnostic_copy_is_never_overwritten()
    {
        File.WriteAllText(FilePath + ".invalid", "first corrupt file");
        const string original = "{ second corrupt file: кириллица\n";
        File.WriteAllText(FilePath, original);

        AppSettings.Load(FilePath).Save(FilePath);

        Assert.Equal("first corrupt file", File.ReadAllText(FilePath + ".invalid"));
        var newBackup = Assert.Single(Directory.EnumerateFiles(_directory),
            path => Path.GetFileName(path).StartsWith("settings.json.invalid.", StringComparison.Ordinal));
        Assert.Equal(original, File.ReadAllText(newBackup));
        Assert.True(AppSettings.Load(FilePath).OverlayEnabled);
    }

    [Fact]
    public void A_failed_backup_prevents_overwriting_and_saving_recovers_after_storage_is_fixed()
    {
        const string original = "not json";
        File.WriteAllText(FilePath, original);
        Directory.CreateDirectory(FilePath + ".invalid");

        var settings = AppSettings.Load(FilePath);
        settings.OverlayEnabled = false;
        settings.Save(FilePath);
        Assert.Equal(original, File.ReadAllText(FilePath));
        Assert.False(File.Exists(FilePath + ".tmp"));

        Directory.Delete(FilePath + ".invalid");
        settings.Save(FilePath);
        Assert.Equal(original, File.ReadAllText(FilePath + ".invalid"));
        Assert.False(AppSettings.Load(FilePath).OverlayEnabled);
    }

    [Fact]
    public void Missing_and_valid_files_need_no_diagnostic_copy()
    {
        var settings = AppSettings.Load(FilePath);
        Assert.Empty(Directory.GetFiles(_directory));
        settings.Language = "ru";
        settings.Panels["deck"] = new PanelPlacement { X = 0.3, Y = 0.2, Scale = 1.1 };
        settings.Save(FilePath);

        var loaded = AppSettings.Load(FilePath);
        Assert.Equal("ru", loaded.Language);
        Assert.Equal((0.3, 0.2, 1.1), (loaded.Panels["deck"].X, loaded.Panels["deck"].Y, loaded.Panels["deck"].Scale));
        Assert.Single(Directory.GetFiles(_directory));
    }
}
