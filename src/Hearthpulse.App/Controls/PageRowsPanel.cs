using System.Collections.Specialized;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace Hearthpulse.App.Controls;

/// <summary>
/// Items panel for a list of equal-height rows inside a scrolling page: it builds only the rows near what the page's
/// ScrollViewer shows and keeps the room of the others, so the page scrolls and measures as with a StackPanel.
/// A VirtualizingStackPanel there gets an endless height and builds every row at once: the meta tier list (about 100
/// archetypes) spent 600 of its 900 ms first opening on that. A screen of rows above and below what is shown follows
/// at background priority in short time slices, ahead of the wheel (a row costs several milliseconds to build, so
/// building rows on the wheel made scrolling stutter); built rows stay, rows nobody scrolls to are never built.
/// Rows must all have the same height: the tallest one built so far sets it.
/// </summary>
public sealed class PageRowsPanel : VirtualizingPanel
{
    /// <summary>Rows built past each edge of the page's viewport, so a wheel notch shows rows that already exist.</summary>
    private const int Spare = 6;

    /// <summary>Time one background turn of the dispatcher spends building rows: input waits for about this long.</summary>
    private const int BuildSliceMs = 12;

    private ScrollViewer? _page;
    private double _row = double.NaN;
    private int _first = -1;
    private int _last = -1;
    private bool _aheadQueued;

    public PageRowsPanel()
    {
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
    }

    /// <summary>
    /// Scrolls <paramref name="page"/> through once and back, so every panel of this kind on it builds all its rows. For
    /// whole-page shots (UI render check): without it the room of the rows nobody scrolled to shows as empty space.
    /// </summary>
    public static void BuildAllRows(ScrollViewer page)
    {
        var start = page.VerticalOffset;
        for (var y = 0.0; ; y += Math.Max(100, page.ViewportHeight))
        {
            page.ScrollToVerticalOffset(y);
            // The first pass moves the page and raises ScrollChanged; the second builds the rows it asked for.
            page.UpdateLayout();
            page.UpdateLayout();
            if (y >= page.ScrollableHeight)
            {
                break;
            }
        }

        page.ScrollToVerticalOffset(start);
        page.UpdateLayout();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var children = InternalChildren; // creates the generator
        var generator = ItemContainerGenerator;
        var count = ItemsControl.GetItemsOwner(this)?.Items.Count ?? 0;
        if (count == 0 || generator is null)
        {
            CleanUp(0, -1);
            _first = _last = -1;
            return default;
        }

        Attach();
        var rowSize = new Size(availableSize.Width, double.PositiveInfinity);
        if (double.IsNaN(_row))
        {
            // One row first: its height tells which rows the viewport holds.
            Realize(0, 0, rowSize);
        }

        (_first, _last) = Wanted(count);
        Realize(_first, _last, rowSize);
        var width = 0.0;
        foreach (UIElement child in children)
        {
            // Rows outside the viewport are measured too (a no-op unless the width changed: their text trims again).
            child.Measure(rowSize);
            width = Math.Max(width, child.DesiredSize.Width);
        }

        QueueAheadBuild();
        return new Size(width, count * _row);
    }

    /// <summary>
    /// Builds the rows of <see cref="Ahead"/> that do not exist yet at background priority: below input, and unlike
    /// idle priority it is not held off by the overlay's 33 ms timer during a game.
    /// </summary>
    private void QueueAheadBuild()
    {
        if (_aheadQueued || FirstMissing(Ahead()) < 0)
        {
            return;
        }

        _aheadQueued = true;
        Dispatcher.InvokeAsync(() =>
        {
            _aheadQueued = false;
            // A hidden page builds nothing: it carries on when it is measured again.
            if (!IsVisible || double.IsNaN(_row))
            {
                return;
            }

            // Rows until the time slice is used (the first always fits): fewer, fuller passes cost less layout and rendering
            // than a pass per couple of rows, and input still waits no longer than about one frame.
            var range = Ahead();
            var slice = Stopwatch.StartNew();
            for (var index = FirstMissing(range); index >= 0 && slice.ElapsedMilliseconds < BuildSliceMs; index = FirstMissing(range))
            {
                Realize(index, index, new Size(RenderSize.Width, double.PositiveInfinity));
            }

            // Measure again: it places the new rows and queues the next ones.
            InvalidateMeasure();
        }, DispatcherPriority.Background);
    }

    /// <summary>The shown rows (<see cref="Wanted"/>) and as many again above and below; empty without items.</summary>
    private (int First, int Last) Ahead()
    {
        var count = ItemsControl.GetItemsOwner(this)?.Items.Count ?? 0;
        if (count == 0 || double.IsNaN(_row))
        {
            return (0, -1);
        }

        var (first, last) = Wanted(count);
        var span = last - first + 1;
        return (Math.Max(0, first - span), Math.Min(count - 1, last + span));
    }

