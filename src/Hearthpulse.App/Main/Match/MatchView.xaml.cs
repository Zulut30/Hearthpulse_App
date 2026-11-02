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

    public MatchView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) => Watch(e.NewValue as MatchViewModel);
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
        }
        else if (e.PropertyName == nameof(MatchViewModel.Actions) && _model is { ShowBattlegroundsTable: true })
        {
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, KeepCurrentActionInView);
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

    /// <summary>The Battlegrounds list of events scrolls on its own to keep the current row in view (not the page).</summary>
    private void KeepCurrentActionInView()
    {
        var index = _model?.Actions.ToList().FindIndex(a => a.Current) ?? -1;
        if (index < 0 || BgActions.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement { IsVisible: true } row || !BgActionsScroll.IsVisible)
        {
            return;
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
    }
}
