using System.Globalization;

namespace Hearthpulse.Core.Localization;

/// <summary>
/// The culture numbers are written in, with the system's format kept but its breakable spaces made non-breaking: .NET
/// writes the space of a "56,2 %" pattern as U+0020, so a wrapping text could put "%" on the next line. Both UIs call
/// <see cref="Apply"/> once at start-up; <see cref="Loc.SetLanguage"/> changes strings only, never the culture.
/// </summary>
public static class NumberCulture
{
    private const char NoBreakSpace = ' ';

    /// <summary>Makes the current and default thread cultures the <see cref="KeepTogether"/> copy of the current one.</summary>
    public static void Apply()
    {
        var culture = KeepTogether(CultureInfo.CurrentCulture);
        CultureInfo.CurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
    }

    /// <summary>
    /// A copy of <paramref name="culture"/> whose percent sign and digit groups never split from the number: "n %" turns
    /// into "n" + " %", "% n" into "% " + "n", and a plain-space group separator into U+00A0. A culture without
    /// such spaces ("55.2%") comes back unchanged.
    /// </summary>
    public static CultureInfo KeepTogether(CultureInfo culture)
    {
        var numbers = culture.NumberFormat;
        var spaceAfter = numbers.PercentPositivePattern == 0; // "n %"
        var spaceBefore = numbers.PercentPositivePattern == 3; // "% n"
        var group = numbers.NumberGroupSeparator == " " || numbers.PercentGroupSeparator == " ";
        if (!spaceAfter && !spaceBefore && !group)
        {
            return culture;
        }

        var copy = (CultureInfo)culture.Clone();
        var format = copy.NumberFormat;
        if (spaceAfter)
        {
            format.PercentSymbol = NoBreakSpace + numbers.PercentSymbol;
            format.PercentPositivePattern = 1; // "n%"
            // Negative patterns with the same "n %" space lose it too; the sign already carries the space.
            format.PercentNegativePattern = numbers.PercentNegativePattern switch
            {
                0 => 1, // "-n %" → "-n%"
                8 => 6, // "n %-" → "n%-"
                11 => 5, // "n- %" → "n-%"
                var other => other,
            };
        }
        else if (spaceBefore)
        {
            format.PercentSymbol = numbers.PercentSymbol + NoBreakSpace;
            format.PercentPositivePattern = 2; // "%n"
            format.PercentNegativePattern = numbers.PercentNegativePattern switch
            {
                7 => 2, // "-% n" → "-%n"
                9 => 4, // "% n-" → "%n-"
                10 => 3, // "% -n" → "%-n"
                var other => other,
            };
        }

        if (format.NumberGroupSeparator == " ")
        {
            format.NumberGroupSeparator = NoBreakSpace.ToString();
        }

        if (format.PercentGroupSeparator == " ")
        {
            format.PercentGroupSeparator = NoBreakSpace.ToString();
        }

        if (format.CurrencyGroupSeparator == " ")
        {
            format.CurrencyGroupSeparator = NoBreakSpace.ToString();
        }

        return copy;
    }
}
