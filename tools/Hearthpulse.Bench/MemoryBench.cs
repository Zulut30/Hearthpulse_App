using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Hearthpulse.Core.Memory;

namespace Hearthpulse.Bench;

/// <summary>
/// <c>memory &lt;out-dir&gt;</c>: records a memory fixture from the running game. Every byte the reads below take (attach,
/// Battlegrounds rating, tribes, button costs, the Arena offer) is kept in <c>&lt;out-dir&gt;/game.hpmem</c>, and what
/// they returned in <c>game.expected.json</c>; MemoryFixtureTests reads the image back and must return the same.
/// It reads no player names (no match players), but an image holds whatever those objects hold: look before committing.
/// </summary>
internal static class MemoryBench
{
    public static int Record(string outDir)
    {
        var game = Process.GetProcessesByName("Hearthstone").FirstOrDefault();
        if (game is null)
        {
            Console.Error.WriteLine("Hearthstone is not running.");
            return 1;
        }

        using var live = ProcessMemory.Open(game.Id);
        if (live is null)
        {
            Console.Error.WriteLine("Cannot open the Hearthstone process.");
            return 1;
        }

        var recording = new RecordingMemorySource(live.Source);
        using var memory = new HearthstoneMemory();
        if (!memory.AttachTo(ProcessMemory.FromSource(recording, game.Id)))
        {
            Console.Error.WriteLine("Attach failed: " + memory.Status);
            return 1;
        }

        var expected = MemoryFixture.Read(memory);
        Directory.CreateDirectory(outDir);
        using (var file = File.Create(Path.Combine(outDir, "game.hpmem")))
        {
            recording.Image.Save(file);
        }

        File.WriteAllText(Path.Combine(outDir, "game.expected.json"), JsonSerializer.Serialize(expected, MemoryFixture.Json));
        Console.WriteLine($"{recording.Image.RegionCount:N0} regions, {recording.Image.ByteCount:N0} bytes: {JsonSerializer.Serialize(expected, MemoryFixture.Json)}");
        return 0;
    }
}
