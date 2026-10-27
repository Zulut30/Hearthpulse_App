using Hearthpulse.Core.Tracking;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Overlay;
#else
namespace Hearthpulse.UI.Overlay;
#endif

/// <summary>Deck panel extras: where the record under the deck name comes from, and which rows just changed.</summary>
public sealed partial class OverlayViewModel
{
    private readonly Dictionary<string, int> _deckLeft = new();
    private string _deckRecordTip = string.Empty;

    /// <summary>Hover text for the record line: whose games it counts, or which community stats it quotes.</summary>
    public string DeckRecordTip { get => _deckRecordTip; private set => Set(ref _deckRecordTip, value); }

    private static string DeckKey(DeckCardRow row) => row.IsAdded ? row.CardId + "+" : row.CardId;

    /// <summary>
    /// Rows whose remaining count moved since the previous deck rebuild. The first deck of a game marks nothing, so the
    /// whole list does not flash when a match starts; the counts are forgotten when the overlay leaves the game.
    /// </summary>
    private HashSet<string> DeckChanges(IReadOnlyList<DeckCardRow> rows)
    {
        var now = new Dictionary<string, int>();
        foreach (var row in rows)
        {
            now[DeckKey(row)] = now.GetValueOrDefault(DeckKey(row)) + row.Remaining;
        }

        var changed = _deckLeft.Count == 0
            ? []
            : now.Where(p => !_deckLeft.TryGetValue(p.Key, out var was) || was != p.Value).Select(p => p.Key).ToHashSet();
        _deckLeft.Clear();
        foreach (var (key, left) in now)
        {
            _deckLeft[key] = left;
        }

        return changed;
    }
}
