using System.Globalization;
using System.Text.RegularExpressions;
using Hearthpulse.Core.Localization;

namespace Hearthpulse.Tests;

/// <summary>
/// <see cref="NumberCulture"/>: a wrapping text never breaks between a number and its "%" (the render showed
/// "20,9 / % меты" on the tier list), between digit groups, or between a number and its time unit.
/// </summary>
public sealed class NumberCultureTests
{
    private const char NoBreakSpace = ' ';

    [Fact]
    public void Russian_percent_keeps_its_space_but_never_breaks()
    {
        var culture = NumberCulture.KeepTogether(new CultureInfo("ru-RU"));

        Assert.Equal("20,9" + NoBreakSpace + "%", 0.209.ToString("P1", culture));
        Assert.Equal("56" + NoBreakSpace + "%", 0.56.ToString("P0", culture));
        Assert.DoesNotContain(' ', (-0.031).ToString("P1", culture));
        Assert.Contains(NoBreakSpace + "%", (-0.031).ToString("P1", culture));
        Assert.DoesNotContain(' ', 45066.ToString("N0", culture));
        Assert.DoesNotContain(' ', 1234.5.ToString("P0", culture));
    }

    [Fact]
    public void English_comes_back_unchanged()
    {
        var english = new CultureInfo("en-US");

        Assert.Same(english, NumberCulture.KeepTogether(english));
        Assert.Equal("55.2%", 0.552.ToString("P1", NumberCulture.KeepTogether(english)));
    }

    [Fact]
    public void A_sign_before_the_number_and_space_group_separators_are_made_non_breaking()
    {
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.NumberFormat.PercentPositivePattern = 3; // "% n"
        culture.NumberFormat.PercentNegativePattern = 7; // "-% n"
        culture.NumberFormat.NumberGroupSeparator = " ";
        culture.NumberFormat.PercentGroupSeparator = " ";

        var kept = NumberCulture.KeepTogether(culture);

        Assert.Equal("%" + NoBreakSpace + "12.5", 0.125.ToString("P1", kept));
        Assert.Equal("-%" + NoBreakSpace + "12.5", (-0.125).ToString("P1", kept));
        Assert.Equal("12" + NoBreakSpace + "345", 12345.ToString("N0", kept));
        Assert.Equal(" ", culture.NumberFormat.NumberGroupSeparator); // the original stays as it was
    }

    [Fact]
    public void Apply_keeps_the_system_culture_with_unbreakable_numbers()
    {
        // The machine's own culture, so tests running in parallel on other threads see the same number format.
        var before = CultureInfo.CurrentCulture;
        var beforeDefault = CultureInfo.DefaultThreadCurrentCulture;
        try
        {
            NumberCulture.Apply();

            Assert.Equal(before.Name, CultureInfo.CurrentCulture.Name);
            Assert.Same(CultureInfo.CurrentCulture, CultureInfo.DefaultThreadCurrentCulture);
            Assert.DoesNotContain(' ', 0.209.ToString("P1", CultureInfo.CurrentCulture));
            Assert.DoesNotContain(' ', 45066.ToString("N0", CultureInfo.CurrentCulture));
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
            CultureInfo.DefaultThreadCurrentCulture = beforeDefault;
        }
    }

    [Theory]
    [InlineData(Loc.English)]
    [InlineData(Loc.Russian)]
    public void Time_units_stay_on_the_line_of_their_number(string language)
    {
        var unit = new Regex(@"\} (min|мин|ч)\b", RegexOptions.CultureInvariant);
        var split = Loc.Table(language).Where(entry => unit.IsMatch(entry.Value)).Select(entry => entry.Key).Order();

        Assert.Empty(split);
    }
}