    /// <summary>The first row of the range that is not built, or -1.</summary>
    private int FirstMissing((int First, int Last) range)
    {
        for (var index = range.First; index <= range.Last; index++)
        {
            if (ItemContainerGenerator.GeneratorPositionFromIndex(index).Offset != 0)
            {
                return index;
            }
        }

        return -1;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var generator = ItemContainerGenerator;
        var children = InternalChildren;
        for (var i = 0; i < children.Count; i++)
        {
            var index = generator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            children[i].Arrange(new Rect(0, index * _row, finalSize.Width, _row));
        }

        return finalSize;
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        base.OnItemsChanged(sender, args);
        switch (args.Action)
        {
            case NotifyCollectionChangedAction.Remove:
            case NotifyCollectionChangedAction.Replace:
            case NotifyCollectionChangedAction.Move:
                RemoveInternalChildRange(args.Position.Index, args.ItemUICount);
                break;
            case NotifyCollectionChangedAction.Reset:
                // A new list: its rows are built again, and may come with another template, so their height is measured again.
                RemoveInternalChildRange(0, InternalChildren.Count);
                _row = double.NaN;
                break;
        }
    }

    /// <summary>Keyboard navigation and ScrollIntoView reach a row that is not built: scroll the page to it.</summary>
    protected override void BringIndexIntoView(int index)
    {
        if (_page is null || double.IsNaN(_row) || !_page.IsAncestorOf(this))
        {
            return;
        }

        var top = TransformToAncestor(_page).Transform(new Point(0, index * _row)).Y;
        if (top < 0)
        {
            _page.ScrollToVerticalOffset(_page.VerticalOffset + top);
        }
        else if (top + _row > _page.ViewportHeight)
        {
            _page.ScrollToVerticalOffset(_page.VerticalOffset + top + _row - _page.ViewportHeight);
        }
    }

    /// <summary>The rows the page's viewport shows, with <see cref="Spare"/> on each side; at least one row.</summary>
    private (int First, int Last) Wanted(int count)
    {
        // Before a page is first arranged its viewport is unknown: the window's height from the panel's top.
        var window = Window.GetWindow(this);
        var viewport = _page is { ViewportHeight: > 0 } page ? page.ViewportHeight
            : window is { ActualHeight: > 0 } ? window.ActualHeight
            : window is { Height: > 0 and < double.PositiveInfinity } ? window.Height
            : SystemParameters.PrimaryScreenHeight;
        var top = 0.0;
        if (_page is not null && _page.IsAncestorOf(this))
        {
            top = TransformToAncestor(_page).Transform(default).Y;
        }

        var row = Math.Max(1, _row);
        var first = (int)Math.Floor(-top / row) - Spare;
        var last = (int)Math.Ceiling((viewport - top) / row) + Spare;
        first = Math.Clamp(first, 0, count - 1);
        return (first, Math.Clamp(last, first, count - 1));
    }

    private void Realize(int first, int last, Size rowSize)
    {
        var generator = ItemContainerGenerator;
        var children = InternalChildren;
        var start = generator.GeneratorPositionFromIndex(first);
        var childIndex = start.Offset == 0 ? start.Index : start.Index + 1;
        using (generator.StartAt(start, GeneratorDirection.Forward, allowStartAtRealizedItem: true))
        {
            for (var index = first; index <= last; index++, childIndex++)
            {
                if (generator.GenerateNext(out var isNew) is not UIElement child)
                {
                    break;
                }

                if (isNew)
                {
                    if (childIndex >= children.Count)
                    {
                        AddInternalChild(child);
                    }
                    else
                    {
                        InsertInternalChild(childIndex, child);
                    }

                    generator.PrepareItemContainer(child);
                }

                child.Measure(rowSize);
                _row = double.IsNaN(_row) ? child.DesiredSize.Height : Math.Max(_row, child.DesiredSize.Height);
            }
        }
    }

    /// <summary>Drops the rows outside <paramref name="first"/>..<paramref name="last"/>.</summary>
    private void CleanUp(int first, int last)
    {
        var generator = ItemContainerGenerator;
        var children = InternalChildren;
        for (var i = children.Count - 1; i >= 0; i--)
        {
            var position = new GeneratorPosition(i, 0);
            var index = generator.IndexFromGeneratorPosition(position);
            if (index < first || index > last)
            {
                generator.Remove(position, 1);
                RemoveInternalChildRange(i, 1);
            }
        }
    }

    private void Attach()
    {
        if (_page is not null)
        {
            return;
        }

        for (var parent = VisualTreeHelper.GetParent(this); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is ScrollViewer page)
            {
                _page = page;
                page.ScrollChanged += OnPageScrolled;
                return;
            }
        }
    }

    private void Detach()
    {
        if (_page is not null)
        {
            _page.ScrollChanged -= OnPageScrolled;
            _page = null;
        }
    }

    private void OnPageScrolled(object sender, ScrollChangedEventArgs e)
    {
        // Inner scroll viewers' events bubble here too; a hidden tab's list has nothing to show.
        if (e.OriginalSource != _page || !IsVisible || double.IsNaN(_row))
        {
            return;
        }

        // Built rows stay: measure again only when the viewport reaches a row the build ahead has not made yet;
        // otherwise only keep building ahead of where the page now is.
        var count = ItemsControl.GetItemsOwner(this)?.Items.Count ?? 0;
        if (count == 0 || InternalChildren.Count >= count)
        {
            return;
        }

        if (FirstMissing(Wanted(count)) >= 0)
        {
            InvalidateMeasure();
        }
        else
        {
            QueueAheadBuild();
        }
    }
}
