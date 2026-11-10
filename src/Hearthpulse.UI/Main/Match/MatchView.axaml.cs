using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Hearthpulse.Core.Localization;
using Hearthpulse.UI.Infrastructure;

namespace Hearthpulse.UI.Main.Match;

public partial class MatchView : UserControl
{
    private MatchViewModel? _model;

    // The feed is bringing the step's row into its own view: the request must not reach the page's scroll viewer.
    private bool _feedBringsRow;
    private TopLevel? _window;

    public MatchView()
    {
        InitializeComponent();
        // Tunnel: the player's keys before the focused button or radio sees them (Space must not click it).
        AddHandler(KeyDownEvent, OnPlayerKey, RoutingStrategies.Tunnel);
        // A click anywhere on the page (the table, the timeline) gives the keys back to the player.
        AddHandler(PointerPressedEvent, (_, _) =>
        {
            if (!IsKeyboardFocusWithin)
            {
                FocusPlayer();
            }
        }, RoutingStrategies.Tunnel);
        DataContextChanged += (_, _) => Watch(DataContext as MatchViewModel);
        // The feed brings a row that is not built into its own view (ScrollIntoView): the request stops at the feed, or
        // the page's scroll viewer would follow it and the page would jump under the player.
        FeedScroll.AddHandler(RequestBringIntoViewEvent, (_, e) => e.Handled |= _feedBringsRow);
        // The table's size (a step, the window, phone width) sets the feed's height.
        TableCard.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty)
            {
                SizeFeed();
            }
        };
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty)
            {
                StopWhenHidden(this);
                FocusPlayer();
            }
        };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _window = TopLevel.GetTopLevel(this);
        if (_window is not null)
        {
            _window.PropertyChanged += OnWindowChanged;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_window is not null)
        {
            _window.PropertyChanged -= OnWindowChanged;
            _window = null;
        }

        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>The window hidden to the tray (its <c>Hide()</c>): the playback stops instead of drawing steps nobody sees.</summary>
    private void OnWindowChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty && sender is Visual window)
        {
            StopWhenHidden(window);
        }
    }

    private void StopWhenHidden(Visual visual)
    {
        if (!visual.IsVisible)
        {
            _model?.StopPlayback();
        }
    }

    /// <summary>
    /// The player's keys on the Replay tab: Space plays or pauses, the arrows go by event and with Ctrl by turn, Enter
    /// and Backspace by step, + and − change the speed, G opens the "Turn" list, M goes to the mulligan, B bookmarks the
    /// step, [ and ] go to the bookmarks around it. Esc and Alt+Left go on to the window (back to History). While the
    /// "Turn" list is open they are the list's. Letters by their physical key, so another layout works the same.
    /// </summary>
    private void OnPlayerKey(object? sender, KeyEventArgs e)
    {
        if (e.Handled || _model is not { ShowReplay: true } model || model.TurnsOpen || TurnBox.IsDropDownOpen)
        {
            return;
        }

        if (e.KeyModifiers == KeyModifiers.None && (e.PhysicalKey == PhysicalKey.G || (e.PhysicalKey == PhysicalKey.None && e.Key == Key.G)))
        {
            OpenTurns(model);
            e.Handled = true;
            return;
        }

        // Shift+B: the "Bookmarks" menu (the arrows move, Enter goes, Esc closes).
        if (e.KeyModifiers == KeyModifiers.Shift && model.CanBookmark && (e.PhysicalKey == PhysicalKey.B || (e.PhysicalKey == PhysicalKey.None && e.Key == Key.B)))
        {
            BookmarksButton.Flyout?.ShowAt(BookmarksButton);
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers == KeyModifiers.None && LetterCommand(model, e) is { } letter)
        {
            letter.Execute(null);
            e.Handled = true;
            return;
        }

        var command = (e.Key, e.KeyModifiers) switch
        {
            (Key.Space, KeyModifiers.None) => model.TogglePlay,
            (Key.Left, KeyModifiers.None) => model.PreviousEvent,
            (Key.Right, KeyModifiers.None) => model.NextEvent,
            (Key.Left, KeyModifiers.Control) => model.PreviousTurn,
            (Key.Right, KeyModifiers.Control) => model.NextTurn,
            (Key.Enter, KeyModifiers.None) => model.Next,
            (Key.Back, KeyModifiers.None) => model.Previous,
            (Key.OemPlus or Key.Add, KeyModifiers.None or KeyModifiers.Shift) => model.Faster,
            (Key.OemMinus or Key.Subtract, KeyModifiers.None or KeyModifiers.Shift) => model.Slower,
            _ => null,
        };
        if (command is not null)
        {
            command.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>M, B, [ and ] by the key's place (the Russian layout gives other letters there), else by the key.</summary>
    private static RelayCommand? LetterCommand(MatchViewModel model, KeyEventArgs e) => e.PhysicalKey switch
    {
        PhysicalKey.M => model.GoMulligan,
        PhysicalKey.B => model.ToggleBookmark,
        PhysicalKey.BracketLeft => model.PreviousBookmark,
        PhysicalKey.BracketRight => model.NextBookmark,
        PhysicalKey.None => e.Key switch
        {
            Key.M => model.GoMulligan,
            Key.B => model.ToggleBookmark,
            Key.OemOpenBrackets => model.PreviousBookmark,
            Key.OemCloseBrackets => model.NextBookmark,
            _ => null,
        },
        _ => null,
    };

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
        Dispatcher.UIThread.Post(() =>
        {
            if (IsVisible)
            {
                Focus();
            }
        }, DispatcherPriority.Input);

    /// <summary>The "Bookmarks" menu as the bookmarks are now: in step order (the one on screen in bold), or a hint.</summary>
    private void OnBookmarksOpening(object? sender, EventArgs e)
    {
        if (sender is not MenuFlyout menu)
        {
            return;
        }

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
            var glyph = new TextBlock { Text = "󰃀", FontSize = 14, Foreground = ThemeBrushes.Get(row.Current ? "Accent" : "Info") };
            glyph.Classes.Add("icon");
            menu.Items.Add(new MenuItem
            {
                Header = row.Text,
                Icon = glyph,
                Command = model.GoBookmark,
                CommandParameter = row,
                FontWeight = row.Current ? FontWeight.SemiBold : FontWeight.Normal,
            });
        }
    }

    private void Watch(MatchViewModel? model)
    {
        if (_model is not null)
        {
            _model.PropertyChanged -= OnModelChanged;
        }

        _model = model;
        if (model is not null)
        {
            model.PropertyChanged += OnModelChanged;
        }
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MatchViewModel.ShowReplay))
        {
            FocusPlayer();
            Dispatcher.UIThread.Post(() => KeepCurrentFeedRowInView(bringIn: true), DispatcherPriority.Loaded);
        }
        else if (e.PropertyName == nameof(MatchViewModel.ActionCurrentIndex) && _model is { ShowBattlegroundsTable: true })
        {
            // In the step's own frame when the row is there (a step within the stretch); a new stretch's rows are
            // built by the layout first.
            if (!KeepCurrentActionInView())
            {
                Dispatcher.UIThread.Post(() => KeepCurrentActionInView(), DispatcherPriority.Loaded);
            }
        }
        else if (e.PropertyName == nameof(MatchViewModel.FeedCurrentIndex) && _model is { ShowConstructedTable: true })
        {
            // The rows stay from step to step, so the next one is usually built and placed already: the feed scrolls
            // in the step's own frame instead of drawing the page a second time.
            if (!KeepCurrentFeedRowInView(bringIn: false))
            {
                Dispatcher.UIThread.Post(() => KeepCurrentFeedRowInView(bringIn: true), DispatcherPriority.Loaded);
            }
        }
    }

    /// <summary>The feed's least height beside the table (its list scrolls instead of stretching the row).</summary>
    private const double FeedMinHeight = 420;

    /// <summary>
    /// The feed is as tall as the table (at least <see cref="FeedMinHeight"/>), so its list scrolls instead of stretching
    /// the row; at phone width it sits under the table at its own height (the styles cap its list at 360).
    /// </summary>
    private void SizeFeed()
    {
        var compact = this.FindAncestorOfType<MainView>()?.Classes.Contains("compact") == true;
        FeedCard.Height = compact ? double.NaN : Math.Max(FeedMinHeight, TableCard.Bounds.Height);
        // A shorter feed may have cut the step's row off.
        Dispatcher.UIThread.Post(() => KeepCurrentFeedRowInView(bringIn: true), DispatcherPriority.Loaded);
    }

    /// <summary>
    /// The event feed scrolls on its own (not the page) to keep the step's row in view, with its turn's header when it is
    /// the turn's first action (<see cref="MatchFeedLayout"/>). The row is found by its index: one that is not built
    /// (far from the view) is brought in by the list first, when <paramref name="bringIn"/> allows a layout pass here.
    /// False when the row could not be placed yet (the feed is not on screen, or the row is not built).
    /// </summary>
    internal bool KeepCurrentFeedRowInView(bool bringIn = true)
    {
        if (_model is not { FeedCurrentIndex: >= 0 and var index } model || index >= model.FeedRows.Count)
        {
            return true;
        }

        if (!FeedScroll.IsEffectivelyVisible)
        {
            return false;
        }

        var firstIndex = index > 0 && model.FeedRows[index - 1].Header ? index - 1 : index;
        if (FeedRow(index) is null || FeedRow(firstIndex) is null)
        {
            if (!bringIn)
            {
                return false;
            }

            _feedBringsRow = true;
            try
            {
                Feed.ScrollIntoView(index);
                if (firstIndex != index)
                {
                    Feed.ScrollIntoView(firstIndex);
                }
            }
            finally
            {
                _feedBringsRow = false;
            }

            Feed.UpdateLayout();
        }

        if (FeedRow(index) is not { } row || row.TranslatePoint(default, FeedScroll) is not { } at)
        {
            return false;
        }

        var top = FeedRow(firstIndex) is { } first && first.TranslatePoint(default, FeedScroll) is { } firstAt ? firstAt.Y : at.Y;
        if (MatchFeedLayout.Offset(FeedScroll.Offset.Y, FeedScroll.Viewport.Height, top, at.Y + row.Bounds.Height) is { } offset)
        {
            FeedScroll.Offset = new Vector(0, offset);
        }

        return true;
    }

    /// <summary>The built row of a feed index, or null (not built: away from the view).</summary>
    private Control? FeedRow(int index) => Feed.ContainerFromIndex(index) is { IsVisible: true } row ? row : null;

    /// <summary>The Replay tab on screen: the page takes the keyboard focus (unless a control in it already has it).</summary>
    private void FocusPlayer()
    {
        if (_model is { ShowReplay: true } && IsVisible && !IsKeyboardFocusWithin)
        {
            Dispatcher.UIThread.Post(() => Focus(), DispatcherPriority.Input);
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

        if (BgActions.ContainerFromIndex(index) is not { IsVisible: true } row || row.TranslatePoint(default, BgActionsScroll) is not { } at)
        {
            return false;
        }

        var offset = BgActionsScroll.Offset.Y;
        if (at.Y < 0)
        {
            BgActionsScroll.Offset = new Vector(0, offset + at.Y);
        }
        else if (at.Y + row.Bounds.Height > BgActionsScroll.Viewport.Height)
        {
            BgActionsScroll.Offset = new Vector(0, offset + at.Y + row.Bounds.Height - BgActionsScroll.Viewport.Height);
        }

        return true;
    }
}
