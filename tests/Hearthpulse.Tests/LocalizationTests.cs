using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Hearthpulse.Core;
using Hearthpulse.Core.Localization;
using Xunit;

namespace Hearthpulse.Tests;

/// <summary>
/// Interface strings (docs/localization.md): every language has every key, placeholders and plural forms line up,
/// and every key the UIs and view models use exists. Tests never switch <see cref="Loc.Instance"/>: other tests run in
/// parallel and read it.
/// </summary>
public sealed class LocalizationTests
{
    private static readonly Regex Placeholder = new(@"\{(\d+)(?:[,:][^}]*)?\}", RegexOptions.CultureInvariant);

    [Fact]
    public void English_and_Russian_have_the_same_keys()
    {
        var english = Loc.Table(Loc.English);
        var russian = Loc.Table(Loc.Russian);
        Assert.NotEmpty(english);
        Assert.Empty(russian.Keys.Except(english.Keys).Order());
        Assert.Empty(english.Keys.Except(russian.Keys).Order());
    }

    [Fact]
    public void Placeholders_and_plural_forms_match_between_languages()
    {
        var russian = Loc.Table(Loc.Russian);
        var problems = new List<string>();
        foreach (var (key, english) in Loc.Table(Loc.English))
        {
            if (!russian.TryGetValue(key, out var translated))
            {
                continue;
            }

            if (Placeholders(english) != Placeholders(translated))
            {
                problems.Add($"{key}: placeholders {Placeholders(english)} vs {Placeholders(translated)}");
            }

            // Plural texts: "one|other" in English, "one|few|many" in Russian.
            var forms = english.Split('|').Length;
            if (forms > 1 && (forms != 2 || translated.Split('|').Length != 3))
            {
                problems.Add($"{key}: {forms} English and {translated.Split('|').Length} Russian plural forms");
            }
        }

        Assert.Empty(problems);
    }

    [Fact]
    public void Every_key_used_in_the_code_exists()
    {
        var root = RepoRoot();
        var used = new Regex(@"\{i:Loc\s+([A-Za-z0-9_.]+)\}|Loc\.(?:T|F|P)\(\s*""([A-Za-z0-9_.]+)""", RegexOptions.CultureInvariant);
        var missing = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.*", SearchOption.AllDirectories)
                     .Where(f => f.EndsWith(".xaml", StringComparison.Ordinal) || f.EndsWith(".axaml", StringComparison.Ordinal) || f.EndsWith(".cs", StringComparison.Ordinal))
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            foreach (Match match in used.Matches(File.ReadAllText(file)))
            {
                var key = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                if (!Loc.Has(key))
                {
                    missing.Add($"{Path.GetRelativePath(root, file)}: {key}");
                }
            }
        }

        Assert.Empty(missing);
    }

    [Fact]
    public void User_facing_texts_name_the_source_Hearthpulse_data()
    {
        foreach (var language in Loc.Languages)
        {
            Assert.DoesNotContain(Loc.Table(language), p => p.Value.Contains("HSReplay", StringComparison.OrdinalIgnoreCase) || p.Value.Contains("HSGuru", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Theory]
    [InlineData("ru-RU", "auto", "ru")]
    [InlineData("ru-RU", null, "ru")]
    [InlineData("de-DE", "auto", "en")]
    [InlineData("en-US", "auto", "en")]
    [InlineData("de-DE", "ru", "ru")]
    [InlineData("ru-RU", "en", "en")]
    [InlineData("ru-RU", "EN", "en")]
    public void Auto_follows_the_system_and_falls_back_to_English(string system, string? setting, string expected) =>
        Assert.Equal(expected, Loc.Resolve(setting, CultureInfo.GetCultureInfo(system)));

    [Theory]
    [InlineData("auto", "ru", "ruRU")]
    [InlineData("auto", "en", "enUS")]
    [InlineData("deDE", "ru", "deDE")]
    public void Card_locale_follows_the_interface_language_unless_set(string locale, string language, string expected) =>
        Assert.Equal(expected, new AppSettings { Locale = locale, Language = language }.CardLocale());

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(4, 1)]
    [InlineData(5, 2)]
    [InlineData(11, 2)]
    [InlineData(12, 2)]
    [InlineData(14, 2)]
    [InlineData(21, 0)]
    [InlineData(22, 1)]
    [InlineData(25, 2)]
    [InlineData(111, 2)]
    [InlineData(101, 0)]
    [InlineData(0, 2)]
    public void Russian_plural_forms(long count, int form) => Assert.Equal(form, Loc.PluralIndex(Loc.Russian, count));

    [Theory]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    [InlineData(2, 1)]
    [InlineData(21, 1)]
    public void English_plural_forms(long count, int form) => Assert.Equal(form, Loc.PluralIndex(Loc.English, count));

    [Fact]
    public void A_language_change_updates_bound_texts_and_falls_back_to_English()
    {
        var loc = new Loc();
        var changed = new List<string?>();
        var events = 0;
        loc.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        loc.LanguageChanged += () => events++;

        Assert.Equal("Settings", loc["Settings.Title"]);
        loc.SetLanguage(Loc.Russian);
        Assert.Equal("Настройки", loc["Settings.Title"]);
        Assert.Contains("Item[]", changed);
        Assert.Equal(1, events);

        loc.SetLanguage("ru");
        Assert.Equal(1, events);
        Assert.Equal("No.Such.Key", loc["No.Such.Key"]);
    }

    [Fact]
    public void Language_choice_round_trips_through_settings_json()
    {
        var settings = new AppSettings { LanguageRussian = true, SettingsTab = "Data" };
        var read = AppSettings.FromJson(settings.ToJson());
        Assert.Equal("ru", read.Language);
        Assert.True(read.LanguageRussian);
        Assert.False(read.LanguageAuto);
        Assert.Equal("Data", read.SettingsTab);
        Assert.True(AppSettings.FromJson("{}").LanguageAuto);
    }

    private static string Placeholders(string text) =>
        string.Join(",", Placeholder.Matches(text).Select(m => m.Groups[1].Value).Distinct().Order(StringComparer.Ordinal));

    // From this source file's path rather than the test binaries: builds may put those outside the repository
    // (--artifacts-path).
    private static string RepoRoot([CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Hearthpulse.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("Repository root (Hearthpulse.slnx) not found above the test sources.");
    }
}
