using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Hearthpulse.UI.Overlay;

/// <summary>
/// What an OBS capture window shows (docs/streaming.md): the overlay's panel control for <see cref="Spec"/> on the
/// overlay's model, so it shows exactly what the overlay shows, or the background alone, or the quiet tile
/// (<see cref="OverlayViewModel.StreamState"/>). DataContext: <see cref="OverlayViewModel"/>.
/// </summary>
public partial class StreamView : UserControl
{
    private OverlayViewModel? _model;
    private StreamWindowSpec _spec = StreamWindowSpec.Deck;
    private readonly IBrush? _themeBackground;

    public StreamView()
    {
        InitializeComponent();
        _themeBackground = Root.Background;
        // The deck row flash (OverlayLook.axaml) runs only when the system allows animations, as on the overlay.
        Classes.Set("motion", Infrastructure.Motion.On);
        PanelHost.Content = Panel(_spec);
    }

    /// <summary>Which window this is; its panel is built when it is set.</summary>
    public StreamWindowSpec Spec
    {
        get => _spec;
        set
        {
            if (value == _spec)
            {
                return;
            }

            _spec = value;
            PanelHost.Content = Panel(value);
            ApplyState();
        }
    }

    /// <summary>The window's background: the theme's (<c>"Theme"</c>) or a key colour (<c>"Green"</c>, <c>"Magenta"</c>).</summary>
    public void SetBackground(string background)
    {
        if (StreamBackdrop.KeyColor(background) is { } key)
        {
            Root.Background = new SolidColorBrush(Color.FromRgb(key.R, key.G, key.B));
        }
        else
        {
            // The theme's shared Bg brush: recoloured in place by a theme change.
            Root.Background = _themeBackground;
        }
    }

    /// <summary>What is on screen now: "panel", "tile" or "background" (tests and renders).</summary>
    public string Showing => Scaler.IsVisible ? "panel" : IdleTile.IsVisible ? "tile" : "background";

    private static Control Panel(StreamWindowSpec spec) => spec.Id switch
    {
        "deck" => new DeckPanel(),
        "lobby" => new LobbyPanel(),
        _ => new OddsPanel(),
    };

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model is not null)
        {
            _model.PropertyChanged -= OnModelChanged;
        }

        _model = DataContext as OverlayViewModel;
        if (_model is not null)
        {
            _model.PropertyChanged += OnModelChanged;
        }

        ApplyState();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_model is not null)
        {
            _model.PropertyChanged -= OnModelChanged;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_model is not null)
        {
            _model.PropertyChanged -= OnModelChanged;
            _model.PropertyChanged += OnModelChanged;
        }

        ApplyState();
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == OverlayViewModel.StreamStateProperty(_spec.Id))
        {
            ApplyState();
        }
    }

    /// <summary>The panel, the background alone, or the quiet tile; switched at once, no fade.</summary>
    private void ApplyState()
    {
        var state = _model?.StreamState(_spec.Id) ?? StreamWindowState.Idle;
        Scaler.IsVisible = state == StreamWindowState.Shown;
        IdleTile.IsVisible = state == StreamWindowState.Idle;
    }
}
