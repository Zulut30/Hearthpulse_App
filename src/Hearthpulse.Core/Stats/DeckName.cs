namespace Hearthpulse.Core.Stats;

/// <summary>A deck name as the interface writes it.</summary>
public static class DeckName
{
    /// <summary>
    /// Hearthstone numbers a new deck of a class it already has without a space ("Рыцарь смерти3", "Mage2"): the
    /// number gets its space back. Names like "R2D2", "XL7" or "Zoo 2" stay as they are.
    /// </summary>
    public static string Display(string name)
    {
        var digits = name.Length;
        while (digits > 0 && char.IsAsciiDigit(name[digits - 1]))
        {
            digits--;
        }

        var count = name.Length - digits;
        return count is > 0 and <= 3 && digits > 0 && char.IsLower(name[digits - 1])
            ? string.Concat(name.AsSpan(0, digits), " ", name.AsSpan(digits))
            : name;
    }
}
