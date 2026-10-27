namespace Hearthpulse.Core.Power;

/// <summary>
/// One shared copy of each tag name, short tag value, card id and entity name Power.log repeats line after line
/// ("ZONE", "HAND", "1", "CS2_029"...). Every entity keeps every tag it saw, so a game held thousands of copies of the
/// same few hundred strings, and each line allocated new ones. Bounded: longer and later strings are not pooled.
/// </summary>
internal sealed class TagStrings
{
    internal const int MaxLength = 64;
    internal const int MaxCount = 16_384;

    private readonly Dictionary<string, string> _strings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> _lookup;

    public TagStrings()
    {
        _lookup = _strings.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    public int Count => _strings.Count;

    /// <summary>The pooled string equal to <paramref name="text"/>; a new one when it is not pooled (yet).</summary>
    public string Get(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
        {
            return string.Empty;
        }

        if (_lookup.TryGetValue(text, out var known))
        {
            return known;
        }

        var value = text.ToString();
        if (value.Length <= MaxLength && _strings.Count < MaxCount)
        {
            _strings.Add(value, value);
        }

        return value;
    }
}
