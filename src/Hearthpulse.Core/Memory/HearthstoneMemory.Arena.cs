using Hearthpulse.Core.Arena;

namespace Hearthpulse.Core.Memory;

public sealed partial class HearthstoneMemory
{
    /// <summary>
    /// The offer on the Arena draft screen (three heroes, three hero powers, then three cards per pick), read from the
    /// client's draft screen: <c>DraftDisplay.s_instance.m_choices</c>, each choice's <c>m_cardID</c> and
    /// <c>m_packageCardIds</c> (the cards that come with a legendary), and <c>m_choicesAreUnderground</c>. Null when the
    /// screen is not open, the offer is still being dealt (a partial list) or the client moved these fields. Not yet
    /// checked on a live draft (docs/arena-draft.md). The cards are face up on the player's screen, so this is no
    /// hidden information. Read only, like every other value here.
    /// </summary>
    public ArenaDraftOffer? GetArenaDraftChoices() => Safe(quiet: true, read: rt =>
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
        var packages = new List<IReadOnlyList<string>>(3);
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
            packages.Add(StringList(rt, choice["m_packageCardIds"]));
        }

        if (ids.Count is < 2 or > 4)
        {
            return null;
        }

        // The flag is only known once the display has the field; an older client gives no mode.
        bool? underground = display.FieldAddress("m_choicesAreUnderground") == 0 ? null : display.Bool("m_choicesAreUnderground");
        return new ArenaDraftOffer(ids, underground, packages);
    });

    /// <summary>A List&lt;string&gt; of card ids; empty when it is null or holds something else.</summary>
    private static IReadOnlyList<string> StringList(MonoRuntime rt, MonoObject list)
    {
        if (list.IsNull)
        {
            return [];
        }

        var result = new List<string>();
        foreach (var item in list.ListItems())
        {
            if (rt.Memory.ReadMonoString(item.Address) is { Length: > 0 and < 64 } id)
            {
                result.Add(id);
            }

            if (result.Count >= 8)
            {
                break;
            }
        }

        return result;
    }
}
