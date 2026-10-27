using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Hearthpulse.App.Main;

namespace Hearthpulse.App.Controls;

/// <summary>
/// A table's column head that sorts the table: <see cref="Text"/> in the table-head look (StatsHead), and a click
/// sorts the rows by <see cref="Column"/> through <see cref="Sort"/> (again: the other way round). The sorted column
/// is lit and shows its direction. A right-aligned head keeps its text on the numbers' edge: the arrow goes before it.
/// Without a <see cref="Sort"/> it is a plain head.
/// </summary>
public sealed class SortHeader : Border
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(SortHeader), new PropertyMetadata(string.Empty, (d, _) => ((SortHeader)d).Update()));

    public static readonly DependencyProperty ColumnProperty = DependencyProperty.Register(
        nameof(Column), typeof(string), typeof(SortHeader), new PropertyMetadata(string.Empty, (d, _) => ((SortHeader)d).Update()));

    public static readonly DependencyProperty SortProperty = DependencyProperty.Register(
        nameof(Sort), typeof(TableSort), typeof(SortHeader), new PropertyMetadata(null, (d, e) => ((SortHeader)d).OnSortChanged(e.OldValue as TableSort)));

    private const string Down = "\U000F035D";
    private const string Up = "\U000F0360";

    private readonly TextBlock _text = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _arrow = new() { FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
    private readonly DockPanel _panel = new();
    private bool _listening;

    public SortHeader()
    {
        Background = Brushes.Transparent;
        Focusable = true;
        FocusVisualStyle = null;
        VerticalAlignment = VerticalAlignment.Center;
        _text.SetResourceReference(StyleProperty, "StatsHead");
        _arrow.SetResourceReference(StyleProperty, "Icon");
        _arrow.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        Child = _panel;
        Loaded += (_, _) => Listen(true);
        Unloaded += (_, _) => Listen(false);
        MouseEnter += (_, _) => Update();
        MouseLeave += (_, _) => Update();
        Update();
    }

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }

    /// <summary>The column's key in <see cref="Sort"/>.</summary>
    public string Column { get => (string)GetValue(ColumnProperty); set => SetValue(ColumnProperty, value); }

    public TableSort? Sort { get => (TableSort?)GetValue(SortProperty); set => SetValue(SortProperty, value); }

    /// <summary>True while the table is sorted by this column.</summary>
    public bool IsSorted => Sort is { } sort && sort.Column == Column;

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (Sort is { } sort)
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

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == HorizontalAlignmentProperty)
        {
            Update();
        }
    }

    private void OnSortChanged(TableSort? old)
    {
        if (old is not null && _listening)
        {
            old.PropertyChanged -= OnSortPropertyChanged;
            _listening = false;
        }

        Listen(IsLoaded);
        Update();
    }

    private void Listen(bool on)
    {
        if (Sort is not { } sort || on == _listening)
        {
            return;
        }

        if (on)
        {
            sort.PropertyChanged += OnSortPropertyChanged;
        }
        else
        {
            sort.PropertyChanged -= OnSortPropertyChanged;
        }

        _listening = on;
        Update();
    }

    private void OnSortPropertyChanged(object? sender, PropertyChangedEventArgs e) => Update();

    private void Update()
    {
        var sorted = IsSorted;
        _text.Text = Text;
        _text.TextAlignment = HorizontalAlignment == HorizontalAlignment.Right ? TextAlignment.Right : TextAlignment.Left;
        if (sorted || (Sort is not null && IsMouseOver))
        {
            _text.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        }
        else
        {
            _text.ClearValue(TextBlock.ForegroundProperty);
        }

        _arrow.Text = Sort is { Descending: false } ? Up : Down;
        _arrow.Visibility = sorted ? Visibility.Visible : Visibility.Collapsed;
        _arrow.Margin = HorizontalAlignment == HorizontalAlignment.Right ? new Thickness(0, 0, 1, 0) : new Thickness(1, 0, 0, 0);
        Cursor = Sort is null ? null : Cursors.Hand;
        AutomationProperties.SetName(this, Text);

        // The arrow docks on the side away from the numbers' edge; the text takes the rest and wraps in a narrow column.
        if (_panel.Children.Count == 0)
        {
            _panel.Children.Add(_arrow);
            _panel.Children.Add(_text);
        }

        DockPanel.SetDock(_arrow, HorizontalAlignment == HorizontalAlignment.Right ? Dock.Left : Dock.Right);
    }
}
