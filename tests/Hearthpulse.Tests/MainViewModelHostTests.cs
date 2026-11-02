using Hearthpulse.Core;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;
using Hearthpulse.UI.Main;

namespace Hearthpulse.Tests;

/// <summary>
/// What both hosts used to keep themselves and now report into the main view model (<c>MainViewModel.Host.cs</c>): the
/// busy line and the update status follow a language switch, and the log import marks the history as imported.
/// </summary>
[Collection(nameof(GlobalLanguage))]
public sealed class MainViewModelHostTests : IDisposable
{
    private readonly string _db = TestDataFolder.Scratch("hp-host-vm") + ".db";
    private readonly HistoryStore _history;

    public MainViewModelHostTests()
    {
        _history = new HistoryStore(_db);
        Language(Loc.English);
    }

    public void Dispose()
    {
        Language(Loc.English);
        TestDataFolder.TryDelete(_db);
    }

    [Fact]
    public void The_busy_line_and_the_update_status_are_shown_again_in_the_new_language()
    {
        var vm = new MainViewModel(_history, new AppSettings());
        vm.ShowBusy(() => Loc.T("Host.Busy.NothingToImport"));
        vm.SetUpdateStatus(() => Loc.T("Host.Update.Off"));
        Assert.Equal("Hearthstone install not found — nothing to import.", vm.Busy);
        Assert.Equal("Automatic updates are off.", vm.UpdateStatus);

        Language(Loc.Russian);

        Assert.Equal(Loc.T("Host.Busy.NothingToImport"), vm.Busy);
        Assert.StartsWith("Hearthstone не найден", vm.Busy, StringComparison.Ordinal);
        Assert.Equal("Автообновление выключено.", vm.UpdateStatus);
    }

    [Fact]
    public void A_cleared_busy_line_stays_clear_and_a_ready_update_replaces_the_status()
    {
        var vm = new MainViewModel(_history, new AppSettings());
        vm.ShowBusy(() => Loc.T("Host.Busy.Importing"));
        vm.ShowBusy(null);
        vm.SetUpdateStatus(() => Loc.T("Host.Update.Off"));
        vm.SetUpdateReady("9.9.9");

        Language(Loc.Russian);

        Assert.Equal(string.Empty, vm.Busy);
        Assert.DoesNotContain("Автообновление", vm.UpdateStatus, StringComparison.Ordinal);
        Assert.Contains("9.9.9", vm.UpdateStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_the_game_folder_nothing_is_imported_and_the_flags_stay()
    {
        var settings = new AppSettings();
        var vm = new MainViewModel(_history, settings);

        await vm.ImportLogsAsync(null, force: true);

        Assert.Equal(Loc.T("Host.Busy.NothingToImport"), vm.Busy);
        Assert.False(settings.HistoryImported);
    }

    [Fact]
    public async Task An_import_marks_the_history_imported_filled_and_read_again()
    {
        var install = TestDataFolder.Scratch("hp-host-install");
        Directory.CreateDirectory(install);
        var settings = new AppSettings();
        var vm = new MainViewModel(_history, settings);

        await vm.ImportLogsAsync(install, force: false);

        Assert.Equal((true, true, true), (settings.HistoryImported, settings.HistoryStatsFilled, settings.HistoryCardsFilled));
        TestDataFolder.TryDelete(install);
    }

    /// <summary>
    /// Switches the language on the headless UI thread: views the Avalonia binding tests leave alive follow every
    /// switch, and they may only be touched from that thread.
    /// </summary>
    private static void Language(string language) => LocBindingTests.Headless.Run(() => Loc.Instance.SetLanguage(language));
}
