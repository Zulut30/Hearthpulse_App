using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Threading;
using Hearthpulse.App.Infrastructure;
using Hearthpulse.Core;
using Hearthpulse.Core.Localization;

namespace Hearthpulse.Presentation.Tests;

/// <summary>
/// WPF checks run on a thread of their own: STA, its dispatcher shut down afterwards, its failure thrown on the test's
/// thread as it was. The tests of this assembly share one <see cref="Application"/> and its theme.
/// </summary>
internal static class WpfThread
{
    /// <summary>Runs <paramref name="body"/> as it is; <paramref name="english"/> sets the interface language first.</summary>
    public static void Run(Action body, bool english = false)
    {
        if (english)
        {
            Loc.Instance.SetLanguage(Loc.English);
        }

        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }

    /// <summary>With the Tavern theme loaded, unless an earlier test loaded one already.</summary>
    public static void RunThemed(Action body) => Run(() =>
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        if (app.Resources.MergedDictionaries.Count == 0)
        {
            app.Resources.MergedDictionaries.Add(ThemeEngine.Build(ThemePalette.Resolve("Tavern", 0, 0, 1)));
        }

        body();
    });

    /// <summary>In English, with the default theme loaded afresh (whole windows: what an earlier test left goes).</summary>
    public static void RunFresh(Action body) => Run(
        () =>
        {
            var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Clear();
            app.Resources.MergedDictionaries.Add(ThemeEngine.Build(ThemePalette.Resolve(ThemePalette.DefaultName, 0, 0, 1)));
            body();
        },
        english: true);
}
