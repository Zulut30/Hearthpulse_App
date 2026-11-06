using System.Windows.Controls;

namespace Hearthpulse.App.Overlay;

/// <summary>The OBS window "Hearthpulse — Session": progress since the stream started (DataContext: <see cref="OverlayViewModel"/>).</summary>
public partial class StreamSessionPanel : UserControl
{
    public StreamSessionPanel() => InitializeComponent();
}
