using System.IO.Compression;
using System.Text;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Replay;

namespace Hearthpulse.Tests;

/// <summary>
/// Fuzzing the replay import (plan R5): random and broken HPR files and Power.logs, made from the golden ones by seeded
/// mutations, never throw out of the import, the log reader or the match page; what the import accepts opens on the page.
/// A light run by default; <c>HEARTHPULSE_FUZZ_ROUNDS=5000</c> for the heavy one. A failure names its seed and round:
/// <c>HEARTHPULSE_FUZZ_SEED</c> with that seed repeats it.
/// </summary>
public sealed class ReplayFuzzTests : IDisposable
{
    private static readonly string Golden = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Replay", "Golden");

    private readonly string _dir = TestDataFolder.Scratch("hp-fuzz");

    public ReplayFuzzTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => TestDataFolder.TryDelete(_dir);

    private static int Rounds => int.TryParse(Environment.GetEnvironmentVariable("HEARTHPULSE_FUZZ_ROUNDS"), out var n) && n > 0 ? n : 150;

    private static int Seed => int.TryParse(Environment.GetEnvironmentVariable("HEARTHPULSE_FUZZ_SEED"), out var n) ? n : 20261105;

    [Fact]
    public void Broken_replay_files_are_refused_or_open_but_never_throw()
    {
        var sources = Directory.GetFiles(Golden, "*.hpr").Select(Source).ToArray();
        Assert.NotEmpty(sources);
        Assert.All(sources, text => Assert.NotNull(ReplayImport.Sanitize(text, CardDb.Empty, out _)));
        var random = new Random(Seed);
        var accepted = 0;
        for (var round = 0; round < Rounds; round++)
        {
            var text = Mutate(sources[random.Next(sources.Length)], random);
            Run(round, () =>
            {
                // A damaged own file opens as it is (the page reads it without the filter).
                Open(text);
                if (ReplayImport.Sanitize(text, CardDb.Empty, out var error) is { } clean)
                {
                    accepted++;
                    Assert.Equal(ReplayImportError.None, error);
                    Assert.NotNull(ReplayFile.Read(clean));
                    Open(clean);
                }
                else
                {
                    Assert.NotEqual(ReplayImportError.None, error);
                }
            });
        }

        // The mutations are small enough that some files still pass: the accepted path is fuzzed too (checked only over
        // enough rounds, so a short run with another seed does not fail on chance).
        if (Rounds >= 100)
        {
            Assert.True(accepted > 0, $"no mutated file passed the filter (seed {Seed}, {Rounds} rounds)");
        }
    }

    [Fact]
    public void Broken_files_through_the_import_dialog_are_refused_or_imported_but_never_throw()
    {
        var sources = Directory.GetFiles(Golden, "*.*").Select(Source).ToArray();
        var random = new Random(Seed + 1);
        var imported = new ReplayStore(Path.Combine(_dir, "imported")) { AutoPrune = false };
        for (var round = 0; round < Math.Max(20, Rounds / 5); round++)
        {
            var bytes = Encoding.UTF8.GetBytes(Mutate(sources[random.Next(sources.Length)], random));
            var path = Path.Combine(_dir, $"round-{round}.hpr");
            File.WriteAllBytes(path, random.Next(4) == 0 ? Packed(bytes, random) : bytes);
            Run(round, () =>
            {
                var result = ReplayImport.ImportFile(path, imported, CardDb.Empty, Path.Combine(_dir, "work"));
                foreach (var name in result.Added)
                {
                    Open(imported.Load(name)!);
                }
            });
        }
    }

    [Fact]
    public void Broken_power_logs_never_throw_out_of_the_log_reader()
    {
        var sources = Directory.GetFiles(Golden, "*.log").Select(File.ReadAllText).ToArray();
        var random = new Random(Seed + 2);
        var replays = new ReplayStore(Path.Combine(_dir, "replays")) { AutoPrune = false };
        for (var round = 0; round < Math.Max(20, Rounds / 5); round++)
        {
            var path = Path.Combine(_dir, $"Power-{round}.log");
            File.WriteAllText(path, Mutate(sources[random.Next(sources.Length)], random));
            Run(round, () =>
            {
                foreach (var record in LogBackfill.Parse(path, new DateTime(2026, 11, 5), CardDb.Empty, replays: replays))
                {
                    if (replays.Load(record.Replay) is { } text)
                    {
                        Open(text);
                    }
                }
            });
        }
    }

