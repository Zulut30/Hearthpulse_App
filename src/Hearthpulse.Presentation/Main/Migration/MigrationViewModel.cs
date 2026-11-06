#if HEARTHPULSE_WPF
using Hearthpulse.App.Infrastructure;
#else
using Hearthpulse.UI.Infrastructure;
#endif
using System.IO;
using Hearthpulse.Core;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Migration;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Main.Migration;
#else
namespace Hearthpulse.UI.Main.Migration;
#endif

/// <summary>One tracker of the migration card: its name, what was found, where.</summary>
public sealed record MigrationSourceRow(string Name, string CountsText, string StatusText, string Folder, bool IsFound, bool IsMissing, bool IsServerOnly, bool IsBroken) : IImmutableView;

/// <summary>
/// Moving over from HDT / Firestone (<c>docs/migration.md</c>): one model for the card on the home page (first start) and in
/// Settings → Data, so both show the same search, progress and result. The search and the import run off the UI thread;
/// the texts are built here and again when the language changes.
/// </summary>
public sealed class MigrationViewModel : Observable
{
    private readonly Func<CardDb> _cards;
    private readonly HistoryStore _history;
    private readonly AppSettings _settings;
    private readonly Func<Task> _imported;
    private readonly Func<string?, IReadOnlyList<MigrationFound>> _find;
    private IReadOnlyList<MigrationFound> _found = [];
    private IReadOnlyList<MigrationSourceRow> _sources = [];
    private bool _scanning;
    private bool _scanned;
    private int _scan;
    private CancellationTokenSource? _transfer;
    private bool _busy;
    private bool _homeActed;
    private bool _homeHidden;
    private string _progress = string.Empty;
    private MigrationResult? _error;
    private Task _work = Task.CompletedTask;

    internal MigrationViewModel(Func<CardDb> cards, HistoryStore history, AppSettings settings, Func<Task> imported,
        Func<string?, IReadOnlyList<MigrationFound>>? find = null)
    {
        _cards = cards;
        _history = history;
        _settings = settings;
        _imported = imported;
        _find = find ?? MigrationScan.Find;
        Migrate = new RelayCommand(p =>
        {
            if (!CanMigrate)
            {
                return;
            }

            if (p as string == "home")
            {
                _homeActed = true;
                Offered();
            }

            _work = MigrateAsync();
            _work.Forget("Migration");
        });
        NotNow = new RelayCommand(() =>
        {
            Offered();
            Raise(nameof(ShowOnHome));
        });
        Hide = new RelayCommand(() =>
        {
            _homeHidden = true;
            Raise(nameof(ShowOnHome));
        });
        PickHdt = new RelayCommand(() =>
        {
            if (!_busy)
            {
                PickFolderRequested?.Invoke(_settings.HdtFolder is { Length: > 0 } folder ? folder : HdtImport.DefaultFolder);
            }
        });
        // The search reads every HDT file: on start only while the home card may still offer the transfer; else when
        // Settings → Data opens (EnsureScanned).
        if (!settings.MigrationOffered)
        {
            EnsureScanned();
        }
    }

    /// <summary>The view should let the player choose HDT's data folder (starting at this one) and pass it to <see cref="SetHdtFolder"/>.</summary>
    public event Action<string>? PickFolderRequested;

    public RelayCommand Migrate { get; }

    public RelayCommand NotNow { get; }

    public RelayCommand Hide { get; }

    public RelayCommand PickHdt { get; }

    public IReadOnlyList<MigrationSourceRow> Sources { get => _sources; private set => Set(ref _sources, value); }

    public bool IsScanning { get => _scanning; private set => Set(ref _scanning, value); }

    /// <summary>
    /// The home card: shown once the search found games or decks to bring over and the player has not answered yet; after
    /// "Transfer" on it, it stays (progress, result) until "Hide".
    /// </summary>
    public bool ShowOnHome => !_homeHidden && !_scanning && (_homeActed || (!_settings.MigrationOffered && HasWork));

