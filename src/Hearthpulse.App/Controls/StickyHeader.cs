using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Hearthpulse.App.Controls;

/// <summary>
/// Keeps a table's head in view while its rows scroll by in the page. Set <c>StickyHeader.IsEnabled</c> on the head
/// (a Border, or a Control whose template draws its Background) in the panel of its table, the rows after it. Once
/// the page has scrolled past the head's place, the head is shifted down to the top of the page's viewport, never
/// past the end of that panel, and takes the opaque Row fill so the rows pass under it.
/// </summary>
public static class StickyHeader
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(StickyHeader), new PropertyMetadata(false, OnChanged));

    private static readonly DependencyProperty PageProperty = DependencyProperty.RegisterAttached(
        "Page", typeof(ScrollViewer), typeof(StickyHeader), new PropertyMetadata(null));

    private static readonly DependencyProperty HandlerProperty = DependencyProperty.RegisterAttached(
        "Handler", typeof(ScrollChangedEventHandler), typeof(StickyHeader), new PropertyMetadata(null));

    public static bool GetIsEnabled(DependencyObject d) => (bool)d.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject d, bool value) => d.SetValue(IsEnabledProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement head)
        {
            return;
        }

        head.Loaded -= OnLoaded;
        head.Unloaded -= OnUnloaded;
        if (e.NewValue is true)
        {
            head.Loaded += OnLoaded;
            head.Unloaded += OnUnloaded;
            if (head.IsLoaded)
            {
                OnLoaded(head, new RoutedEventArgs());
            }
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        var head = (FrameworkElement)sender;
        if (head.GetValue(PageProperty) is ScrollViewer || PageOf(head) is not { } page)
        {
            return;
        }

        head.SetValue(PageProperty, page);
        head.RenderTransform = new TranslateTransform();
        Panel.SetZIndex(head, 1);
        ScrollChangedEventHandler scrolled = (_, _) => Update(head, page);
        page.ScrollChanged += scrolled;
        head.SetValue(HandlerProperty, scrolled);
        Update(head, page);
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        var head = (FrameworkElement)sender;
        if (head.GetValue(PageProperty) is ScrollViewer page && head.GetValue(HandlerProperty) is ScrollChangedEventHandler scrolled)
        {
            page.ScrollChanged -= scrolled;
        }

        head.ClearValue(PageProperty);
        head.ClearValue(HandlerProperty);
    }

    private static ScrollViewer? PageOf(DependencyObject node)
    {
        while (VisualTreeHelper.GetParent(node) is { } parent)
        {
            if (parent is ScrollViewer page)
            {
                return page;
            }

            node = parent;
        }

        return null;
    }

    private static void Update(FrameworkElement head, ScrollViewer page)
    {
        if (head.RenderTransform is not TranslateTransform shift || VisualTreeHelper.GetParent(head) is not FrameworkElement table
            || !page.IsAncestorOf(head))
        {
            return;
        }

        // Where the head sits without the shift: against the viewport's top and inside its table.
        var inPage = head.TransformToVisual(page).Transform(new Point(0, 0)).Y - shift.Y;
        var inTable = head.TransformToVisual(table).Transform(new Point(0, 0)).Y - shift.Y;
        var room = Math.Max(0, table.ActualHeight - inTable - head.ActualHeight);
        var y = Math.Clamp(-inPage, 0, room);
        shift.Y = y;
        var fill = head switch
        {
            Border => Border.BackgroundProperty,
            Control => Control.BackgroundProperty,
            _ => null,
        };
        if (fill is null)
        {
            return;
        }

        if (y > 0)
        {
            head.SetResourceReference(fill, "Row");
        }
        else
        {
            head.ClearValue(fill);
        }
    }
}
