using System.Collections.ObjectModel;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Infrastructure;
#else
namespace Hearthpulse.UI.Infrastructure;
#endif

/// <summary>
/// A list a view binds once and that changes in place. A new list for every change made the items control throw away
/// and rebuild every row: on the overlay that cost about 23 ms of layout for 24 played cards and 8 ms for four counter
/// chips, each time one card was played or one counter moved. <see cref="Sync"/> touches only the rows that differ, so
/// the control keeps the visuals of the others. Items must have value equality (records); use it on the UI thread.
/// </summary>
public sealed class SyncList<T> : ObservableCollection<T>
{
    public SyncList()
    {
    }

    public SyncList(IEnumerable<T> items)
        : base(items)
    {
    }

    /// <summary>Makes this list equal to <paramref name="items"/>; false when it already was.</summary>
    public bool Sync(IReadOnlyList<T> items)
    {
        var comparer = EqualityComparer<T>.Default;
        var start = 0;
        while (start < Count && start < items.Count && comparer.Equals(this[start], items[start]))
        {
            start++;
        }

        var oldEnd = Count;
        var newEnd = items.Count;
        while (oldEnd > start && newEnd > start && comparer.Equals(this[oldEnd - 1], items[newEnd - 1]))
        {
            oldEnd--;
            newEnd--;
        }

        if (start == oldEnd && start == newEnd)
        {
            return false;
        }

        // Between the equal head and tail: rows that differ are replaced where they stand (a draw changes the odds of
        // scattered deck rows), then the surplus is removed or the rest inserted.
        var overlap = Math.Min(oldEnd, newEnd) - start;
        for (var i = start; i < start + overlap; i++)
        {
            if (!comparer.Equals(this[i], items[i]))
            {
                this[i] = items[i];
            }
        }

        for (var i = oldEnd - 1; i >= start + overlap; i--)
        {
            RemoveAt(i);
        }

        for (var i = start + overlap; i < newEnd; i++)
        {
            Insert(i, items[i]);
        }

        return true;
    }
}