    public string HomeTitle => Loc.F("Migration.Home.Title", Importable.Select(f => f.Source).Distinct().Count() > 1 ? Loc.T("Migration.Home.Both")
        : Importable.FirstOrDefault()?.Source == MigrationSource.Firestone ? "Firestone" : "HDT");

    public IReadOnlyList<string> HomeFoundLines => Importable
        .Select(f => f.Counted
            ? Loc.F("Migration.Found", f.Source == MigrationSource.Hdt ? "HDT" : "Firestone", Counts(f.Games, f.Decks))
            : Loc.F("Migration.FoundUncounted", f.Source == MigrationSource.Hdt ? "HDT" : "Firestone"))
        .ToList();

    public bool NothingFound => !_scanning && !HasWork;

    /// <summary>"Transfer" shows when there is something to bring over.</summary>
    public bool ShowMigrate => HasWork;

    public bool CanMigrate => HasWork && !_busy && !_scanning && Ready;

    /// <summary>
    /// The cards are loaded and the first import of the game's logs is done: a game brought over earlier would get no
    /// deck code or class, and the logs' own record of it would come in after it (the app's record replaces it, but
    /// the transfer waits rather than do the work twice).
    /// </summary>
    private bool Ready => _cards().Count > 0 && _settings.HistoryImported;

    /// <summary>Something to bring over, but the app is still loading the cards or reading the game's logs.</summary>
    public bool Waiting => HasWork && !_busy && !_scanning && !Ready;

    /// <summary>Home: "Transfer" until a transfer ended well (then only "Hide").</summary>
    public bool HomeShowMigrate => HasWork && !HasResult;

    /// <summary>Home: "Not now" only before the player acted on the card.</summary>
    public bool HomeShowNotNow => !_homeActed && !_busy;

    /// <summary>Home: "Hide" once a transfer from the card ended (done or failed).</summary>
    public bool HomeShowHide => _homeActed && !_busy && (HasResult || HasError);

    public bool IsBusy { get => _busy; private set => Set(ref _busy, value); }

    public bool CanPick => !_busy;

    public string MigrateLabel => _busy ? Loc.T("Migration.Action.Busy")
        : _settings.LastMigration is not null || _error is not null ? Loc.T("Migration.Action.Again")
        : Loc.T("Migration.Action.Move");

    public string ProgressText { get => _progress; private set => Set(ref _progress, value); }

    public bool HasResult => _settings.LastMigration is not null && _error is null;

    public string ResultWhen => _settings.LastMigration is { } last
        ? Loc.F("Migration.Result.When", last.At.ToLocalTime().ToString("d MMMM yyyy, HH:mm", Loc.DateCulture))
        : string.Empty;

    public string ResultHeadline => _settings.LastMigration is not { } last ? string.Empty
        : last.Added == 0 && last.Decks == 0 ? Loc.T("Migration.Result.Nothing")
        : Loc.F("Migration.Result.Moved", Counts(last.Added, last.Decks));

    public IReadOnlyList<string> ResultDetails => _settings.LastMigration is not { } last ? []
        : new[]
        {
            last.Already > 0 ? Loc.F("Migration.Result.Already", Loc.P("Migration.Count.Games", last.Already)) : null,
            last.Skipped > 0 ? Loc.F("Migration.Result.Skipped", Loc.P("Migration.Count.Games", last.Skipped)) : null,
            last.Replays > 0 ? Loc.F("Migration.Result.Replays", Loc.P("Migration.Count.Games", last.Replays)) : null,
        }.OfType<string>().ToList();

    public bool HasError => _error is not null;

    public string ErrorTitle => _error is { Added: > 0 } ? Loc.T("Migration.Error.Partial") : Loc.T("Migration.Error.Title");

