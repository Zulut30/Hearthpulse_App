using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Hearthpulse.Core.Localization;

namespace Hearthpulse.App.Main.Match;

public partial class MatchView : UserControl
{
    private MatchViewModel? _model;
    private ScrollViewer? _feedScroll;

    // The feed is bringing the step's row into its own view: the request must not reach the page's scroll viewer.
    private bool _feedBringsRow;
    private VirtualizingStackPanel? _feedPanel;

    public MatchView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) => Watch(e.NewValue as MatchViewModel);
        // The feed brings a row that is not built into its own view: the request stops at the feed's card, or the
        // page's scroll viewer would follow it (the feed's passes it on) and the page would jump under the player.
        FeedCard.RequestBringIntoView += (_, e) => e.Handled |= _feedBringsRow;
        // Hidden with the window too (to the tray): the playback stops instead of drawing steps nobody sees.
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible)
            {
                _model?.StopPlayback();
            }

            FocusPlayer();
        };
    }

    /// <summary>
    /// The player's keys on the Replay tab, before the focused button or radio sees them (Space must not click it):
    /// Space plays or pauses, the arrows go by event and with Ctrl by turn, Enter and Backspace by step, + and − change
    /// the speed, G opens the "Turn" list, M goes to the mulligan, B bookmarks the step, [ and ] go to the bookmarks
    /// around it. Esc and Alt+Left go on to the window (back to History). While the "Turn" list is open (its keys come
    /// through here from its popup) they are the list's. Letters by their key, so the Russian layout works the same.
    /// </summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled || _model is not { ShowReplay: true } model || model.TurnsOpen || TurnBox.IsDropDownOpen)
        {
            return;
        }

        if (e.Key == Key.G && Keyboard.Modifiers == ModifierKeys.None)
        {
            OpenTurns(model);
            e.Handled = true;
            return;
        }

        // Shift+B: the "Bookmarks" menu, its first entry focused (the arrows move, Enter goes, Esc closes).
        if (e.Key == Key.B && Keyboard.Modifiers == ModifierKeys.Shift && model.CanBookmark)
        {
            OnBookmarksMenu(BookmarksButton, e);
            e.Handled = true;
            return;
        }

        var command = (e.Key, Keyboard.Modifiers) switch
        {
            (Key.Space, ModifierKeys.None) => model.TogglePlay,
            (Key.Left, ModifierKeys.None) => model.PreviousEvent,
            (Key.Right, ModifierKeys.None) => model.NextEvent,
            (Key.Left, ModifierKeys.Control) => model.PreviousTurn,
            (Key.Right, ModifierKeys.Control) => model.NextTurn,
            (Key.Enter, ModifierKeys.None) => model.Next,
            (Key.Back, ModifierKeys.None) => model.Previous,
            (Key.OemPlus or Key.Add, ModifierKeys.None or ModifierKeys.Shift) => model.Faster,
            (Key.OemMinus or Key.Subtract, ModifierKeys.None or ModifierKeys.Shift) => model.Slower,
            (Key.M, ModifierKeys.None) => model.GoMulligan,
            (Key.B, ModifierKeys.None) => model.ToggleBookmark,
            (Key.OemOpenBrackets, ModifierKeys.None) => model.PreviousBookmark,
            (Key.OemCloseBrackets, ModifierKeys.None) => model.NextBookmark,
            _ => null,
        };
        if (command is not null)
        {
            command.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>G: the "Turn" list opens with the keyboard in it (the arrows move through it, Enter goes, Esc closes).</summary>
    private void OpenTurns(MatchViewModel model)
    {
        if (model.Jumps.Count == 0)
        {
            return;
        }

        TurnBox.Focus();
        model.OpenTurns.Execute(null);
    }

    /// <summary>The "Turn" list closed (a pick, Esc or a click outside): the keys go back to the player.</summary>
    private void OnTurnsClosed(object? sender, EventArgs e) =>
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (IsVisible)
            {
                Focus();
            }
        });

    /// <summary>"Bookmarks": its menu opens under it on a click, with the bookmarks as they are now.</summary>
    private void OnBookmarksMenu(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            FillBookmarks(menu);
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    /// <summary>The same menu by a right click: filled first as well.</summary>
    private void OnBookmarksMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu })
        {
            FillBookmarks(menu);
        }
    }

    /// <summary>The bookmarks in step order (the one on screen in bold, its glyph in the accent), or a hint without any.</summary>
    private void FillBookmarks(ContextMenu menu)
    {
        menu.Items.Clear();
        if (_model is not { } model)
        {
            return;
        }

        if (model.BookmarkRows.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = Loc.T("Match.Player.BookmarksEmpty"), IsEnabled = false });
            return;
        }

        foreach (var row in model.BookmarkRows)
        {
            var glyph = new TextBlock { Text = "󰃀", FontSize = 14 };
            glyph.SetResourceReference(StyleProperty, "Icon");
            glyph.SetResourceReference(TextBlock.ForegroundProperty, row.Current ? "Accent" : "Info");
            menu.Items.Add(new MenuItem
            {
                Header = row.Text,
                Icon = glyph,
                Command = model.GoBookmark,
                CommandParameter = row,
                FontWeight = row.Current ? FontWeights.SemiBold : FontWeights.Normal,
            });
        }
    }

    /// <summary>A click anywhere on the page (the table, the timeline) gives the keys back to the player.</summary>
    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseDown(e);
        if (!IsKeyboardFocusWithin)
        {
            FocusPlayer();
        }
    }

    /// <summary>The page takes the focus only for the keys: unlike other elements it does not scroll itself into view.</summary>
    protected override void OnGotFocus(RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, this))
        {
            base.OnGotFocus(e);
        }
    }

    /// <summary>
    /// A weak subscription: the view model lives with the app, and a closed main window (theme change, a while in the
    /// tray) keeps its DataContext, so a plain one would keep the window and all its pages.
    /// </summary>
    private void Watch(MatchViewModel? model)
    {
        if (_model is not null)
        {
            PropertyChangedEventManager.RemoveHandler(_model, OnModelChanged, string.Empty);
        }

        _model = model;
        if (model is not null)
        {
            PropertyChangedEventManager.AddHandler(model, OnModelChanged, string.Empty);
        }
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MatchViewModel.ShowReplay))
        {
            FocusPlayer();
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => KeepCurrentFeedRowInView(bringIn: true));
        }
        else if (e.PropertyName == nameof(MatchViewModel.ActionCurrentIndex) && _model is { ShowBattlegroundsTable: true })
        {
            // In the step's own frame when the row is there (a step within the stretch); a new stretch's rows are
            // built by the layout first.
            if (!KeepCurrentActionInView())
            {
                _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => KeepCurrentActionInView());
            }
        }
        else if (e.PropertyName == nameof(MatchViewModel.FeedCurrentIndex) && _model is { ShowConstructedTable: true })
        {
            // The rows stay from step to step, so the next one is usually built and placed already: the feed scrolls
            // in the step's own frame. Posted for after the layout, the scroll drew the page a second time each step.
            if (!KeepCurrentFeedRowInView(bringIn: false))
            {
                _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => KeepCurrentFeedRowInView(bringIn: true));
            }
        }
    }

    /// <summary>The feed's own scroll viewer (a part of the list's template: the list scrolls its rows, built near the view only).</summary>
    internal ScrollViewer? FeedScroll => _feedScroll ??= Feed.Template?.FindName("FeedScroll", Feed) as ScrollViewer;

    /// <summary>The feed is as tall as the table (at least its 420): its list scrolls instead of stretching the row.</summary>
    private void OnTableSized(object sender, SizeChangedEventArgs e)
    {
        FeedCard.Height = Math.Max(FeedCard.MinHeight, TableCard.ActualHeight);
        // A shorter feed may have cut the step's row off.
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => KeepCurrentFeedRowInView(bringIn: true));
    }

    /// <summary>
    /// The event feed scrolls on its own (not the page) to keep the step's row in view, with its turn's header when it is
    /// the turn's first action (<see cref="MatchFeedLayout"/>). The row is found by its index: one that is not built
    /// (far from the view) is brought in by the list's panel first, when <paramref name="bringIn"/> allows a layout
    /// pass here. False when the row could not be placed yet (the feed is not laid out, or the row is not built).
    /// </summary>
    internal bool KeepCurrentFeedRowInView(bool bringIn = true)
    {
        if (_model is not { FeedCurrentIndex: >= 0 and var index } model || index >= model.FeedRows.Count)
        {
            return true;
        }

        if (FeedScroll is not { } scroll || !IsLaidOut(scroll))
        {
            return false;
        }

        var firstIndex = index > 0 && model.FeedRows[index - 1].Header ? index - 1 : index;
        if (FeedRow(index) is null || FeedRow(firstIndex) is null)
        {
            if (!bringIn || FeedPanel() is not { } panel)
            {
                return false;
            }

            _feedBringsRow = true;
            try
            {
                panel.BringIndexIntoViewPublic(index);
                if (firstIndex != index)
                {
                    panel.BringIndexIntoViewPublic(firstIndex);
                }

                Feed.UpdateLayout();
            }
            finally
            {
                _feedBringsRow = false;
            }
        }

        if (FeedRow(index) is not { } row || FeedRow(firstIndex) is not { } first)
        {
            return false;
        }

        var top = first.TransformToAncestor(scroll).Transform(default).Y;
        var bottom = row.TransformToAncestor(scroll).Transform(default).Y + row.ActualHeight;
        if (MatchFeedLayout.Offset(scroll.VerticalOffset, scroll.ViewportHeight, top, bottom) is { } offset)
        {
            scroll.ScrollToVerticalOffset(offset);
        }

        return true;
    }

    /// <summary>The built row of a feed index, or null (not built: away from the view).</summary>
    private FrameworkElement? FeedRow(int index) =>
        Feed.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement { Visibility: Visibility.Visible } row && IsLaidOut(row) ? row : null;

    // Laid out, on screen or off it (renders and tests have no window to be visible in).
    private static bool IsLaidOut(FrameworkElement element) => element.IsArrangeValid && element.ActualHeight > 0;

    private VirtualizingStackPanel? FeedPanel()
    {
        if (_feedPanel is null && FeedScroll is { } scroll)
        {
            _feedPanel = Find(scroll);
        }

        return _feedPanel;

        static VirtualizingStackPanel? Find(DependencyObject node)
        {
            for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(node); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(node, i);
                if (child is VirtualizingStackPanel panel)
                {
                    return panel;
                }

                if (Find(child) is { } found)
                {
                    return found;
                }
            }

            return null;
        }
    }

    /// <summary>The Replay tab on screen: the page takes the keyboard focus (unless a control in it already has it).</summary>
    private void FocusPlayer()
    {
        if (_model is { ShowReplay: true } && IsVisible && !IsKeyboardFocusWithin)
        {
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, () => Focus());
        }
    }

    /// <summary>
    /// The Battlegrounds list of events scrolls on its own to keep the current row in view (not the page). False when
    /// the row is not built yet.
    /// </summary>
    private bool KeepCurrentActionInView()
    {
        var index = _model?.ActionCurrentIndex ?? -1;
        if (index < 0)
        {
            return true;
        }

        if (BgActions.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement { IsVisible: true } row || !BgActionsScroll.IsVisible || !IsLaidOut(row))
        {
            return false;
        }

        var top = row.TransformToAncestor(BgActionsScroll).Transform(new Point(0, 0)).Y;
        if (top < 0)
        {
            BgActionsScroll.ScrollToVerticalOffset(BgActionsScroll.VerticalOffset + top);
        }
        else if (top + row.ActualHeight > BgActionsScroll.ViewportHeight)
        {
            BgActionsScroll.ScrollToVerticalOffset(BgActionsScroll.VerticalOffset + top + row.ActualHeight - BgActionsScroll.ViewportHeight);
        }

        return true;
    }
}
