using Hearthpulse.Core.Replay;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Main.Match;
#else
namespace Hearthpulse.UI.Main.Match;
#endif

/// <summary>The feed of the match page: the rows are built once per game, a step only moves the mark.</summary>
public sealed partial class MatchViewModel
{
    /// <summary>The game's feed rows as built (none marked), and where each header's turn ends.</summary>
    private void SetFeedRows(IReadOnlyList<MatchFeedRow> rows)
    {
        _feedTurnEnds = new int[rows.Count];
        var end = -1;
        for (var i = rows.Count - 1; i >= 0; i--)
        {
            if (rows[i].Header)
            {
                _feedTurnEnds[i] = Math.Max(end, rows[i].Frame);
                end = -1;
            }
            else
            {
                end = Math.Max(end, rows[i].Frame);
            }
        }

        var items = new MatchFeedItem[rows.Count];
        for (var i = 0; i < items.Length; i++)
        {
            items[i] = new MatchFeedItem(rows[i]);
        }

        // The index first: the view scrolls to it when it changes, and by then the rows are the new game's.
        FeedCurrentIndex = -1;
        FeedRows = items;
    }

    /// <summary>
    /// Marks the step's row and the header of its turn (as <see cref="MatchPage.Feed"/> marks them) on the rows the view
    /// already shows: the two to four rows whose mark moves tell their bindings, the others are left alone.
    /// </summary>
    private void MoveFeed(int frame)
    {
        var index = -1;
        for (var i = 0; i < _feedRows.Count; i++)
        {
            var row = _feedRows[i];
            var current = row.Header ? frame >= row.Frame && frame <= _feedTurnEnds[i] : row.Frame == frame;
            if (current && !row.Header && index < 0)
            {
                index = i;
            }

            row.Current = current;
        }

        FeedCurrentIndex = index;
    }
}
