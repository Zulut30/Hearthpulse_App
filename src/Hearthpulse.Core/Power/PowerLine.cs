using System.Globalization;

namespace Hearthpulse.Core.Power;

/// <summary>
/// Splits the most frequent Power.log lines without a regex match per line: a GameState line, its "tag=" lines and
/// TAG_CHANGE. Each method accepts exactly what the pattern in its comment accepts and returns the same groups (as
/// ranges of the input); <c>PowerLineTests</c> checks them against those patterns. The regex objects (match, groups)
/// were most of the parser's 760 bytes per line.
/// </summary>
internal static class PowerLine
{
    private const string GameState = "GameState.";
    private const string DebugPrint = "DebugPrint";
    private const string TagChange = "TAG_CHANGE Entity=";

    /// <summary><c>^[DWEI] (?&lt;time&gt;\d{2}:\d{2}:\d{2}\.\d+) GameState\.(?&lt;method&gt;DebugPrint\w+)\(\) - (?&lt;body&gt;.*)$</c></summary>
    public static bool TrySplit(string line, out Range time, out Range method, out Range body)
    {
        time = method = body = default;
        var s = line.AsSpan();
        if (s.Length < 2 || s[0] is not ('D' or 'W' or 'E' or 'I') || s[1] != ' ')
        {
            return false;
        }

        // \d{2}:\d{2}:\d{2}\.\d+
        var i = 2;
        if (!Digits(s, ref i, 2) || !Char(s, ref i, ':') || !Digits(s, ref i, 2) || !Char(s, ref i, ':')
            || !Digits(s, ref i, 2) || !Char(s, ref i, '.') || !Digits(s, ref i, 1))
        {
            return false;
        }

        while (i < s.Length && char.IsDigit(s[i]))
        {
            i++;
        }

        time = 2..i;
        if (!Char(s, ref i, ' ') || !s[i..].StartsWith(GameState, StringComparison.Ordinal))
        {
            return false;
        }

        i += GameState.Length;
        var methodStart = i;
        if (!s[i..].StartsWith(DebugPrint, StringComparison.Ordinal))
        {
            return false;
        }

        i += DebugPrint.Length;
        var wordStart = i;
        while (i < s.Length && IsWordChar(s[i]))
        {
            i++;
        }

        if (i == wordStart || !s[i..].StartsWith("() - ", StringComparison.Ordinal))
        {
            return false;
        }

        method = methodStart..i;
        i += "() - ".Length;

        // .*$ : no line feed, except one at the very end, which is left out.
        var end = s.Length;
        if (end > i && s[end - 1] == '\n')
        {
            end--;
        }

        if (s[i..end].Contains('\n'))
        {
            return false;
        }

        body = i..end;
        return true;
    }

    /// <summary><c>^tag=(?&lt;tag&gt;\S+) value=(?&lt;value&gt;\S*)</c></summary>
    public static bool TryTagValue(string text, out Range tag, out Range value) =>
        TryTagValueAt(text, 0, out tag, out value);

    /// <summary><c>^TAG_CHANGE Entity=(?&lt;entity&gt;.+?) tag=(?&lt;tag&gt;\S+) value=(?&lt;value&gt;\S*)</c></summary>
    public static bool TryTagChange(string text, out Range entity, out Range tag, out Range value)
    {
        entity = tag = value = default;
        var s = text.AsSpan();
        if (!s.StartsWith(TagChange, StringComparison.Ordinal))
        {
            return false;
        }

        // The entity is the shortest text (at least one character, no line feed) after which the rest matches.
        var start = TagChange.Length;
        for (var at = start + 1; at < s.Length; at++)
        {
            if (s[at - 1] == '\n')
            {
                return false;
            }

            if (s[at] == ' ' && TryTagValueAt(text, at + 1, out tag, out value))
            {
                entity = start..at;
                return true;
            }
        }

        return false;
    }

    private static bool TryTagValueAt(string text, int start, out Range tag, out Range value)
    {
        tag = value = default;
        var s = text.AsSpan();
        if (!s[start..].StartsWith("tag=", StringComparison.Ordinal))
        {
            return false;
        }

        var i = start + "tag=".Length;
        var tagStart = i;
        while (i < s.Length && !char.IsWhiteSpace(s[i]))
        {
            i++;
        }

        if (i == tagStart || !s[i..].StartsWith(" value=", StringComparison.Ordinal))
        {
            return false;
        }

        tag = tagStart..i;
        i += " value=".Length;
        var valueStart = i;
        while (i < s.Length && !char.IsWhiteSpace(s[i]))
        {
            i++;
        }

        value = valueStart..i;
        return true;
    }

    private static bool Digits(ReadOnlySpan<char> s, ref int i, int count)
    {
        for (var n = 0; n < count; n++, i++)
        {
            if (i >= s.Length || !char.IsDigit(s[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool Char(ReadOnlySpan<char> s, ref int i, char c)
    {
        if (i >= s.Length || s[i] != c)
        {
            return false;
        }

        i++;
        return true;
    }

    /// <summary>The regex \w: letters, non-spacing marks, decimal digits and connector punctuation.</summary>
    private static bool IsWordChar(char c) =>
        char.GetUnicodeCategory(c) is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter
            or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter
            or UnicodeCategory.NonSpacingMark or UnicodeCategory.DecimalDigitNumber or UnicodeCategory.ConnectorPunctuation;
}
