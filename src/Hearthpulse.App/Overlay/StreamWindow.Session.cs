using System.Windows;

namespace Hearthpulse.App.Overlay;

/// <summary>"Start a new session" in the menu of the "Session" window (proposal B1).</summary>
public partial class StreamWindow
{
    /// <summary>"Start a new session" from the "Session" window's menu.</summary>
    public event Action? ResetSessionRequested;

    private void OnResetSession(object sender, RoutedEventArgs e) => ResetSessionRequested?.Invoke();
}
