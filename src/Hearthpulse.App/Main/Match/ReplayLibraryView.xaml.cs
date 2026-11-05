using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Hearthpulse.Core;
using Hearthpulse.Core.Localization;
using Microsoft.Win32;

namespace Hearthpulse.App.Main.Match;

/// <summary>
/// The History page as the replay library. The platform parts live here: the file picker of Import, the copy to share
/// on the clipboard as a file, Ctrl+V (the window calls <see cref="PasteFromClipboard"/>), scrolling to a selected card.
/// </summary>
public partial class ReplayLibraryView : UserControl
{
    private ReplayLibraryViewModel? _model;

    public ReplayLibraryView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as ReplayLibraryViewModel, IsLoaded);
        Loaded += (_, _) => Attach(DataContext as ReplayLibraryViewModel, listen: true);
        Unloaded += (_, _) => Attach(DataContext as ReplayLibraryViewModel, listen: false);
        // Before the list: its items take no focus, so it would not move between them.
        List.PreviewKeyDown += OnListKey;
    }

    /// <summary>
    /// The keyboard in the list (review m3). The list is one Tab stop and its items take no focus: Up and Down move from
    /// game to game (the next one scrolled into view), Right goes on to the game's own buttons, Left goes back, Esc returns
    /// to the game; Enter opens it. S, Shift+S, Delete and O act on the game (<see cref="ReplayLibraryViewModel.HandleCardKey"/>).
    /// </summary>
    private void OnListKey(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is not Button { DataContext: LibraryCard card } current || _model is not { } model
            || (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0)
        {
            return;
        }

        var plain = Keyboard.Modifiers == ModifierKeys.None;
        var buttons = List.ItemContainerGenerator.ContainerFromItem(card) is DependencyObject item
            ? Buttons(item).Where(b => b.IsVisible && b.IsEnabled).ToList()
            : [];
        var at = buttons.IndexOf(current);
        switch (e.Key)
        {
            case Key.Up or Key.Down when plain:
                MoveToGame(card, e.Key == Key.Down ? 1 : -1);
                break;
            case Key.Right when plain && at >= 0:
                buttons[Math.Min(at + 1, buttons.Count - 1)].Focus();
                break;
            case Key.Left when plain && at > 0:
                buttons[at - 1].Focus();
                break;
            case Key.Escape when plain && at > 0:
                buttons[0].Focus();
                break;
            default:
                if (!model.HandleCardKey(card, e.Key.ToString(), shift: !plain))
                {
                    return;
                }

                break;
        }

        e.Handled = true;
    }

    private void MoveToGame(LibraryCard card, int step)
    {
        var next = List.Items.IndexOf(card) + step;
        if (next >= 0 && next < List.Items.Count)
        {
            List.ScrollIntoView(List.Items[next]);
            List.UpdateLayout();
            if (List.ItemContainerGenerator.ContainerFromIndex(next) is DependencyObject item && Buttons(item).FirstOrDefault() is { } game)
            {
                game.Focus();
            }
        }
    }

    /// <summary>A game's buttons in their order: the game itself (the card), then its own buttons and the question's.</summary>
    private static IEnumerable<Button> Buttons(DependencyObject parent)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is Button button)
            {
                yield return button;
            }

            foreach (var inner in Buttons(child))
            {
                yield return inner;
            }
        }
    }

    /// <summary>Ctrl+F: the search box takes the focus with its text selected.</summary>
    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    /// <summary>Esc in the search box clears its text (the result filter stays); an empty box lets Esc go on.</summary>
    private void OnSearchKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None && _model is { Search.Length: > 0 } model)
        {
            model.Search = string.Empty;
            e.Handled = true;
        }
    }

    /// <summary>The box's ✕: the text goes, the focus stays in the box.</summary>
    private void OnClearSearchText(object sender, RoutedEventArgs e)
    {
        if (_model is { } model)
        {
            model.Search = string.Empty;
        }

        SearchBox.Focus();
    }

    /// <summary>"Clear search" of the empty state: the text and the result filter go, the focus goes to the box.</summary>
    private void OnClearSearch(object sender, RoutedEventArgs e)
    {
        _model?.ClearSearch.Execute(null);
        SearchBox.Focus();
    }

    /// <summary>Imports the file on the clipboard, or the file whose path is there as text.</summary>
    public void PasteFromClipboard()
    {
        if (_model is not { } model)
        {
            return;
        }

        string? path = null;
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                path = Clipboard.GetFileDropList().Cast<string>().FirstOrDefault();
            }
            else if (Clipboard.ContainsText())
            {
                path = Clipboard.GetText().Trim().Trim('"');
            }
        }
        catch (ExternalException)
        {
            // Another program holds the clipboard: the same as nothing there.
        }

        model.PasteAsync(path).Forget("Replay paste");
    }

    /// <summary>
    /// Listens to the model only while the view is on screen: the model outlives a closed window, and a handler of
    /// its events would keep the window alive.
    /// </summary>
    private void Attach(ReplayLibraryViewModel? model, bool listen)
    {
        if (_model is not null)
        {
            _model.PickFileRequested -= OnPickFile;
            _model.ShareFileRequested -= OnShareFile;
            _model.CopyLinkRequested -= OnCopyLink;
        }

        _model = model;
        if (model is not null && listen)
        {
            model.PickFileRequested += OnPickFile;
            model.ShareFileRequested += OnShareFile;
            model.CopyLinkRequested += OnCopyLink;
        }
    }

    private void OnPickFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = Loc.T("Library.ImportTitle"),
            Filter = Loc.T("Library.ImportFilter") + "|*.hpr;*.log",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            _model?.ImportPath(dialog.FileName);
        }
    }

    /// <summary>An uploaded replay's link (plan 2.2); the status line shows it too, so a busy clipboard loses nothing.</summary>
    private static void OnCopyLink(string url)
    {
        try
        {
            Clipboard.SetText(url);
        }
        catch (ExternalException)
        {
            // Another program holds the clipboard: the link is in the status line.
        }
    }

    private void OnShareFile(string path)
    {
        try
        {
            Clipboard.SetFileDropList([path]);
            _model?.ShareDone(copied: true);
        }
        catch (ExternalException)
        {
            _model?.ShareDone(copied: false);
        }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (List.SelectedItem is { } card)
        {
            List.ScrollIntoView(card);
        }
    }
}
