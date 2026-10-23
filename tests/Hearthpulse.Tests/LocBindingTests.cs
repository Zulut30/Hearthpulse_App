using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Hearthpulse.Core.Localization;
using Hearthpulse.UI.Infrastructure;

namespace Hearthpulse.Tests;

/// <summary>Switches the global language, so it runs alone: other tests read English texts.</summary>
[CollectionDefinition(nameof(GlobalLanguage), DisableParallelization = true)]
public sealed class GlobalLanguage;

/// <summary>
/// The Avalonia <c>{i:Loc Key}</c> binding on a headless platform: it follows a language switch, and it does not keep
/// controls alive through the static <see cref="Loc.Instance"/> (overlay rows are rebuilt on every snapshot).
/// </summary>
[Collection(nameof(GlobalLanguage))]
public sealed class LocBindingTests
{
    [Fact]
    public void Avalonia_texts_follow_a_switch_and_let_their_controls_go()
    {
        Headless.Start();
        var block = Bound();
        try
        {
            Assert.Equal("Settings", block.Text);
            Loc.Instance.SetLanguage(Loc.Russian);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Настройки", block.Text);

            var dropped = BindMany(500);
            for (var i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            Assert.DoesNotContain(dropped, r => r.TryGetTarget(out _));
        }
        finally
        {
            Loc.Instance.SetLanguage(Loc.English);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.Equal("Settings", block.Text);
    }

    private static TextBlock Bound()
    {
        var block = new TextBlock();
        block.Bind(TextBlock.TextProperty, new LocExtension("Settings.Title").ProvideValue(null!));
        return block;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static List<WeakReference<TextBlock>> BindMany(int count) =>
        Enumerable.Range(0, count).Select(_ => new WeakReference<TextBlock>(Bound())).ToList();

    /// <summary>One headless Avalonia platform per test run, set up on the first test that needs it.</summary>
    private static class Headless
    {
        private static readonly Lazy<bool> Setup = new(() =>
        {
            AppBuilder.Configure<Application>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
            return true;
        });

        public static void Start() => _ = Setup.Value;
    }
}
