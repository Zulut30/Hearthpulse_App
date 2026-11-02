using Avalonia.Controls;

namespace Hearthpulse.UI.Main.Pages;

/// <summary>
/// The history: the filter bar and the replay library (Recent, Saved, Imported) (its data: <see cref="MainViewModel"/>). The view keeps the state that
/// outlives it (the tab shown) and reaches the named parts it switches through this control.
/// </summary>
public partial class HistoryPage : UserControl
{
    public HistoryPage() => InitializeComponent();
}
