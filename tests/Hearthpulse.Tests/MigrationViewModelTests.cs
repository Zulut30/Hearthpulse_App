using Hearthpulse.Core;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Migration;
using Hearthpulse.UI.Main.Migration;

namespace Hearthpulse.Tests;

/// <summary>The migration card's model (shared, Avalonia build): what the home page offers, transfer, result, errors.</summary>
public sealed class MigrationViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"hp-migration-vm-{Guid.NewGuid():N}");
    private readonly string _hdt;
    private readonly HistoryStore _history;
    private readonly AppSettings _settings = new() { HistoryImported = true };
    private int _searches;
    private int _refreshed;

    public MigrationViewModelTests()
    {
        _hdt = Path.Combine(_dir, "HearthstoneDeckTracker");
        HdtImportTests.WriteFixture(_hdt);
        _history = new HistoryStore(Path.Combine(_dir, "history.db"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private async Task<MigrationViewModel> Open(params MigrationFound[] found)
    {
        var vm = new MigrationViewModel(() => HdtImportTests.Cards, _history, _settings, () =>
        {
            _refreshed++;
            return Task.CompletedTask;
        }, _ =>
        {
            _searches++;
            return found;
        });
        await vm.WorkTask;
        return vm;
    }

    private MigrationFound Hdt => HdtImport.Scan(_hdt)!;

    [Fact]
    public async Task Found_hdt_data_is_offered_on_the_home_page_until_answered()
    {
        var vm = await Open(Hdt);

        Assert.True(vm.ShowOnHome);
        Assert.Equal(Loc.F("Migration.Home.Title", "HDT"), vm.HomeTitle);
        Assert.Equal([Loc.F("Migration.Found", "HDT", Loc.F("Migration.Count.Pair", Loc.P("Migration.Count.Games", 3), Loc.P("Migration.Count.Decks", 1)))], vm.HomeFoundLines);
        var row = vm.Sources[0];
        Assert.True(row.IsFound);
        Assert.Equal(_hdt, row.Folder);
        Assert.True(vm.Sources[1].IsMissing);
        Assert.True(vm.HomeShowNotNow);

        vm.NotNow.Execute(null);

        Assert.False(vm.ShowOnHome);
        Assert.True(_settings.MigrationOffered);
        Assert.True(vm.ShowMigrate);
    }

    [Fact]
    public async Task Transferring_from_the_home_page_shows_the_result_and_reads_history_again()
    {
        var vm = await Open(Hdt);

        vm.Migrate.Execute("home");
        Assert.True(vm.IsBusy);
        Assert.False(vm.CanMigrate);
        await vm.WorkTask;

        Assert.False(vm.IsBusy);
        Assert.True(vm.HasResult);
        Assert.Equal(Loc.F("Migration.Result.Moved", Loc.F("Migration.Count.Pair", Loc.P("Migration.Count.Games", 3), Loc.P("Migration.Count.Decks", 1))), vm.ResultHeadline);
        Assert.True(vm.ShowOnHome);
        Assert.True(vm.HomeShowHide);
        Assert.False(vm.HomeShowMigrate);
        Assert.Equal(Loc.T("Migration.Action.Again"), vm.MigrateLabel);
        Assert.Equal(3, _history.GameCount());
        Assert.Equal(1, _refreshed);
        Assert.Equal(3, _settings.LastMigration!.Added);

        // Again: nothing new.
        vm.Migrate.Execute(null);
        await vm.WorkTask;
        Assert.Equal(Loc.T("Migration.Result.Nothing"), vm.ResultHeadline);
        Assert.Contains(Loc.F("Migration.Result.Already", Loc.P("Migration.Count.Games", 3)), vm.ResultDetails);

        vm.Hide.Execute(null);
        Assert.False(vm.ShowOnHome);
    }

    [Fact]
    public async Task Nothing_found_or_firestone_on_its_server_offers_nothing()
    {
        var nothing = await Open();
        Assert.True(nothing.NothingFound);
        Assert.False(nothing.ShowOnHome);
        Assert.False(nothing.ShowMigrate);

        var server = await Open(new MigrationFound(MigrationSource.Firestone, _dir, 0, 0, ServerOnly: true));
        Assert.False(server.ShowOnHome);
        Assert.True(server.Sources[1].IsServerOnly);
        Assert.Equal(Loc.T("Migration.Status.ServerOnly"), server.Sources[1].StatusText);
    }

    [Fact]
    public async Task A_damaged_hdt_folder_says_why()
    {
        var found = Hdt;
        foreach (var file in Directory.EnumerateFiles(_hdt, "*.xml"))
        {
            File.WriteAllText(file, "<broken");
        }

        var vm = await Open(found);
        vm.Migrate.Execute(null);
        await vm.WorkTask;

        Assert.True(vm.HasError);
        Assert.False(vm.HasResult);
        Assert.Equal(Loc.T("Migration.Error.Title"), vm.ErrorTitle);
        Assert.Equal(Loc.F("Migration.Error.Damaged", "HDT"), vm.ErrorText);
        Assert.Equal(Loc.T("Migration.Action.Again"), vm.MigrateLabel);
    }

    [Fact]
    public async Task Once_answered_the_search_waits_for_settings_data()
    {
        _settings.MigrationOffered = true;
        var vm = await Open(Hdt);

        Assert.Equal(0, _searches);
        Assert.False(vm.ShowOnHome);

        vm.EnsureScanned();
        await vm.WorkTask;
        vm.EnsureScanned();
        await vm.WorkTask;

        Assert.Equal(1, _searches);
        Assert.True(vm.Sources[0].IsFound);
    }

    [Fact]
    public async Task Transfer_waits_for_the_first_import_of_the_logs()
    {
        _settings.HistoryImported = false;
        var vm = await Open(Hdt);

        Assert.True(vm.Waiting);
        Assert.False(vm.CanMigrate);
        vm.Migrate.Execute(null);
        Assert.False(vm.IsBusy);

        _settings.HistoryImported = true;
        vm.InputsChanged();
        Assert.False(vm.Waiting);
        Assert.True(vm.CanMigrate);
    }

    [Fact]
    public async Task A_search_that_throws_ends_with_nothing_found()
    {
        var vm = new MigrationViewModel(() => HdtImportTests.Cards, _history, _settings, () => Task.CompletedTask, _ => throw new InvalidOperationException("broken"));
        await vm.WorkTask;

        Assert.False(vm.IsScanning);
        Assert.True(vm.NothingFound);
    }

    [Fact]
    public async Task A_chosen_hdt_folder_is_kept_and_searched()
    {
        string? asked = null;
        var vm = await Open();
        vm.PickFolderRequested += start => asked = start;

        vm.PickHdt.Execute(null);
        vm.SetHdtFolder(_hdt);
        await vm.WorkTask;

        Assert.Equal(HdtImport.DefaultFolder, asked);
        Assert.Equal(_hdt, _settings.HdtFolder);
    }
}
