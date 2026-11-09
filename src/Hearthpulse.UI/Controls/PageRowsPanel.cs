using System.Collections.Specialized;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Hearthpulse.UI.Controls;

/// <summary>
/// Items panel for a list of equal-height rows inside a scrolling page (the twin of the WPF panel of the same name):
/// it builds only the rows near what the page shows and keeps the room of the others, so the page scrolls and
/// measures as with a StackPanel. A plain StackPanel there builds every row at once: the meta tier list (about 100
/// archetypes) is some 1,700 visuals on its first opening. A screen of rows above and below what is shown follows at
/// background priority in short time slices, so a wheel notch shows rows that already exist; built rows stay, rows
/// nobody scrolls to are never built. Rows must all have the same height: the tallest one built so far sets it.
/// What the page shows comes from the layout's effective viewport, so the panel needs no reference to the page's
/// ScrollViewer and follows a move of the list on the page as well as a scroll.
/// </summary>
public sealed class PageRowsPanel : VirtualizingPanel
{
    /// <summary>Rows built past each edge of the viewport at once, so a small scroll step never shows a gap.</summary>
    private const int Spare = 2;

    /// <summary>Time one background turn of the dispatcher spends building rows: input waits for about this long.</summary>
    private const int BuildSliceMs = 12;

    /// <summary>The row of every item, by item index; null where it is not built.</summary>
    private readonly List<Control?> _rows = [];

    /// <summary>What the page shows, in the panel's coordinates; empty until the first layout tells, and while hidden.</summary>
    private Rect _viewport;
    private double _row = double.NaN;
    private bool _aheadQueued;

    public PageRowsPanel()
    {
        EffectiveViewportChanged += OnEffectiveViewportChanged;
    }

    /// <summary>
    /// Builds every row of the panels of this kind that <paramref name="page"/> shows. For whole-page shots (UI render
    /// check): without it the room of the rows nobody scrolled to shows as empty space.
    /// </summary>
    public static void BuildAllRows(ScrollViewer page)
    {
        foreach (var panel in page.GetVisualDescendants().OfType<PageRowsPanel>().Where(p => p.IsEffectivelyVisible).ToList())
        {
            panel.SyncRows();
            for (var index = 0; index < panel._rows.Count; index++)
            {
                panel.Realize(index);
            }

            // A virtualizing panel is not measured again for a row added to it: ask.
            panel.InvalidateMeasure();
        }

        page.UpdateLayout();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        SyncRows();
        var count = _rows.Count;
        if (count == 0)
        {
            return default;
        }

        var rowSize = new Size(availableSize.Width, double.PositiveInfinity);
        if (double.IsNaN(_row))
        {
            // One row first: its height tells which rows the viewport holds.
            Realize(0);
            MeasureRows(rowSize);
        }

        // Before the first layout the viewport is unknown: the first row (its height) is enough. The layout pass that
        // places the panel reports the viewport and measures again, so no frame shows the rows missing.
        var (first, last) = Wanted(count);
        for (var index = first; index <= last; index++)
        {
            Realize(index);
        }

        // Rows outside the viewport are measured too (a no-op unless the width changed: their text trims again).
        var width = MeasureRows(rowSize);
        QueueAheadBuild();
        return new Size(width, count * _row);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        for (var index = 0; index < _rows.Count; index++)
        {
            _rows[index]?.Arrange(new Rect(0, index * _row, finalSize.Width, _row));
        }

        return finalSize;
    }

    protected override void OnItemsControlChanged(ItemsControl? oldValue)
    {
        base.OnItemsControlChanged(oldValue);
        Clear();
    }

    protected override void OnItemsChanged(IReadOnlyList<object?> items, NotifyCollectionChangedEventArgs e)
    {
        base.OnItemsChanged(items, e);
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewItems is { } added && InRange(e.NewStartingIndex, 0):
                InsertRows(e.NewStartingIndex, added.Count);
                break;
            case NotifyCollectionChangedAction.Remove when e.OldItems is { } removed && InRange(e.OldStartingIndex, removed.Count):
                RemoveRows(e.OldStartingIndex, removed.Count);
                break;
            case NotifyCollectionChangedAction.Replace when e.OldItems is { } old && e.NewItems is { } fresh && old.Count == fresh.Count
                    && InRange(e.OldStartingIndex, old.Count) && _rows.Count == items.Count:
                for (var index = e.OldStartingIndex; index < e.OldStartingIndex + old.Count; index++)
                {
                    ReplaceRow(index, items[index]);
                }

                break;
            case NotifyCollectionChangedAction.Move when e.OldItems is { } moved && InRange(e.OldStartingIndex, moved.Count)
                    && e.NewStartingIndex >= 0 && e.NewStartingIndex <= _rows.Count - moved.Count:
                RemoveRows(e.OldStartingIndex, moved.Count);
                InsertRows(e.NewStartingIndex, moved.Count);
                break;
            default:
                // A new list: its rows are built again, and may come with another template, so their height is measured again.
                Clear();
                break;
        }