    public string ErrorText => _error?.Error is { } code ? Loc.F("Migration.Error." + code, ErrorSource) : string.Empty;

    /// <summary>The source whose import failed, for the error text.</summary>
    private string ErrorSource { get; set; } = string.Empty;

    private IEnumerable<MigrationFound> Importable => _found.Where(f => f.HasWork);

    private bool HasWork => Importable.Any();

    /// <summary>Ends when the current search or transfer has (tests wait on it).</summary>
    internal Task WorkTask => _work;

    /// <summary>The player chose HDT's data folder: kept in the settings and searched again.</summary>
    public void SetHdtFolder(string folder)
    {
        _settings.HdtFolder = folder;
        SaveSettings();
        _work = ScanAsync();
        _work.Forget("Migration search");
    }

    /// <summary>The language changed: every text built here is read again.</summary>
    internal void Rebuild()
    {
        Sources = RowsOf(_found);
        foreach (var name in new[] { nameof(HomeTitle), nameof(HomeFoundLines), nameof(MigrateLabel), nameof(ResultWhen), nameof(ResultHeadline),
                     nameof(ResultDetails), nameof(ErrorTitle), nameof(ErrorText) })
        {
            Raise(name);
        }
    }

    /// <summary>Searches for the other trackers' data unless it was searched already (Settings → Data opened).</summary>
    public void EnsureScanned()
    {
        if (!_scanned && !_scanning && !_busy)
        {
            _work = ScanAsync();
            _work.Forget("Migration search");
        }
    }

    /// <summary>The app is closing: a transfer under way stops after the game it is at (each game is saved whole).</summary>
    internal void Stop() => _transfer?.Cancel();

    /// <summary>The cards or History's import of the logs changed: whether Transfer can be pressed is read again.</summary>
    internal void InputsChanged()
    {
        Raise(nameof(CanMigrate));
        Raise(nameof(Waiting));
    }

