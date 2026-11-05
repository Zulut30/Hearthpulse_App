using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Hearthpulse.Core;
using Hearthpulse.Core.Localization;

namespace Hearthpulse.UI.Main.Migration;

/// <summary>
/// The card for moving over from HDT / Firestone: compact on the home page, whole in Settings → Data (<see cref="Full"/>).
/// The folder picker of "Choose HDT folder" lives here; it listens to the model only while on screen (the model outlives
/// a closed window).
/// </summary>
public partial class MigrationCard : UserControl
{
    public static readonly StyledProperty<bool> FullProperty = AvaloniaProperty.Register<MigrationCard, bool>(nameof(Full));

    public static readonly StyledProperty<bool> EmbeddedProperty = AvaloniaProperty.Register<MigrationCard, bool>(nameof(Embedded));

    private MigrationViewModel? _model;

    public MigrationCard()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as MigrationViewModel, this.IsAttachedToVisualTree());
        AttachedToVisualTree += (_, _) =>
        {
            Attach(DataContext as MigrationViewModel, listen: true);
            // A platform without a folder picker cannot choose HDT's folder.
            PickHdt.IsVisible = TopLevel.GetTopLevel(this)?.StorageProvider.CanPickFolder ?? false;
        };
        DetachedFromVisualTree += (_, _) => Attach(DataContext as MigrationViewModel, listen: false);
    }

    /// <summary>Settings → Data: every source, the folder choice and the last result; else the home page's offer.</summary>
    public bool Full
    {
        get => GetValue(FullProperty);
        set => SetValue(FullProperty, value);
    }

    /// <summary>Inside the setup wizard: the whole card without its own heading and note (the wizard's step has them).</summary>
    public bool Embedded
    {
        get => GetValue(EmbeddedProperty);
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

    // Both cards (home, settings) hear the request: the one on screen answers.
    private void OnPickFolder(string start)
    {
        if (IsEffectivelyVisible)
        {
            PickFolderAsync(start).Forget("HDT folder picker");
        }
    }

    private async Task PickFolderAsync(string start)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanPickFolder: true } storage)
        {
            return;
        }

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Loc.T("Migration.Action.PickHdt"),
            AllowMultiple = false,
            SuggestedStartLocation = Directory.Exists(start) ? await storage.TryGetFolderFromPathAsync(start) : null,
        });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
        {
            _model?.SetHdtFolder(path);
        }
    }
}
