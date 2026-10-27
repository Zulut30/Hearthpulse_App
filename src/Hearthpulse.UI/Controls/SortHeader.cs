using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Hearthpulse.UI.Infrastructure;
using Hearthpulse.UI.Main;

namespace Hearthpulse.UI.Controls;

/// <summary>
/// A table's column head that sorts the table: <see cref="Text"/> in the table-head look, and a click sorts the
/// rows by <see cref="Column"/> through <see cref="Sort"/> (again: the other way round). The sorted column is lit and
/// shows its direction. A right-aligned head keeps its text on the numbers' edge: the arrow goes before it. Without
/// a <see cref="Sort"/> it is a plain head.
/// </summary>
public sealed class SortHeader : Border
{
    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<SortHeader, string>(nameof(Text), string.Empty);

    public static readonly StyledProperty<string> ColumnProperty =
        AvaloniaProperty.Register<SortHeader, string>(nameof(Column), string.Empty);

    public static readonly StyledProperty<TableSort?> SortProperty =
        AvaloniaProperty.Register<SortHeader, TableSort?>(nameof(Sort));

    private const string Down = "\U000F035D";
    private const string Up = "\U000F0360";

    private readonly TextBlock _text = new()
    {
        FontSize = 11.5, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
    };

    private readonly TextBlock _arrow = new() { FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
    private readonly DockPanel _panel = new();
    private TableSort? _listened;

    public SortHeader()
    {
        Background = Brushes.Transparent;
        Focusable = true;
        VerticalAlignment = VerticalAlignment.Center;
        _arrow.Classes.Add("icon");
        _panel.Children.Add(_arrow);
        _panel.Children.Add(_text);
        Child = _panel;
        Update();
    }

    public string Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }

    /// <summary>The column's key in <see cref="Sort"/>.</summary>
    public string Column { get => GetValue(ColumnProperty); set => SetValue(ColumnProperty, value); }

    public TableSort? Sort { get => GetValue(SortProperty); set => SetValue(SortProperty, value); }

    /// <summary>True while the table is sorted by this column.</summary>
    public bool IsSorted => Sort is { } sort && sort.Column == Column;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SortProperty)
        {
            Listen(this.IsAttachedToVisualTree());
        }

        if (change.Property == TextProperty || change.Property == ColumnProperty || change.Property == SortProperty
            || change.Property == HorizontalAlignmentProperty || change.Property == IsPointerOverProperty)
        {
            Update();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Listen(true);
        Update();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Listen(false);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (Sort is { } sort && e.InitialPressMouseButton == MouseButton.Left)
        {
            sort.Toggle(Column);
            e.Handled = true;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Sort is { } sort && e.Key is Key.Enter or Key.Space)
        {
            sort.Toggle(Column);
            e.Handled = true;
        }
    }

    private void Listen(bool on)
    {
        var sort = on ? Sort : null;
        if (ReferenceEquals(sort, _listened))
        {
            return;
        }

        if (_listened is not null)
        {
            _listened.PropertyChanged -= OnSortPropertyChanged;
        }

        _listened = sort;
        if (sort is not null)
        {
            sort.PropertyChanged += OnSortPropertyChanged;
        }
    }

    private void OnSortPropertyChanged(object? sender, PropertyChangedEventArgs e) => Update();

    private void Update()
    {
        var sorted = IsSorted;
        var right = HorizontalAlignment == HorizontalAlignment.Right;
        _text.Text = Text;
        _text.TextAlignment = right ? TextAlignment.Right : TextAlignment.Left;
        _text.Foreground = ThemeBrushes.Get(sorted || (Sort is not null && IsPointerOver) ? "Text" : "Muted");
        _arrow.Text = Sort is { Descending: false } ? Up : Down;
        _arrow.Foreground = ThemeBrushes.Get("Accent");
        _arrow.IsVisible = sorted;
        _arrow.Margin = right ? new Thickness(0, 0, 1, 0) : new Thickness(1, 0, 0, 0);
        // The arrow docks on the side away from the numbers' edge; the text takes the rest and wraps in a narrow column.
        DockPanel.SetDock(_arrow, right ? Dock.Left : Dock.Right);
        Cursor = Sort is null ? null : new Cursor(StandardCursorType.Hand);
        AutomationProperties.SetName(this, Text);
    }
}
