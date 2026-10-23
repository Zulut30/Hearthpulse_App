using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Hearthpulse.UI;
using Hearthpulse.UI.Infrastructure;
using Hearthpulse.UI.Main;

namespace Hearthpulse.Desktop;

/// <summary>
/// Render-check scenes of the deck page: a deck with a merge suggestion, else a merged one, else the most played; its
/// chart hover, a matchup filter, and the Versions card's states (card differences, the "add a deck" list, the other
/// deck of the suggestion, a merge with its ⋯ menu and undo). Merges go to the data copy the render runs on and are undone.
/// </summary>
internal static partial class OffscreenRenderer
{
    private static async Task RenderDeckPageAsync(Window main, MainView view, AppHost host, string folder)
    {
        view.ShowPage("Constructed");
        var deck = host.Main.DeckFamiliesShown
            .OrderByDescending(f => host.Main.DeckSuggestions.Any(s => s.FirstKey == f.Key || s.SecondKey == f.Key))
            .ThenByDescending(f => f.IsMerged)
            .ThenByDescending(f => f.Games.Count)
            .FirstOrDefault();
        if (deck is null || !host.Main.OpenDeckPage(deck.Key))
        {
            return;
        }

        var page = host.Main.DeckPage;
        main.Height = 4300;
        await Save(main, Path.Combine(folder, "main-deck-full.png"));
        SaveCrop(main, view.DeckVersionsCard, Path.Combine(folder, "main-deck-versions.png"));
        await RenderVersionsAsync(main, view, host, folder);
        main.Height = 840;
        await Save(main, Path.Combine(folder, "main-deck.png"));
        if (page.Chart.Count > 0)
        {
            view.HoverDeckGame(page.Chart.Count / 2);
            await Save(main, Path.Combine(folder, "main-deck-hover.png"));
            view.HoverDeckGame(-1);
        }

        if (page.Matchups.FirstOrDefault() is { } matchup)
        {
            page.SelectMatchup.Execute(matchup.Key);
            await Save(main, Path.Combine(folder, "main-deck-filtered.png"));
            page.ClearMatchup.Execute(null);
        }

        // Phone width: cards stack, the meta columns and average turn hide.
        main.Width = 420;
        main.Height = 6200;
        await Save(main, Path.Combine(folder, "main-deck-compact.png"));
        main.Width = 1260;
        main.Height = 840;
        await RenderComboBoxAsync(folder);
        page.Back.Execute(null);
    }

    /// <summary>Versions card states, cropped from the tall window (the deck page shows the deck it was opened on again after).</summary>
    private static async Task RenderVersionsAsync(Window main, MainView view, AppHost host, string folder)
    {
        var page = host.Main.DeckPage;
        var first = page.Key;
        async Task Crop(string name)
        {
            await Settle();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            SaveCrop(main, view.DeckVersionsCard, Path.Combine(folder, name));
        }

        if (page.Suggestions.FirstOrDefault() is { Changes.Length: > 0 } suggestion)
        {
            page.ToggleDiff.Execute(suggestion.SecondKey);
            await Crop("main-deck-versions-diff.png");
            page.ToggleDiff.Execute(suggestion.SecondKey);
        }

        page.TogglePicker.Execute(null);
        if (page.Candidates.FirstOrDefault() is { } candidate)
        {
            page.Choose.Execute(candidate.Key);
        }

        await Crop("main-deck-versions-picker.png");
        page.Search = "zzzz";
        await Crop("main-deck-versions-picker-empty.png");
        page.TogglePicker.Execute(null);

        if (page.Suggestions.FirstOrDefault() is { } pair && host.Main.OpenDeckPage(pair.SecondKey))
        {
            await Crop("main-deck-versions-partner.png");
            if (page.Suggestions.FirstOrDefault() is { } back)
            {
                page.Merge.Execute(back);
                await Crop("main-deck-versions-merged.png");
                if (page.Versions.FirstOrDefault(v => v.HasDiff) is { } changed)
                {
                    page.ToggleDiff.Execute(changed.Key);
                    await Crop("main-deck-versions-merged-diff.png");
                    page.ToggleDiff.Execute(changed.Key);
                }

                await Settle();
                if (view.OpenDeckVersionMenu() is { } menu)
                {
                    await Settle();
                    SavePopup(main, menu.Items.OfType<MenuItem>().FirstOrDefault()?.FindAncestorOfType<MenuFlyoutPresenter>(),
                        Path.Combine(folder, "main-deck-version-menu.png"));
                    menu.Hide();
                }
                else
                {
                    Console.WriteLine("no version menu to open");
                }

                page.UndoMerge.Execute(null);
                await Crop("main-deck-versions-undone.png");
            }
        }

        if (first is not null)
        {
            host.Main.OpenDeckPage(first);
            await Settle();
        }
    }

    /// <summary>The theme's combo box, closed with its placeholder, then open with a picked item (no page has one yet).</summary>
    private static async Task RenderComboBoxAsync(string folder)
    {
        var combo = new ComboBox { Width = 300, PlaceholderText = "Pick a deck", ItemsSource = new[] { "Рыцарь смерти2", "Рыцарь смерти3", "Quest Priest" } };
        var window = new Window
        {
            Content = new StackPanel { Margin = new Avalonia.Thickness(16), Children = { combo } },
            Width = 332,
            Height = 68,
            Background = ThemeBrushes.Get("Surface2"),
        };
        window.Show();
        await Save(window, Path.Combine(folder, "theme-combobox.png"));
        combo.SelectedIndex = 1;
        combo.IsDropDownOpen = true;
        await Settle();
        SavePopup(window, combo.GetVisualDescendants().OfType<Popup>().FirstOrDefault()?.Child, Path.Combine(folder, "theme-combobox-open.png"));

        combo.IsDropDownOpen = false;
        window.Close();
    }

    /// <summary>A popup's content: its own top level when popups are windows, else cropped from the owner's overlay layer.</summary>
    private static void SavePopup(Window owner, Control? content, string path)
    {
        if (content is null)
        {
            Console.WriteLine("no popup for " + path);
            return;
        }

        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        if (TopLevel.GetTopLevel(content) is { } top && top != owner)
        {
            top.CaptureRenderedFrame()?.Save(path, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            Console.WriteLine("wrote " + path);
        }
        else
        {
            SaveCrop(owner, content, path);
        }
    }

    /// <summary>Lets a family change rebuild the page (at least 1.5 s) and card art load (up to 12 s).</summary>
    private static async Task Settle()
    {
        var minimum = DateTime.UtcNow.AddSeconds(1.5);
        var deadline = DateTime.UtcNow.AddSeconds(12);
        do
        {
            await Task.Delay(250);
            Dispatcher.UIThread.RunJobs();
        }
        while (DateTime.UtcNow < minimum || (CardArt.InFlight > 0 && DateTime.UtcNow < deadline));
    }
}
