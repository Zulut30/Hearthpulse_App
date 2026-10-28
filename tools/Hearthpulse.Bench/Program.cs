using System.Globalization;
using System.IO;

namespace Hearthpulse.Bench;

/// <summary>
/// Measures what docs/perf-architecture-audit.md reports: card loading, the Power.log pipeline, overlay updates,
/// history queries, start-up and memory of the WPF app. Commands print plain text so before/after runs can be diffed.
/// </summary>
internal static class Program
{
    private const string Usage = """
        Hearthpulse.Bench <command> [--logs <dir>] [--rounds <n>] [--games <n>]

          cards     load the card database: time, allocations, live heap, LOH
          replay    replay kept Power logs through GameSession: parse and publish timings, allocations
          overlay   replay snapshots into the WPF and Avalonia OverlayViewModel: time and bytes per Update, PropertyChanged
          tick      the WPF overlay tick's click-through hit test and hover along cursor paths, game window lookup
          frames    the shown WPF overlay at 1080p and 4K: process and UI-thread CPU idle, at 10 snapshots/s, a small change
                    30/s and when shown again (HP_BENCH_SIZES=WxH,... other sizes)
          hover     replay: game time during which the card under the mouse is read from the game, before and now
          cleanup   run the start-up cache clean-up on HEARTHPULSE_DATA: size of cache/ before and after (deletes files)
          history   history store and My Stats queries on 1 000 / 5 000 (/ --games) synthetic games
          startup   WPF start-up steps up to the first rendered main window and until the log watcher can start
                    (HP_BENCH_SERIAL=1: game and cards looked up after the first frame, the order before stage 6)
          ram       working set step by step (cards, meta, window, every page and tab), image cache bytes, page times
          pages     first and second opening of every page, Settings tab and stats tab, a deck page and a match page
          art       decode the cached portraits and deck tiles at once (--games caps the count): time, allocations, GCs
          idle      the overlay window with no Hearthstone window: CPU time over 20 s (run with Hearthstone closed)
          watch     LogWatcher + GameSession on the installed game's logs: CPU time over 20 s
          poll      one idle watcher pass and one "is Hearthstone running" (game closed): thread CPU per call
          meta      Battlegrounds meta, Constructed meta and the card pool catalog from the cache: time and allocations
          all       cards, replay, overlay, history

        HEARTHPULSE_DATA must point at a copy of the data folder; the bench refuses to touch the real one.
        Logs default to D:\Hearthstone\Logs.
        """;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Console.WriteLine(Usage);
            return 0;
        }

        if (Environment.GetEnvironmentVariable("HEARTHPULSE_DATA") is not { Length: > 0 } data || !Directory.Exists(data))
        {
            Console.Error.WriteLine("Set HEARTHPULSE_DATA to a copy of the data folder first (it is read and written).");
            return 2;
        }

        // cleanup deletes files and the history store may upgrade its schema: never on the folder the app uses.
        var real = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Hearthpulse"));
        if (string.Equals(Path.GetFullPath(data).TrimEnd(Path.DirectorySeparatorChar), real, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"HEARTHPULSE_DATA is the app's own data folder ({real}); point it at a copy.");
            return 2;
        }

        var options = Options.Parse(args.Skip(1).ToArray());
        Console.WriteLine($"data={data} logs={options.Logs}");
        switch (args[0])
        {
            case "cards":
                CoreBench.Cards();
                break;
            case "replay":
                CoreBench.Replay(options);
                break;
            case "overlay":
                WpfBench.Overlay(options);
                break;
            case "tick":
                TickBench.Run(options);
                break;
            case "frames":
                FrameBench.Run(options);
                break;
            case "history":
                CoreBench.History(options);
                break;
            case "hover":
                CoreBench.Hover(options);
                break;
            case "watch":
                CoreBench.Watch();
                break;
            case "poll":
                CoreBench.Poll();
                break;
            case "meta":
                CoreBench.Meta();
                break;
            case "cleanup":
                CoreBench.Cleanup();
                break;
            case "startup":
                WpfBench.Startup();
                break;
            case "ram":
                WpfBench.Ram(options);
                break;
            case "pages":
                WpfBench.Pages(options);
                break;
            case "art":
                WpfBench.Art(options);
                break;
            case "idle":
                WpfBench.OverlayIdle();
                break;
            case "all":
                CoreBench.Cards();
                CoreBench.Replay(options);
                WpfBench.Overlay(options);
                CoreBench.History(options);
                break;
            default:
                Console.Error.WriteLine(Usage);
                return 2;
        }

        return 0;
    }
}

/// <summary>Command-line options shared by the commands.</summary>
internal sealed record Options(string Logs, int Rounds, int Games)
{
    public static Options Parse(string[] args)
    {
        var logs = @"D:\Hearthstone\Logs";
        var rounds = 1;
        var games = 0;
        for (var i = 0; i + 1 < args.Length; i += 2)
        {
            switch (args[i])
            {
                case "--logs":
                    logs = args[i + 1];
                    break;
                case "--rounds":
                    rounds = int.Parse(args[i + 1], CultureInfo.InvariantCulture);
                    break;
                case "--games":
                    games = int.Parse(args[i + 1], CultureInfo.InvariantCulture);
                    break;
            }
        }

        return new Options(logs, rounds, games);
    }
}
