#if HEARTHPULSE_WPF
using Hearthpulse.App.Infrastructure;
#else
using Hearthpulse.UI.Infrastructure;
#endif

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Main;
#else
namespace Hearthpulse.UI.Main;
#endif

/// <summary>
/// The sort of a table with clickable column heads: the column clicked last and its direction. A click on another
/// column sorts by it, numbers from the highest and names from A; a click on the same column turns the order round.
/// Rows without a value in the column ("—") stay at the bottom either way. <paramref name="changed"/> re-sorts the rows.
/// </summary>
public sealed class TableSort : Observable
{
    private readonly HashSet<string> _ascendingFirst;
    private readonly Action _changed;
    private string _column;
    private bool _descending;

    public TableSort(string column, Action changed, params string[] ascendingFirst)
    {
        _ascendingFirst = new HashSet<string>(ascendingFirst, StringComparer.Ordinal);
        _column = column;
        _descending = !_ascendingFirst.Contains(column);
        _changed = changed;
        By = new RelayCommand(p =>
        {
            if (p is string key)
            {
                Toggle(key);
            }
        });
    }

    /// <summary>Sorts by a column (its key as the parameter), or turns the order round when it is the sorted one.</summary>
    public RelayCommand By { get; }

    /// <summary>The key of the sorted column.</summary>
    public string Column { get => _column; private set => Set(ref _column, value); }

    /// <summary>Highest (or Z) first.</summary>
    public bool Descending { get => _descending; private set => Set(ref _descending, value); }

    /// <summary>Sorts by a column in its first direction (no turning round when it is already sorted by it).</summary>
    public void SortBy(string column)
    {
        Column = column;
        Descending = !_ascendingFirst.Contains(column);
        _changed();
    }

    public void Toggle(string column)
    {
        if (column == _column)
        {
            Descending = !_descending;
        }
        else
        {
            Column = column;
            Descending = !_ascendingFirst.Contains(column);
        }

        _changed();
    }

    /// <summary>
    /// The rows in this order. <paramref name="value"/> gives a row's value in a column: a number, a string, or null
    /// for none. Equal values keep the rows' own order.
    /// </summary>
    public IReadOnlyList<T> Apply<T>(IEnumerable<T> rows, Func<T, string, IComparable?> value) => Apply(rows, value, _column, _descending);

    /// <summary>The rows sorted by <paramref name="column"/> (for a build off the UI thread, with the sort read before it).</summary>
    public static IReadOnlyList<T> Apply<T>(IEnumerable<T> rows, Func<T, string, IComparable?> value, string column, bool descending)
    {
        var keyed = rows.Select(r => (Row: r, Value: value(r, column))).ToList();
        var comparer = Comparer<IComparable?>.Create((a, b) => a is string x && b is string y
            ? StringComparer.CurrentCultureIgnoreCase.Compare(x, y)
            : Comparer<IComparable?>.Default.Compare(a, b));
        var ordered = keyed.OrderBy(k => k.Value is null);
        ordered = descending ? ordered.ThenByDescending(k => k.Value, comparer) : ordered.ThenBy(k => k.Value, comparer);
        return ordered.Select(k => k.Row).ToList();
    }

    /// <summary>
    /// +1 for the row with the best value, -1 for the worst, 0 for the others: among the rows that <paramref name="counts"/>
    /// (enough games), and only when at least three of them have a value and the two ends differ.
    /// </summary>
    public static int[] BestAndWorst<T>(IReadOnlyList<T> rows, Func<T, double?> value, Func<T, bool> counts)
    {
        var marks = new int[rows.Count];
        var candidates = Enumerable.Range(0, rows.Count)
            .Where(i => counts(rows[i]) && value(rows[i]) is not null)
            .Select(i => (Index: i, Value: value(rows[i])!.Value))
            .ToList();
        if (candidates.Count < 3)
        {
            return marks;
        }

        var best = candidates.MaxBy(c => c.Value);
        var worst = candidates.MinBy(c => c.Value);
        if (best.Value > worst.Value)
        {
            marks[best.Index] = 1;
            marks[worst.Index] = -1;
        }

        return marks;
    }
}
