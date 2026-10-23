using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Hearthpulse.App.Infrastructure;

namespace Hearthpulse.App.Controls;

/// <summary>Where the hover preview opens relative to the hovered element.</summary>
public enum CardPreviewPlacement
{
    /// <summary>Beside the panel that holds the element, on the side facing the screen centre (deck and counter rows).</summary>
    Beside,

    /// <summary>Under the element itself (small marks along the top of the screen).</summary>
    Below,
}

/// <summary>
/// Hover preview of a whole card. Any element sets <c>CardPreview.CardId</c> (and optionally <c>Caption</c>); while
/// the mouse is over it, the <see cref="CardPreviewHost"/> of the same window shows the card render next to it. One
/// preview per window: moving from row to row swaps the card in place instead of closing and reopening it, so the
/// preview does not flicker. Elements inside the click-through overlay also need <c>OverlayHit.Interactive</c>.
/// </summary>
public static class CardPreview
{
    public static readonly DependencyProperty CardIdProperty = DependencyProperty.RegisterAttached(
        "CardId", typeof(string), typeof(CardPreview), new PropertyMetadata(null, OnSourceChanged));

    public static readonly DependencyProperty CaptionProperty = DependencyProperty.RegisterAttached(
        "Caption", typeof(string), typeof(CardPreview), new PropertyMetadata(null, OnSourceChanged));

    public static readonly DependencyProperty PlacementProperty = DependencyProperty.RegisterAttached(
        "Placement", typeof(CardPreviewPlacement), typeof(CardPreview), new PropertyMetadata(CardPreviewPlacement.Beside));

    public static string? GetCardId(DependencyObject d) => (string?)d.GetValue(CardIdProperty);

    public static void SetCardId(DependencyObject d, string? value) => d.SetValue(CardIdProperty, value);

    public static string? GetCaption(DependencyObject d) => (string?)d.GetValue(CaptionProperty);

    public static void SetCaption(DependencyObject d, string? value) => d.SetValue(CaptionProperty, value);

    public static CardPreviewPlacement GetPlacement(DependencyObject d) => (CardPreviewPlacement)d.GetValue(PlacementProperty);

    public static void SetPlacement(DependencyObject d, CardPreviewPlacement value) => d.SetValue(PlacementProperty, value);

    /// <summary>Whether the element has anything to preview.</summary>
    public static bool HasPreview(DependencyObject d) => !string.IsNullOrEmpty(GetCardId(d)) || !string.IsNullOrEmpty(GetCaption(d));

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        element.MouseEnter -= OnEnter;
        element.MouseLeave -= OnLeave;
        element.MouseEnter += OnEnter;
        element.MouseLeave += OnLeave;

        // Fetch the render while the list is on screen, so the first hover does not wait for the download.
        if (e.Property == CardIdProperty && e.NewValue is string { Length: > 0 } cardId)
        {
            _ = CardArt.GetAsync(ArtKind.Card, cardId);
        }
    }

    private static void OnEnter(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement element && HasPreview(element))
        {
            CardPreviewHost.For(element)?.Show(element);
        }
    }

    private static void OnLeave(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            CardPreviewHost.For(element)?.HideSoon(element);
        }
    }
}

/// <summary>
/// The layer that draws the hover preview: a transparent canvas laid over the whole overlay surface (last child, so
/// it sits on top). It never takes the mouse. The preview is placed beside the hovered element's panel, so it never
/// covers the row being read, and is pulled back inside the canvas near the screen edges.
/// </summary>
public sealed class CardPreviewHost : Canvas
{
    private const double CardWidth = 236;
    private const double CardHeight = CardWidth * 388 / 256;
    private const double Gap = 10;

    private static readonly List<WeakReference<CardPreviewHost>> Hosts = new();

    private readonly Border _frame;
    private readonly TextBlock _caption;
    private readonly Image _image;
    private readonly DispatcherTimer _hide;
    private FrameworkElement? _target;

