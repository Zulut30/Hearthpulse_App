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
          overlay   replay snapshots into the WPF OverlayViewModel: PropertyChanged per property, same-content share
          history   history store and My Stats queries on 1 000 / 5 000 (/ --games) synthetic games
          startup   WPF start-up steps up to the first rendered main window
          ram       working set step by step (cards, meta, window, every page and tab), image cache bytes, page times
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
            case "history":
                CoreBench.History(options);
                break;
            case "startup":
                WpfBench.Startup();
                break;
            case "ram":
                WpfBench.Ram(options);
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
