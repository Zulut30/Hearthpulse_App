using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Hearthpulse.Core;
using Hearthpulse.Core.Accounts;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Tracking;
using WpfMain = Hearthpulse.App.Main.MainViewModel;

namespace Hearthpulse.Presentation.Tests;

/// <summary>The WPF account center (TASK-2026-11-05-account-center, review m6): the sending switch and the connection light.</summary>
public sealed partial class UxPolishContractTests
{
    [Fact]
    public void Wpf_sending_switch_asks_before_turning_off_while_games_wait() => Sta(() =>
    {
        var history = History();
        var start = DateTime.UtcNow.AddHours(-1);
        var game = new GameRecord
        {
            Key = $"{start:yyyyMMddHHmmss}|Secret#1234|GT_RANKED", StartedAt = start, EndedAt = start.AddMinutes(8), Mode = "Ranked",
            GameType = "GT_RANKED", Format = "FT_STANDARD", PlayerName = "Secret#1234", OpponentName = "Rival#5678", PlayerClass = "MAGE",
            OpponentClass = "WARRIOR", Result = "Win",
        };
        Assert.True(history.Add(game));
        var settings = new AppSettings { UploadGamesToProfile = true };
        using var http = new HttpClient();
        var uploader = new ProfileUploader(new AccountClient(http, "https://site.test/api/v1/"), (_, _) => Task.FromResult<string?>(null),
            () => new AccountSession("u-42", 1), () => settings.UploadGamesToProfile, history,
            new ProfileUploadQueue(Path.Combine(AppPaths.Root, Guid.NewGuid().ToString("N") + "-upload.json")));
        uploader.OnGameFinished(new GameOutcome(game.Mode, game.Result, game.Placement, game.RatingBefore, null, game.HeroCardId) { Key = game.Key });
        Assert.Equal(1, uploader.PendingCount);

        var model = new WpfMain(history, settings, TestApp.Data);
        model.AttachUploader(uploader);
        var (window, root) = Open(model, "Settings", "Account");
        var toggle = Logical<CheckBox>(window).Single(c => BindingOperations.GetBinding(c, System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty)?.Path?.Path == nameof(WpfMain.UploadGames));
        Assert.True(toggle.IsChecked);

        // The player clicks it off while a game waits: the switch stays on and the question shows under it.
        toggle.IsChecked = false;
        Settle(root);
        Assert.True(toggle.IsChecked);
        Assert.True(settings.UploadGamesToProfile);
        Assert.True(model.UploadOffConfirming);
        var question = Logical<TextBlock>(window).Single(t => BindingPath(t) == nameof(WpfMain.UploadOffConfirmText));
        Assert.Equal(model.UploadOffConfirmText, question.Text);
        Assert.Equal(Visibility.Visible, ((FrameworkElement)((FrameworkElement)question.Parent).Parent).Visibility);

        // "Turn off" does it: the switch follows.
        model.ConfirmUploadOff.Execute(null);
        Settle(root);
        Assert.False(toggle.IsChecked);
        Assert.False(settings.UploadGamesToProfile);
        Assert.False(model.UploadOffConfirming);

        // The connection light reacts to hover and press like the other chrome buttons (tokens, no colours of its own).
        var light = Named<Button>(window, "AccountLinkButton");
        var triggers = light.Template.Triggers.OfType<Trigger>().ToList();
        Assert.Contains(triggers, t => t.Property == UIElement.IsMouseOverProperty && t.Value is true);
        Assert.Contains(triggers, t => t.Property == System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty && t.Value is true);
        uploader.Dispose();
        window.Close();
    });
}
