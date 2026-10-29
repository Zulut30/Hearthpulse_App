using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Hearthpulse.App.Main.Match;

public partial class MatchView : UserControl
{
    private MatchViewModel? _model;

    public MatchView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) => Watch(e.NewValue as MatchViewModel);
        IsVisibleChanged += (_, _) => FocusPlayer();
    }

    /// <summary>
    /// The player's keys on the Replay tab, before the focused button or radio sees them (Space must not click it):
    /// Space plays or pauses, the arrows go by event and with Ctrl by turn, Enter and Backspace by step, + and − change
    /// the speed. Esc and Alt+Left go on to the window (back to History).
    /// </summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled || _model is not { ShowReplay: true } model)
        {
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
            _ => null,
        };
        if (command is not null)
        {
            command.Execute(null);
            e.Handled = true;
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
        }
        else if (e.PropertyName == nameof(MatchViewModel.Actions) && _model is { ShowBattlegroundsTable: true })
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, KeepCurrentActionInView);
        }
    }

    /// <summary>The Replay tab on screen: the page takes the keyboard focus (unless a control in it already has it).</summary>
    private void FocusPlayer()
    {
        if (_model is { ShowReplay: true } && IsVisible && !IsKeyboardFocusWithin)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => Focus());
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
