using System.Windows;
using System.Windows.Controls;
using Hearthpulse.Core.Localization;
using Microsoft.Win32;

namespace Hearthpulse.App.Main.Migration;

/// <summary>
/// The card for moving over from HDT / Firestone: compact on the home page, whole in Settings → Data (<see cref="Full"/>).
/// The folder picker of "Choose HDT folder" lives here; it listens to the model only while on screen (the model outlives
/// a closed window).
/// </summary>
public partial class MigrationCard : UserControl
{
    public static readonly DependencyProperty FullProperty =
        DependencyProperty.Register(nameof(Full), typeof(bool), typeof(MigrationCard), new PropertyMetadata(false));

    public static readonly DependencyProperty EmbeddedProperty =
        DependencyProperty.Register(nameof(Embedded), typeof(bool), typeof(MigrationCard), new PropertyMetadata(false));

    private MigrationViewModel? _model;

    public MigrationCard()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as MigrationViewModel, IsLoaded);
        Loaded += (_, _) => Attach(DataContext as MigrationViewModel, listen: true);
        Unloaded += (_, _) => Attach(DataContext as MigrationViewModel, listen: false);
    }

    /// <summary>Settings → Data: every source, the folder choice and the last result; else the home page's offer.</summary>
    public bool Full
    {
        get => (bool)GetValue(FullProperty);
        set => SetValue(FullProperty, value);
    }

    /// <summary>Inside the setup wizard: the whole card without its own heading and note (the wizard's step has them).</summary>
    public bool Embedded
    {
        get => (bool)GetValue(EmbeddedProperty);
        set => SetValue(EmbeddedProperty, value);
    }

    private void Attach(MigrationViewModel? model, bool listen)
    {
        if (_model is not null)
        {
            _model.PickFolderRequested -= OnPickFolder;
        }

        _model = model;
        if (model is not null && listen)
        {
            model.PickFolderRequested += OnPickFolder;
        }
    }

    private void OnPickFolder(string start)
    {
        // Both cards (home, settings) hear the request: the one on screen answers.
        if (!IsVisible)
        {
            return;
        }

        var dialog = new OpenFolderDialog { Title = Loc.T("Migration.Action.PickHdt"), InitialDirectory = System.IO.Directory.Exists(start) ? start : string.Empty };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            _model?.SetHdtFolder(dialog.FolderName);
        }
    }
}
