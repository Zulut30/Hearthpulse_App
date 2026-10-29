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
}
