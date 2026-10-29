using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Hearthpulse.UI.Main.Match;

public partial class MatchView : UserControl
{
    private MatchViewModel? _model;

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
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty)
            {
                FocusPlayer();
            }
        };
    }

    /// <summary>
    /// The player's keys on the Replay tab: Space plays or pauses, the arrows go by event and with Ctrl by turn, Enter
    /// and Backspace by step, + and − change the speed. Esc and Alt+Left go on to the window (back to History).
    /// </summary>
    private void OnPlayerKey(object? sender, KeyEventArgs e)
    {
        if (e.Handled || _model is not { ShowReplay: true } model)
        {
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
            Dispatcher.UIThread.Post(KeepCurrentActionInView, DispatcherPriority.Loaded);
        }
    }

    /// <summary>The Replay tab on screen: the page takes the keyboard focus (unless a control in it already has it).</summary>
    private void FocusPlayer()
    {
        if (_model is { ShowReplay: true } && IsVisible && !IsKeyboardFocusWithin)
        {
            Dispatcher.UIThread.Post(() => Focus(), DispatcherPriority.Input);
        }
    }

    /// <summary>The Battlegrounds list of events scrolls on its own to keep the current row in view (not the page).</summary>
    private void KeepCurrentActionInView()
    {
        var index = _model?.Actions.ToList().FindIndex(a => a.Current) ?? -1;
        if (index < 0 || BgActions.ContainerFromIndex(index) is not { IsVisible: true } row || row.TranslatePoint(default, BgActionsScroll) is not { } at)
        {
            return;
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
    }
}
