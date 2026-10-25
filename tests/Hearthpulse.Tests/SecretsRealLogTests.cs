using Hearthpulse.Core.Power;
using Hearthpulse.Core.Secrets;
using Xunit;
using Xunit.Abstractions;

namespace Hearthpulse.Tests;

/// <summary>
/// The Secrets helper over the kept Power logs of the local client. The kept games may have no opponent Secret at all,
/// so each game also gets a card-back Mage Secret for the opponent at the start of your first turn: it never fires, so
/// every rule that the real events of the game meet must rule its card out.
/// </summary>
public sealed class SecretsRealLogTests(ITestOutputHelper output)
{
    private const int FakeSecret = 99_000;

    [RealLogsFact]
    public void Real_games_narrow_a_secret_that_never_fires()
    {
        var games = 0;
        var ruledOut = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateDirectories(RealLogTests.LogRoot, "Hearthstone_*")
                     .SelectMany(d => new[] { Path.Combine(d, "Power_old.log"), Path.Combine(d, "Power.log") })
                     .Where(File.Exists))
        {
            var tracker = new SecretTracker();
            var ended = new EndListener(tracker);
            var parser = new PowerParser(DateOnly.FromDateTime(File.GetLastWriteTime(path)));
            parser.AddListener(tracker);
            parser.AddListener(ended);
            var injected = false;
            GameState? current = null;
            foreach (var line in RealLogTests.SharedLines(path))
            {
                parser.Feed(line);
                var game = parser.Game;
                if (!ReferenceEquals(game, current))
                {
                    current = game;
                    injected = false;
                }

                if (game is null || game.IsComplete || !game.IsConstructedLike || game.LocalPlayerId == 0 || game.OpponentPlayerId == 0)
                {
                    continue;
                }

                if (!injected && line.Contains("GameState.DebugPrintPower() - TAG_CHANGE Entity=GameEntity tag=STEP value=MAIN_ACTION", StringComparison.Ordinal)
                    && game.LocalPlayerEntity?.GetInt("CURRENT_PLAYER") == 1)
                {
                    // Before the injection only real Secrets may show; objectives and auras in the Secret zone are not.
                    Assert.Null(tracker.BuildView(game, SecretGame.Cards)?.Slots.FirstOrDefault(s => s.EntityId == FakeSecret));
                    injected = true;
                    foreach (var body in new[]
                             {
                                 $"FULL_ENTITY - Creating ID={FakeSecret} CardID=",
                                 "    tag=ZONE value=SECRET",
                                 $"    tag=CONTROLLER value={game.OpponentPlayerId}",
                                 "    tag=CLASS value=MAGE",
                                 "    tag=SECRET value=1",
                             })
                    {
                        parser.Feed("D 00:00:00.0000000 GameState.DebugPrintPower() - " + body);
                    }

                    parser.Flush();
                }
            }

            parser.Flush();
            foreach (var (when, view) in ended.Views)
            {
                games++;
                var slot = view.Slots.Single(s => s.EntityId == FakeSecret);
                var outs = view.Rows.Where(r => r.Possible == 0 && !r.Known).Select(r => r.Key).ToList();
                foreach (var key in outs)
                {
                    ruledOut[key] = ruledOut.GetValueOrDefault(key) + 1;
                }

                output.WriteLine($"{Path.GetFileName(Path.GetDirectoryName(path))} {when:HH:mm} {view.Pool}: {slot.Possible} left, ruled out {string.Join(", ", outs)}");
            }
        }

        output.WriteLine($"{games} games; ruled out: {string.Join(", ", ruledOut.Select(kv => $"{kv.Key}×{kv.Value}"))}");
        if (games == 0)
        {
            return;
        }

        // Every kept game has attacks on the hero and spells, so these always go.
        Assert.True(ruledOut.GetValueOrDefault("EX1_289") > 0, "Ice Barrier never ruled out");
        Assert.True(ruledOut.GetValueOrDefault("EX1_287") > 0, "Counterspell never ruled out");
    }

    /// <summary>Keeps the Secrets view each game ends with.</summary>
    private sealed class EndListener(SecretTracker tracker) : IPowerListener
    {
        public List<(DateTime When, SecretsView View)> Views { get; } = new();

        public void OnGameEnded(GameState game)
        {
            tracker.CommitIfQuiet(game);
            if (tracker.BuildView(game, SecretGame.Cards) is { } view && view.Slots.Any(s => s.EntityId == FakeSecret))
            {
                Views.Add((game.StartedAt, view));
            }
        }
    }
}