    /// <summary>A golden file as the app has it: its game's start in place of the placeholder (<see cref="GoldenReplayTests"/>).</summary>
    private static string Source(string path) => File.ReadAllText(path).Replace("@STARTED@", "2026-11-05T12:30:00Z", StringComparison.Ordinal);

    /// <summary>The match page of a file (as History or the library opens it), with every step built.</summary>
    private static void Open(string text)
    {
        var page = MatchPage.LoadFile(text, "fuzz.hpr", CardDb.Empty, _ => null);
        _ = page.FrameCount;
        _ = page.Timeline;
        _ = BattlegroundsMatchReplay.Parse(text)?.Frames.Count;
        _ = MatchReplay.Parse(text);
    }

    /// <summary>A round: its exceptions and failed checks name the seed and round, and one that hangs fails (a loop on a damaged line).</summary>
    private static void Run(int round, Action body)
    {
        var task = Task.Run(body);
        bool finished;
        try
        {
            finished = task.Wait(RoundTimeout);
        }
        catch (AggregateException wrapped) when (wrapped.InnerException is Xunit.Sdk.XunitException failed)
        {
            throw new Xunit.Sdk.XunitException($"Fuzz round {round} (seed {Seed}): {failed.Message}", failed);
        }
        catch (AggregateException wrapped) when (wrapped.InnerException is { } ex)
        {
            throw new InvalidOperationException($"Fuzz round {round} (seed {Seed}) threw", ex);
        }

        if (!finished)
        {
            throw new Xunit.Sdk.XunitException($"Fuzz round {round} (seed {Seed}) ran over {RoundTimeout.TotalSeconds:F0} s");
        }
    }

    /// <summary>A round takes milliseconds; the limit leaves room for a loaded runner.</summary>
    private static readonly TimeSpan RoundTimeout = TimeSpan.FromSeconds(20);

    /// <summary>One to four edits of the text: lines dropped, doubled, swapped or cut, tokens and numbers replaced, bytes flipped, the end cut off.</summary>
    internal static string Mutate(string text, Random random)
    {
        var lines = text.Split('\n').ToList();
        for (var edits = random.Next(1, 5); edits > 0 && lines.Count > 0; edits--)
        {
            var i = random.Next(lines.Count);
            switch (random.Next(10))
            {
                case 0:
                    lines.RemoveAt(i);
                    break;
                case 1:
                    lines.Insert(i, lines[random.Next(lines.Count)]);
                    break;
                case 2:
                    (lines[i], lines[^1]) = (lines[^1], lines[i]);
                    break;
                case 3:
                    lines[i] = lines[i][..random.Next(lines[i].Length + 1)];
                    break;
                case 4:
                    lines[i] = ReplaceToken(lines[i], random, Pick(random, "-1", "0", "2147483648", "99999999999999999999", "NaN", "", "-", "null", "{", "]", "\u0000", "ё"));
                    break;
                case 5:
                    lines[i] = ReplaceToken(lines[i], random, Pick(random, "E", "L", "O", "P", "B", "M", "T", "F", "V", "X", "Y", "Z", "f", "G", "R"));
                    break;
                case 6:
                    var chars = lines[i].ToCharArray();
                    if (chars.Length > 0)
                    {
                        chars[random.Next(chars.Length)] = (char)random.Next(0x20, 0x7F);
                    }

                    lines[i] = new string(chars);
                    break;
                case 7:
                    lines.Insert(i, new string('x', random.Next(1, 4000)));
                    break;
                case 8:
                    lines.RemoveRange(i, lines.Count - i);
                    break;
                default:
                    lines.Insert(i, string.Join(' ', Enumerable.Range(0, random.Next(1, 12)).Select(_ => random.Next(-5, 300).ToString(System.Globalization.CultureInfo.InvariantCulture))));
                    break;
            }
        }

        return string.Join('\n', lines);
    }

    private static string ReplaceToken(string line, Random random, string token)
    {
        var parts = line.Split(' ');
        parts[random.Next(parts.Length)] = token;
        return string.Join(' ', parts);
    }

    private static string Pick(Random random, params string[] options) => options[random.Next(options.Length)];

    /// <summary>The bytes packed as an exported file, sometimes cut short (a damaged download).</summary>
    private static byte[] Packed(byte[] bytes, Random random)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(bytes);
        }

        var packed = output.ToArray();
        // An empty text packs to nothing at all: nothing to cut then.
        return packed.Length <= 2 || random.Next(2) == 0 ? packed : packed[..random.Next(2, packed.Length)];
    }
}
