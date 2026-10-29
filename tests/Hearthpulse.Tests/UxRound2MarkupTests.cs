using Hearthpulse.Core.Localization;

namespace Hearthpulse.Tests;

/// <summary>
/// The second usability pass (TASK-2026-10-29-ux-round2) where texts and markup are enough to tell: names as the game
/// gives them, one wording for one state, the same structure in both overlays. Read from the files, so it runs on every
/// platform.
/// </summary>
public sealed class UxRound2MarkupTests
{
    [Fact]
    public void Excavate_reads_as_Dobycha_and_Discover_stays_Raskopka()
    {
        var ru = Loc.Table(Loc.Russian);
        // Two counters stand side by side: the Russian client calls Excavate "Добыча" and Discover "Раскопка".
        var excavate = ru.Where(p => p.Key.Contains("excavate", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.NotEmpty(excavate);
        Assert.All(excavate, p => Assert.DoesNotContain("Раскоп", p.Value, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Добыч", ru["Core.Counter.excavate.Title"], StringComparison.Ordinal);
        Assert.Contains("Добыч", ru["Pool.Mechanic.EXCAVATE"], StringComparison.Ordinal);
        Assert.Contains("Раскоп", ru["Core.Counter.discovers.Title"], StringComparison.Ordinal);
        Assert.Contains("Раскоп", ru["Pool.Mechanic.DISCOVER"], StringComparison.Ordinal);
    }

    /// <summary>What is still on its way, one pattern: "{What} загружается…" / "Loading {what}…".</summary>
    private static readonly string[] LoadingKeys =
    [
        "Main.Bg.CommunityLoading", "Stats.Bg.NoteLoading", "Main.Meta.Loading", "Host.Busy.LoadingCards", "Overlay.Bar.ReadingLobby",
    ];

    [Theory]
    [InlineData("en")]
    [InlineData("ru")]
    public void Loading_texts_follow_one_pattern(string language)
    {
        var table = Loc.Table(language);
        foreach (var key in LoadingKeys)
        {
            var text = table[key];
            Assert.EndsWith("…", text, StringComparison.Ordinal);
            if (language == Loc.Russian)
            {
                Assert.False(text.StartsWith("Загрузка", StringComparison.Ordinal) || text.StartsWith("Загружаем", StringComparison.Ordinal)
                    || text.StartsWith("Читаю", StringComparison.Ordinal), $"{key}: {text}");
            }
            else
            {
                Assert.True(text.StartsWith("Loading ", StringComparison.Ordinal) || text.StartsWith("Reading ", StringComparison.Ordinal), $"{key}: {text}");
            }
        }

        // The same Battlegrounds community data has one name on the meta page and in My Stats.
        Assert.Equal(table["Main.Bg.CommunityLoading"], table["Stats.Bg.NoteLoading"]);
        // An update is "downloaded" in both places that announce it, and the version stays in the text.
        Assert.Contains("{0}", table["Main.Update.Downloaded"], StringComparison.Ordinal);
        if (language == Loc.Russian)
        {
            Assert.Contains("скачана", table["Main.Update.Downloaded"], StringComparison.Ordinal);
            Assert.Contains("скачана", table["Host.Update.Ready"], StringComparison.Ordinal);
        }

        // Numbers that could not be had are "unavailable", not a load that went wrong.
        var noData = table["Arena.NoData"];
        Assert.Contains(language == Loc.Russian ? "недоступн" : "unavailable", noData, StringComparison.Ordinal);
        Assert.DoesNotContain(language == Loc.Russian ? "не загрузились" : "did not load", noData, StringComparison.Ordinal);
    }

    /// <summary>
    /// The names one panel goes by in the live bar's "⋯" menu, in Settings and over its layout placeholder: the player
    /// looks in Settings for what the menu called it.
    /// </summary>
    private static readonly string[][] PanelNames =
    [
        ["Main.Live.Panel.OpponentDeck", "Settings.Tracker.Opponent"],
        ["Main.Live.Panel.Secrets", "Settings.Overlay.Secrets", "Overlay.Panel.Secrets"],
        ["Main.Live.Panel.BgBar", "Overlay.Panel.BgBar"],
        ["Main.Live.Panel.BgSession", "Settings.Overlay.Session"],
        ["Settings.Tracker.Summary", "Overlay.Panel.Toast"],
        ["Main.Live.Panel.Odds", "Settings.Overlay.Odds", "Overlay.Panel.Odds"],
        ["Main.Live.Panel.Boards", "Settings.Overlay.Boards"],
    ];

    [Theory]
    [InlineData("en")]
    [InlineData("ru")]
    public void Each_panel_has_one_name_everywhere(string language)
    {
        var table = Loc.Table(language);
        foreach (var keys in PanelNames)
        {
            Assert.All(keys.Skip(1), key => Assert.True(table[keys[0]] == table[key], $"{keys[0]} \"{table[keys[0]]}\" vs {key} \"{table[key]}\""));
        }

        // Inside the Battlegrounds group of Settings the short name stands for the same bar.
        Assert.Contains(table["Settings.Overlay.TopBar"], table["Main.Live.Panel.BgBar"], StringComparison.OrdinalIgnoreCase);
    }
}
