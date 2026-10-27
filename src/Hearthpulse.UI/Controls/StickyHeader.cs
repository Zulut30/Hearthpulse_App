using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.VisualTree;
using Hearthpulse.UI.Infrastructure;

namespace Hearthpulse.UI.Controls;

/// <summary>
/// Keeps a table's head in view while its rows scroll by in the page. Set <c>StickyHeader.IsEnabled</c> on the head
/// (a Border, or a templated control that draws its Background) in the panel of its table, the rows after it. Once
/// the page has scrolled past the head's place, the head is shifted down to the top of the page's viewport, never
/// past the end of that panel, and takes the opaque Row fill so the rows pass under it.
/// </summary>
public static class StickyHeader
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("IsEnabled", typeof(StickyHeader));

    private static readonly ConditionalWeakTable<Control, Attachment> Attached = new();

    static StickyHeader()
    {
        IsEnabledProperty.Changed.AddClassHandler<Control>((head, e) =>
        {
            head.AttachedToVisualTree -= OnAttached;
            head.DetachedFromVisualTree -= OnDetached;
            Detach(head);
            if (e.NewValue is true)
            {
                head.AttachedToVisualTree += OnAttached;
                head.DetachedFromVisualTree += OnDetached;
                if (head.IsAttachedToVisualTree())
                {
                    Attach(head);
                }
            }
        });
    }

    public static bool GetIsEnabled(Control control) => control.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(Control control, bool value) => control.SetValue(IsEnabledProperty, value);

    private static void OnAttached(object? sender, VisualTreeAttachmentEventArgs e) => Attach((Control)sender!);

    private static void OnDetached(object? sender, VisualTreeAttachmentEventArgs e) => Detach((Control)sender!);

    private static void Attach(Control head)
    {
        if (Attached.TryGetValue(head, out _) || head.FindAncestorOfType<ScrollViewer>() is not { } page)
        {
            return;
        }

        var shift = new TranslateTransform();
        head.RenderTransform = shift;
        head.ZIndex = 1;
        EventHandler<ScrollChangedEventArgs> scrolled = (_, _) => Update(head, page, shift);
        page.ScrollChanged += scrolled;
        Attached.AddOrUpdate(head, new Attachment(page, scrolled));
        Update(head, page, shift);
    }

    private static void Detach(Control head)
    {
        if (Attached.TryGetValue(head, out var attachment))
        {
            attachment.Page.ScrollChanged -= attachment.Scrolled;
            Attached.Remove(head);
        }
    }

    private static void Update(Control head, ScrollViewer page, TranslateTransform shift)
    {
        if (head.GetVisualParent() is not Visual table || table.TranslatePoint(new Point(0, head.Bounds.Y), page) is not { } inPage)
        {
            return;
        }

        // Bounds are the head's place without the shift: against the viewport's top and inside its table.
        var room = Math.Max(0, table.Bounds.Height - head.Bounds.Y - head.Bounds.Height);
        var y = Math.Clamp(-inPage.Y, 0, room);
        shift.Y = y;
        var fill = head switch
        {
            Border => Border.BackgroundProperty,
            TemplatedControl => TemplatedControl.BackgroundProperty,
            _ => null,
        };
        if (fill is null)
        {
            return;
        }

        if (y > 0)
        {
            head.SetValue(fill, ThemeBrushes.Get("Row"));
        }
        else
        {
            head.ClearValue(fill);
        }
    }

    private sealed record Attachment(ScrollViewer Page, EventHandler<ScrollChangedEventArgs> Scrolled);
}
