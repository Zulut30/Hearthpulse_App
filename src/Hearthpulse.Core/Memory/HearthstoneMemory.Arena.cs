namespace Hearthpulse.Core.Memory;

public sealed partial class HearthstoneMemory
{
    /// <summary>
    /// Card ids of the offer on the Arena draft screen (three heroes, then three cards per pick), read from the
    /// client's draft screen (<c>DraftDisplay.s_instance.m_choices</c>, each choice's <c>m_cardID</c>). Null when the
    /// screen is not open, the offer is still being dealt (a partial list) or the client moved these fields. The cards
    /// are face up on the player's screen, so this is no hidden information. Read only, like every other value here.
    /// Not yet checked against the current client (docs/arena-draft.md).
    /// </summary>
    public IReadOnlyList<string>? GetArenaDraftChoices() => Safe(quiet: true, read: rt =>
    {
        var display = rt.FindClass("DraftDisplay")?.GetStatic("s_instance") ?? default;
        if (display.IsNull)
        {
            return null;
        }

        var choices = display["m_choices"];
        if (choices.IsNull)
        {
            return null;
        }

        var ids = new List<string>(3);
        foreach (var choice in choices.ListItems())
        {
            var id = choice.String("m_cardID");
            if (string.IsNullOrEmpty(id))
            {
                // A choice without its card yet: the offer is being dealt, and a partial one would put scores on the
                // wrong cards.
                return null;
            }

            ids.Add(id);
        }

        return ids.Count is >= 2 and <= 4 ? ids : null;
    });
}