    private async Task ScanAsync()
    {
        // A later search (another folder chosen) wins over one still running.
        var scan = ++_scan;
        IsScanning = true;
        RaiseState();
        var folder = _settings.HdtFolder;
        var find = _find;
        IReadOnlyList<MigrationFound> found;
        try
        {
            found = await Task.Run(() => find(folder)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // Data of another program, anything in it: the card says nothing was found instead of searching for ever.
            Diag.Error("Searching for HDT / Firestone data failed", ex);
            found = [];
        }

        if (scan != _scan)
        {
            return;
        }

        _found = found;
        _scanned = true;
        IsScanning = false;
        Sources = RowsOf(_found);
        RaiseState();
    }

    private async Task MigrateAsync()
    {
        IsBusy = true;
        _error = null;
        RaiseState();
        var cards = _cards();
        var history = _history;
        using var transfer = _transfer = new CancellationTokenSource();
        var token = transfer.Token;
        var total = new MigrationResult(0, 0, 0, 0, 0);
        foreach (var source in Importable.ToList())
        {
            var name = source.Source == MigrationSource.Hdt ? "HDT" : "Firestone";
            var progress = new Progress<MigrationProgress>(p =>
                ProgressText = Loc.F("Migration.Progress.Line", name, Loc.F("Migration.Progress.Games", p.Done, p.Total)));
            MigrationResult result;
            try
            {
                result = await Task.Run(() => MigrationScan.Import(source, history, cards, progress, token), token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                // The app is closing: what was saved stays, transferring again picks up without duplicates.
                return;
            }
            catch (Exception ex)
            {
                // Anything the readers did not expect: the player gets "could not read", the log the reason.
                Diag.Error($"Migration from {name} failed", ex);
                result = MigrationResult.Failed("Other");
            }

            total = new MigrationResult(total.Added + result.Added, total.Already + result.Already, total.Skipped + result.Skipped,
                total.Decks + result.Decks, total.Replays + result.Replays);
            if (result.Error is { } error)
            {
                _error = total with { Error = error };
                ErrorSource = name;
                break;
            }
        }

        if (_error is null || total.Added > 0)
        {
            _settings.LastMigration = new MigrationRecord
            {
                At = DateTime.UtcNow,
                Added = total.Added,
                Already = total.Already,
                Skipped = total.Skipped,
                Decks = total.Decks,
                Replays = total.Replays,
            };
            SaveSettings();
        }

        ProgressText = string.Empty;
        _transfer = null;
        IsBusy = false;
        RaiseState();
        await _imported().ConfigureAwait(true);
        // The numbers in Settings → Data after the transfer (a Firestone database is counted now).
        _scanned = false;
        await ScanAsync().ConfigureAwait(true);
    }

    private void Offered()
    {
        _settings.MigrationOffered = true;
        SaveSettings();
    }

    private void SaveSettings()
    {
        try
        {
            _settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diag.Error("Saving the settings after the migration card failed", ex);
        }
    }

    private void RaiseState()
    {
        foreach (var name in new[] { nameof(ShowOnHome), nameof(HomeTitle), nameof(HomeFoundLines), nameof(NothingFound), nameof(ShowMigrate), nameof(Waiting),
                     nameof(HomeShowMigrate), nameof(HomeShowNotNow), nameof(HomeShowHide),
                     nameof(CanMigrate), nameof(CanPick), nameof(MigrateLabel), nameof(HasResult), nameof(ResultWhen), nameof(ResultHeadline),
                     nameof(ResultDetails), nameof(HasError), nameof(ErrorTitle), nameof(ErrorText) })
        {
            Raise(name);
        }
    }

    private static string Counts(int games, int decks) => decks > 0
        ? Loc.F("Migration.Count.Pair", Loc.P("Migration.Count.Games", games), Loc.P("Migration.Count.Decks", decks))
        : Loc.P("Migration.Count.Games", games);

    private List<MigrationSourceRow> RowsOf(IReadOnlyList<MigrationFound> found)
    {
        var rows = new List<MigrationSourceRow>();
        var hdtFolder = _settings.HdtFolder is { Length: > 0 } chosen ? chosen : HdtImport.DefaultFolder;
        rows.Add(found.FirstOrDefault(f => f.Source == MigrationSource.Hdt) is { } hdt
            ? Row(Loc.T("Migration.Source.Hdt"), hdt)
            : _settings.HdtFolder is { Length: > 0 } && Directory.Exists(hdtFolder)
                ? new MigrationSourceRow(Loc.T("Migration.Source.Hdt"), string.Empty, Loc.T("Migration.Status.NoHdtFiles"), hdtFolder, false, false, false, true)
                : new MigrationSourceRow(Loc.T("Migration.Source.Hdt"), string.Empty, Loc.T("Migration.Status.Missing"), string.Empty, false, true, false, false));
        rows.Add(found.FirstOrDefault(f => f.Source == MigrationSource.Firestone) is { } firestone
            ? Row(Loc.T("Migration.Source.Firestone"), firestone)
            : new MigrationSourceRow(Loc.T("Migration.Source.Firestone"), string.Empty, Loc.T("Migration.Status.Missing"), string.Empty, false, true, false, false));
        return rows;
    }

    private static MigrationSourceRow Row(string name, MigrationFound found) =>
        found.ServerOnly ? new MigrationSourceRow(name, string.Empty, Loc.T("Migration.Status.ServerOnly"), string.Empty, false, false, true, false)
        : !found.Counted ? new MigrationSourceRow(name, Loc.T("Migration.Count.Unknown"), string.Empty, found.Path, true, false, false, false)
        : found.Games + found.Decks == 0 ? new MigrationSourceRow(name, string.Empty, Loc.T("Migration.Status.Empty"), found.Path, false, true, false, false)
        : new MigrationSourceRow(name, Counts(found.Games, found.Decks), string.Empty, found.Path, true, false, false, false);
}
