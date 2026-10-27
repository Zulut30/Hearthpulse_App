using Avalonia.Controls;
using Avalonia.Input.Platform;
using Hearthpulse.Core;

namespace Hearthpulse.UI.Main.Meta;

/// <summary>The Standard/Wild community tabs; the page's tab bar picks which one shows (<see cref="ShowTab"/>).</summary>
public partial class ConstructedMetaView : UserControl
{
    private MainViewModel? _viewModel;

    public ConstructedMetaView() => InitializeComponent();

    /// <summary>"Tiers", "Top" or "Matchups"; anything else hides the view (the "My stats" tab).</summary>
    public void ShowTab(string? tab)
    {
        MetaTiersPanel.IsVisible = tab == "Tiers";
        MetaDecksPanel.IsVisible = tab == "Top";
        MetaMatchupsPanel.IsVisible = tab == "Matchups";
        IsVisible = tab is "Tiers" or "Top" or "Matchups";
    }

    /// <summary>The tier list and the chosen archetype's card: side by side, or one under the other when narrow.</summary>
    public void Stack(bool compact)
    {
        MetaTiersPanel.ColumnDefinitions = compact ? new ColumnDefinitions("*") : new ColumnDefinitions("3*,16,2*");
        MetaTiersPanel.RowDefinitions = compact ? new RowDefinitions("Auto,16,Auto") : new RowDefinitions("Auto");
        Grid.SetColumn(ArchetypeCard, compact ? 0 : 2);
        Grid.SetRow(ArchetypeCard, compact ? 2 : 0);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
        {
            _viewModel.DeckCodeCopyRequested -= OnCopyDeck;
        }

        _viewModel = DataContext as MainViewModel;
        if (_viewModel is not null)
        {
            _viewModel.DeckCodeCopyRequested += OnCopyDeck;
        }
    }

    // Avalonia's clipboard belongs to the top level, not the view model.
    private async void OnCopyDeck(MetaDeckRow deck)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        try
        {
            if (clipboard is not null)
            {
                await clipboard.SetTextAsync(deck.Code);
            }

            _viewModel?.DeckCodeCopied(deck.Name, clipboard is not null);
        }
        catch (Exception ex)
        {
            // An async event handler: anything that escaped would reach the crash handler for a failed clipboard copy.
            if (ex is not (InvalidOperationException or System.Runtime.InteropServices.ExternalException))
            {
                Diag.Error("Copying a deck code failed", ex);
            }

            _viewModel?.DeckCodeCopied(deck.Name, false);
        }
    }
}