    public CardPreviewHost()
    {
        IsHitTestVisible = false;
        _caption = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 2, 6), MaxWidth = CardWidth };
        _caption.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        _image = new Image { Width = CardWidth, Height = CardHeight, Stretch = Stretch.Uniform };
        var stack = new StackPanel { Width = CardWidth };
        stack.Children.Add(_caption);
        stack.Children.Add(_image);
        _frame = new Border { Padding = new Thickness(8), Child = stack, Visibility = Visibility.Collapsed };
        _frame.SetResourceReference(StyleProperty, "OverlayCard");
        Children.Add(_frame);

        // A short grace period: the mouse crossing the gap between two rows must not close and reopen the preview.
        _hide = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        _hide.Tick += (_, _) => Hide();
        Loaded += (_, _) => Register(this);
        Unloaded += (_, _) => Unregister(this);
    }

    /// <summary>The host in the same window as <paramref name="element"/>, if that window has one.</summary>
    public static CardPreviewHost? For(DependencyObject element)
    {
        var window = Window.GetWindow(element);
        lock (Hosts)
        {
            foreach (var reference in Hosts)
            {
                if (reference.TryGetTarget(out var host) && Window.GetWindow(host) == window)
                {
                    return host;
                }
            }
        }

        return null;
    }

    /// <summary>Shows the preview for an element carrying <see cref="CardPreview"/> properties.</summary>
    public void Show(FrameworkElement element)
    {
        _hide.Stop();
        _target = element;
        var cardId = CardPreview.GetCardId(element);
        var caption = CardPreview.GetCaption(element);
        _caption.Text = caption ?? string.Empty;
        _caption.Visibility = string.IsNullOrEmpty(caption) ? Visibility.Collapsed : Visibility.Visible;
        _image.Visibility = string.IsNullOrEmpty(cardId) ? Visibility.Collapsed : Visibility.Visible;
        CardArt.SetCard(_image, cardId);
        _frame.Visibility = Visibility.Visible;
        Place(element);
    }

    /// <summary>Hides the preview shortly, unless another element takes it over first.</summary>
    public void HideSoon(FrameworkElement element)
    {
        if (_target == element)
        {
            _hide.Stop();
            _hide.Start();
        }
    }

    /// <summary>Hides the preview now (the overlay went click-through or was hidden).</summary>
    public void Hide()
    {
        _hide.Stop();
        _target = null;
        _frame.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Shows the preview for the first visible element under <paramref name="root"/> that has one and passes
    /// <paramref name="filter"/> (off-screen renders, which have no mouse). Returns false when there is none.
    /// </summary>
    public bool ShowFirst(DependencyObject root, Func<FrameworkElement, bool>? filter = null)
    {
        if (Find(root, filter) is not { } element)
        {
            return false;
        }

        Show(element);
        return true;
    }

    private static FrameworkElement? Find(DependencyObject node, Func<FrameworkElement, bool>? filter)
    {
        if (node is FrameworkElement { IsVisible: true } element && CardPreview.HasPreview(element) && (filter is null || filter(element)))
        {
            return element;
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            if (Find(VisualTreeHelper.GetChild(node, i), filter) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private void Place(FrameworkElement element)
    {
        if (!element.IsVisible || !IsAncestorOfSafe(element))
        {
            return;
        }

        _frame.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = _frame.DesiredSize;
        var row = element.TransformToVisual(this).TransformBounds(new Rect(element.RenderSize));
        double left, top;
        if (CardPreview.GetPlacement(element) == CardPreviewPlacement.Below)
        {
            left = row.Left + (row.Width / 2) - (size.Width / 2);
            top = row.Bottom + Gap;
        }
        else
        {
            var panel = PanelOf(element);
            var anchor = panel.TransformToVisual(this).TransformBounds(new Rect(panel.RenderSize));
            var onRight = anchor.Left + (anchor.Width / 2) > ActualWidth / 2;
            left = onRight ? anchor.Left - size.Width - Gap : anchor.Right + Gap;
            top = row.Top + (row.Height / 2) - (size.Height / 2);
        }

        SetLeft(_frame, Math.Clamp(left, 4, Math.Max(4, ActualWidth - size.Width - 4)));
        SetTop(_frame, Math.Clamp(top, 4, Math.Max(4, ActualHeight - size.Height - 4)));
    }

    private bool IsAncestorOfSafe(FrameworkElement element) =>
        VisualParent is Visual parent && parent.IsAncestorOf(element);

    /// <summary>The element's top-level panel: its ancestor that sits directly on the same surface as this host.</summary>
    private FrameworkElement PanelOf(FrameworkElement element)
    {
        var surface = VisualParent;
        DependencyObject node = element;
        while (VisualTreeHelper.GetParent(node) is { } parent && parent != surface)
        {
            node = parent;
        }

        return node as FrameworkElement ?? element;
    }

    private static void Register(CardPreviewHost host)
    {
        lock (Hosts)
        {
            Hosts.RemoveAll(r => !r.TryGetTarget(out var h) || h == host);
            Hosts.Add(new WeakReference<CardPreviewHost>(host));
        }
    }

    private static void Unregister(CardPreviewHost host)
    {
        lock (Hosts)
        {
            Hosts.RemoveAll(r => !r.TryGetTarget(out var h) || h == host);
        }
    }
}