        InvalidateMeasure();
    }

    /// <summary>Keyboard navigation reaches a row that is not built: it is built and the page scrolls to it.</summary>
    protected override Control? ScrollIntoView(int index)
    {
        SyncRows();
        if (index < 0 || index >= _rows.Count)
        {
            return null;
        }

        if (_rows[index] is null)
        {
            Realize(index);
            InvalidateMeasure();
        }

        if (!double.IsNaN(_row))
        {
            this.BringIntoView(new Rect(0, index * _row, Bounds.Width, _row));
        }

        return _rows[index];
    }

    protected override Control? ContainerFromIndex(int index) => index >= 0 && index < _rows.Count ? _rows[index] : null;

    protected override int IndexFromContainer(Control container) => _rows.IndexOf(container);

    protected override IEnumerable<Control>? GetRealizedContainers() => _rows.OfType<Control>();

    protected override IInputElement? GetControl(NavigationDirection direction, IInputElement? from, bool wrap)
    {
        SyncRows();
        var count = _rows.Count;
        if (count == 0)
        {
            return null;
        }

        var current = -1;
        for (var visual = from as Visual; visual is not null && current < 0; visual = visual.GetVisualParent())
        {
            current = visual is Control control ? _rows.IndexOf(control) : -1;
        }

        var target = direction switch
        {
            NavigationDirection.First => 0,
            NavigationDirection.Last => count - 1,
            NavigationDirection.Next or NavigationDirection.Down => current + 1,
            NavigationDirection.Previous or NavigationDirection.Up => current - 1,
            _ => -1,
        };
        if (wrap && direction is NavigationDirection.Next or NavigationDirection.Down or NavigationDirection.Previous or NavigationDirection.Up)
        {
            target = ((target % count) + count) % count;
        }

        if (target < 0 || target >= count)
        {
            return null;
        }

        ScrollIntoView(target);
        return _rows[target];
    }

    private bool InRange(int start, int length) => start >= 0 && length >= 0 && start <= _rows.Count - length;

    /// <summary>Makes the row table as long as the list; a list that changed behind the panel's back starts over.</summary>
    private void SyncRows()
    {
        var count = Items.Count;
        if (_rows.Count == count)
        {
            return;
        }

        Clear();
        for (var index = 0; index < count; index++)
        {
            _rows.Add(null);
        }
    }

    private void Clear()
    {
        var generator = ItemContainerGenerator;
        foreach (var row in _rows)
        {
            if (row is not null)
            {
                RemoveInternalChild(row);
                generator?.ClearItemContainer(row);
            }
        }

        _rows.Clear();
        _row = double.NaN;
    }

    private void InsertRows(int start, int length)
    {
        _rows.InsertRange(start, Enumerable.Repeat<Control?>(null, length));
        Renumber(start + length, length);
    }

    private void RemoveRows(int start, int length)
    {
        var generator = ItemContainerGenerator;
        for (var index = start; index < start + length; index++)
        {
            if (_rows[index] is { } row)
            {
                RemoveInternalChild(row);
                generator?.ClearItemContainer(row);
            }
        }

        _rows.RemoveRange(start, length);
        Renumber(start, -length);
    }

    /// <summary>The rows from <paramref name="start"/> on moved by <paramref name="shift"/> places: their containers learn the new index.</summary>
    private void Renumber(int start, int shift)
    {
        if (ItemContainerGenerator is not { } generator)
        {
            return;
        }

        for (var index = start; index < _rows.Count; index++)
        {
            if (_rows[index] is { } row)
            {
                generator.ItemContainerIndexChanged(row, index - shift, index);
            }
        }
    }

    /// <summary>
    /// Another item in the same place: a built row keeps its container and takes the new item (the template's controls
    /// stay when the template is the same, only the bindings read the new item), which is what makes a new sort order
    /// cheap. A control that is its own row is built anew.
    /// </summary>
    private void ReplaceRow(int index, object? item)
    {
        if (_rows[index] is not { } row || ItemContainerGenerator is not { } generator)
        {
            return;
        }

        if (generator.NeedsContainer(item, index, out _) && !ReferenceEquals(row, item))
        {
            generator.PrepareItemContainer(row, item, index);
            generator.ItemContainerPrepared(row, item, index);
            return;
        }

        RemoveInternalChild(row);
        generator.ClearItemContainer(row);
        _rows[index] = null;
    }

    private void Realize(int index)
    {
        if (_rows[index] is not null || ItemContainerGenerator is not { } generator)
        {
            return;
        }

        var item = Items[index];
        var row = generator.NeedsContainer(item, index, out var recycleKey)
            ? generator.CreateContainer(item, index, recycleKey)
            : (Control)item!;
        generator.PrepareItemContainer(row, item, index);
        AddInternalChild(row);
        generator.ItemContainerPrepared(row, item, index);
        _rows[index] = row;
    }

    /// <summary>Measures the built rows; the tallest sets the row height. Returns the widest row's width.</summary>
    private double MeasureRows(Size rowSize)
    {
        var width = 0.0;
        foreach (var row in _rows)
        {
            if (row is not null)
            {
                row.Measure(rowSize);
                width = Math.Max(width, row.DesiredSize.Width);
                _row = double.IsNaN(_row) ? row.DesiredSize.Height : Math.Max(_row, row.DesiredSize.Height);
            }
        }

        return width;
    }

    /// <summary>The rows the viewport shows; empty (0, -1) while the viewport is unknown or the list is out of sight.</summary>
    private (int First, int Last) Shown(int count)
    {
        if (count == 0 || double.IsNaN(_row) || _viewport.Height <= 0)
        {
            return (0, -1);
        }

        var row = Math.Max(1, _row);
        var first = (int)Math.Floor(_viewport.Top / row);
        var last = (int)Math.Ceiling(_viewport.Bottom / row) - 1;
        return last < 0 || first >= count ? (0, -1) : (Math.Max(0, first), Math.Min(count - 1, last));
    }

    /// <summary>The shown rows with <see cref="Spare"/> on each side: built in the layout pass itself.</summary>
    private (int First, int Last) Wanted(int count) => Around(count, Spare);

    /// <summary>The shown rows and a screen of rows above and below: what the background build works through.</summary>
    private (int First, int Last) Ahead(int count)
    {
        var (first, last) = Shown(count);
        return Around(count, last - first + 1);
    }

    private (int First, int Last) Around(int count, int extra)
    {
        var (first, last) = Shown(count);
        if (last < first)
        {
            return (0, -1);
        }

        // Never more than a screen of rows on each side, so the built rows stay within three screens of them.
        extra = Math.Min(extra, last - first + 1);
        return (Math.Max(0, first - extra), Math.Min(count - 1, last + extra));
    }

    /// <summary>The first row of the range that is not built, or -1.</summary>
    private int FirstMissing((int First, int Last) range)
    {
        for (var index = range.First; index <= range.Last && index < _rows.Count; index++)
        {
            if (_rows[index] is null)
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>Builds the rows of <see cref="Ahead"/> that do not exist yet at background priority, below input.</summary>
    private void QueueAheadBuild()
    {
        if (_aheadQueued || FirstMissing(Ahead(_rows.Count)) < 0)
        {
            return;
        }

        _aheadQueued = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                _aheadQueued = false;
                // A hidden page builds nothing: it carries on when it is measured again.
                if (!IsEffectivelyVisible || double.IsNaN(_row) || _rows.Count != Items.Count)
                {
                    return;
                }

                // Rows until the time slice is used (the first always fits): fewer, fuller passes cost less layout than a
                // pass per couple of rows, and input still waits no longer than about one frame. The measure asked for
                // at the end places the new rows and queues the next ones.
                var range = Ahead(_rows.Count);
                var slice = Stopwatch.StartNew();
                for (var index = FirstMissing(range); index >= 0 && slice.ElapsedMilliseconds < BuildSliceMs; index = FirstMissing(range))
                {
                    Realize(index);
                }

                InvalidateMeasure();
            },
            DispatcherPriority.Background);
    }

    private void OnEffectiveViewportChanged(object? sender, EffectiveViewportChangedEventArgs e)
    {
        _viewport = e.EffectiveViewport;
        if (double.IsNaN(_row) || _rows.Count == 0)
        {
            return;
        }

        // Built rows stay: measure again only when the viewport reaches a row that is not built yet; otherwise only
        // keep building ahead of where the page now is.
        if (FirstMissing(Wanted(_rows.Count)) >= 0)
        {
            InvalidateMeasure();
        }
        else
        {
            QueueAheadBuild();
        }
    }
}
