using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Hearthpulse.App;

/// <summary>
/// Render-check scenes of the deck page: a deck with a merge suggestion, else a merged one, else the most played; its
/// chart hover, a matchup filter, and the Versions card's states (card differences, the "add a deck" list, the other
/// deck of the suggestion, a merge with its ⋯ menu and undo). Merges go to the data copy the render runs on and are undone.
/// </summary>
public partial class App
{
    private async Task RenderDeckPageAsync(string folder)
    {
        _main.ShowPage("Constructed");
        _main.ShowConstructedTab("Decks");
        await Task.Delay(1500);
        var deck = _mainViewModel.DeckFamiliesShown
            .OrderByDescending(f => _mainViewModel.DeckSuggestions.Any(s => s.FirstKey == f.Key || s.SecondKey == f.Key))
            .ThenByDescending(f => f.IsMerged)
            .ThenByDescending(f => f.Games.Count)
            .FirstOrDefault();
        if (deck is null || !_mainViewModel.OpenDeckPage(deck.Key))
        {
            return;
        }

        await Task.Delay(2500);
        _main.UpdateLayout();
        Save(_main, Path.Combine(folder, "main-deck.png"));
        if (_main.FindName("PageDeck") is ScrollViewer { Content: FrameworkElement content })
        {
            SaveFull(content, Path.Combine(folder, "main-deck-full.png"));
        }

        await SaveVersionsAsync(folder, "main-deck-versions.png");

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

        try
        {
            await RenderVersionsAsync(folder);
        }
        catch (IOException)
        {
        }

        RenderComboBox(folder);
        _mainViewModel.DeckPage.Back.Execute(null);
    }

    private async Task RenderVersionsAsync(string folder)
    {
        var page = _mainViewModel.DeckPage;

        // A suggestion's card differences.
        if (page.Suggestions.FirstOrDefault() is { Changes.Length: > 0 } suggestion)
        {
            page.ToggleDiff.Execute(suggestion.SecondKey);
            await SaveVersionsAsync(folder, "main-deck-versions-diff.png");
            page.ToggleDiff.Execute(suggestion.SecondKey);
        }

        // The "add a deck" list with the most alike deck picked, then a search that finds nothing.
        page.TogglePicker.Execute(null);
        if (page.Candidates.FirstOrDefault() is { } candidate)
        {
            page.Choose.Execute(candidate.Key);
        }

        await SaveVersionsAsync(folder, "main-deck-versions-picker.png");
        page.Search = "zzzz";
        await SaveVersionsAsync(folder, "main-deck-versions-picker-empty.png");
        page.TogglePicker.Execute(null);

        // The other deck of the suggestion (e.g. a list with two games), then merged into it, its ⋯ menu, and undone.
        if (page.Suggestions.FirstOrDefault() is not { } pair || !_mainViewModel.OpenDeckPage(pair.SecondKey))
        {
            return;
        }

        await SaveVersionsAsync(folder, "main-deck-versions-partner.png");
        if (page.Suggestions.FirstOrDefault() is not { } back)
        {
            return;
        }

        page.Merge.Execute(back);
        await SaveVersionsAsync(folder, "main-deck-versions-merged.png");
        if (page.Versions.FirstOrDefault(v => v.HasDiff) is { } changed)
        {
            page.ToggleDiff.Execute(changed.Key);
            await SaveVersionsAsync(folder, "main-deck-versions-merged-diff.png");
            page.ToggleDiff.Execute(changed.Key);
        }

        await Task.Delay(1000);
        _main.UpdateLayout();
        if (_main.DeckDetailsPage.OpenLastVersionMenu() is { } menu)
        {
            await Task.Delay(800);
            menu.UpdateLayout();
            SaveCard(menu, Path.Combine(folder, "main-deck-version-menu.png"));
            menu.IsOpen = false;
        }

        page.UndoMerge.Execute(null);
        await SaveVersionsAsync(folder, "main-deck-versions-undone.png");
    }

    private async Task SaveVersionsAsync(string folder, string name)
    {
        await Task.Delay(2000);
        _main.UpdateLayout();
        SaveCard(_main.DeckDetailsPage.Versions, Path.Combine(folder, name));
    }

    /// <summary>
    /// An element cut out of its window (or popup) at twice its size, scrolled into view first. Unlike
    /// <see cref="SaveScaled"/> it is cut from the rendered window, so a card that just got shorter is not squeezed into
    /// its old height.
    /// </summary>
    private static void SaveCard(FrameworkElement element, string path)
    {
        const double scale = 2;
        const double pad = 12;
        element.BringIntoView();
        if (PresentationSource.FromVisual(element)?.RootVisual is not FrameworkElement root)
        {
            return;
        }

        root.UpdateLayout();
        var full = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)Math.Ceiling(root.ActualWidth * scale), (int)Math.Ceiling(root.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        full.Render(root);
        var box = element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
        box.Inflate(pad, pad);
        box.Intersect(new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        if (box.IsEmpty)
        {
            return;
        }

        var crop = new System.Windows.Media.Imaging.CroppedBitmap(full, new Int32Rect(
            (int)(box.X * scale), (int)(box.Y * scale), (int)Math.Min(full.PixelWidth - (box.X * scale), box.Width * scale), (int)Math.Min(full.PixelHeight - (box.Y * scale), box.Height * scale)));
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(crop));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>The theme's combo box, closed with its placeholder, then open with a picked item (no page has one yet).</summary>
    private void RenderComboBox(string folder)
    {
        var combo = new ComboBox { Width = 300, Tag = "Pick a deck", ItemsSource = new[] { "Рыцарь смерти2", "Рыцарь смерти3", "Quest Priest" } };
        var window = new Window
        {
            Content = new StackPanel { Margin = new Thickness(16), Children = { combo } },
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            Left = -20000,
            Top = -20000,
            Background = (Brush)FindResource("Surface2"),
        };
        window.Show();
        window.UpdateLayout();
        SaveCard((FrameworkElement)window.Content, Path.Combine(folder, "theme-combobox.png"));
        combo.SelectedIndex = 1;
        combo.IsDropDownOpen = true;
        window.UpdateLayout();
        if (combo.Template.FindName("PART_Popup", combo) is Popup { Child: FrameworkElement list })
        {
            list.UpdateLayout();
            SaveCard(list, Path.Combine(folder, "theme-combobox-open.png"));
        }

        combo.IsDropDownOpen = false;
        window.Close();
    }
}
