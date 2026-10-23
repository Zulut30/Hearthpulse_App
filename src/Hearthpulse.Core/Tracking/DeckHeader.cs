using System.Globalization;
using Hearthpulse.Core.Data;

namespace Hearthpulse.Core.Tracking;

/// <summary>The deck panel's subtitle: the player's own record with the deck, else its archetype's community win rate.</summary>
public static class DeckHeader
{
    /// <summary>Subtitle text, its tooltip, and whether it comes from community stats rather than the player's games.</summary>
    public static (string Text, string Tooltip, bool FromMeta) Describe(DeckAggregate? own, DeckMetaStat? meta)
    {
        if (own is { Games: > 0 })
        {
            return ($"{own.Wins}–{own.Losses} · {Percent(own.WinRate)} win rate",
                string.Create(CultureInfo.InvariantCulture, $"Your {own.Games} games with this deck (older lists of it included)"),
                false);
        }

        if (meta is not null)
        {
            return ($"Meta {Percent(meta.WinRate)} · {meta.Rank}",
                string.Create(CultureInfo.InvariantCulture,
                    $"No games of your own with this deck yet. {meta.Archetype}: {Percent(meta.WinRate)} win rate over {meta.Games:N0} games ({meta.Rank}, {meta.Source})."),
                true);
        }

        return ("No games with this deck yet", string.Empty, false);
    }

    private static string Percent(double rate) => (rate * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
}
