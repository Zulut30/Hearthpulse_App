using System.IO;
using System.Windows.Controls;

namespace Hearthpulse.App;

/// <summary>Render-check scenes of the deck page: a merged deck if there is one (else the most played), its chart hover, a matchup filter.</summary>
public partial class App
{
    private async Task RenderDeckPageAsync(string folder)
    {
        _main.ShowPage("Constructed");
        _main.ShowConstructedTab("Decks");
        await Task.Delay(1500);
        var deck = _mainViewModel.DeckFamiliesShown
            .OrderByDescending(f => f.IsMerged)
            .ThenByDescending(f => f.Games.Count)
            .FirstOrDefault();
        if (deck is null || !_mainViewModel.OpenDeckPage(deck.Key))
        {
            return;
        }

        await Task.Delay(2500);
        _main.UpdateLayout();
        Save(_main, Path.Combine(folder, "main-deck.png"));
        if (_main.FindName("PageDeck") is ScrollViewer { Content: System.Windows.FrameworkElement content })
        {
            SaveFull(content, Path.Combine(folder, "main-deck-full.png"));
        }

        var chart = _main.DeckDetailsPage.Chart;
        if (_mainViewModel.DeckPage.Chart.Count > 0)
        {
            chart.HoverIndex = _mainViewModel.DeckPage.Chart.Count / 2;
            await Task.Delay(1500);
            _main.UpdateLayout();
            Save(_main, Path.Combine(folder, "main-deck-hover.png"));
            chart.HoverIndex = -1;
        }

        // The most played matchup as the filter.
        if (_mainViewModel.DeckPage.Matchups.FirstOrDefault() is { } matchup)
        {
            _mainViewModel.DeckPage.SelectMatchup.Execute(matchup.Key);
            await Task.Delay(2000);
            _main.UpdateLayout();
            Save(_main, Path.Combine(folder, "main-deck-filtered.png"));
            _mainViewModel.DeckPage.ClearMatchup.Execute(null);
        }

        _mainViewModel.DeckPage.Back.Execute(null);
    }
}
