using System.Text;
using System.Text.Json;
using Hearthpulse.Bench;
using Hearthpulse.Core.Memory;

namespace Hearthpulse.Tests;

/// <summary>
/// T3 Memory: the reads the memory scans are built on, over a recorded image instead of the live game. The image reads
/// only what was recorded (an unmapped page fails as in the game), the typed and string reads parse it as the game
/// lays it out, and a recording of a scan reads back the same.
/// </summary>
public sealed class MemoryImageTests
{
    private const ulong Base = 0x7FF6_0000_1000;

    [Fact]
    public void An_image_reads_only_within_what_was_recorded()
    {
        var image = new MemoryImage();
        image.Add(Base, [1, 2, 3, 4]);
        image.Add(Base + 8, [9, 9]);
        Span<byte> two = stackalloc byte[2];

        Assert.True(image.TryRead(Base + 1, two));
        Assert.Equal([2, 3], two.ToArray());
        Assert.False(image.TryRead(Base + 3, two)); // runs past the region's end into the gap
        Assert.False(image.TryRead(Base - 1, two));
        Assert.Equal(2, image.RegionCount);
    }

    [Fact]
    public void Overlapping_and_touching_reads_merge_and_the_newest_bytes_win()
    {
        var image = new MemoryImage();
        image.Add(Base, [1, 1, 1, 1]);
        image.Add(Base + 8, [3, 3]);
        image.Add(Base + 4, [2, 2, 2, 2]); // touches both: one region of ten
        image.Add(Base + 2, [7]); // inside: replaces

        Assert.Equal(1, image.RegionCount);
        Assert.Equal(10, image.ByteCount);
        var all = new byte[10];
        Assert.True(image.TryRead(Base, all));
        Assert.Equal([1, 1, 7, 1, 2, 2, 2, 2, 3, 3], all);
    }

    [Fact]
    public void An_image_saves_and_loads_its_regions_and_modules()
    {
        var image = new MemoryImage();
        image.Add(Base, [5, 6, 7]);
        image.Add(Base + 0x1000, Encoding.ASCII.GetBytes("Assembly-CSharp\0"));
        image.AddModule("mono-2.0-bdwgc.dll", 0x7FFA_1000_0000);
        using var stream = new MemoryStream();
        image.Save(stream);
        stream.Position = 0;

        var loaded = MemoryImage.Load(stream);

        Assert.Equal((2, image.ByteCount), (loaded.RegionCount, loaded.ByteCount));
        Assert.Equal(0x7FFA_1000_0000UL, loaded.FindModuleBase("MONO-2.0-BDWGC.DLL"));
        Assert.Equal(0UL, loaded.FindModuleBase("UnityPlayer.dll"));
        var bytes = new byte[3];
        Assert.True(loaded.TryRead(Base, bytes));
        Assert.Equal([5, 6, 7], bytes);
        Assert.Throws<InvalidDataException>(() => MemoryImage.Load(new MemoryStream(Gzip("not an image"))));
    }

    [Fact]
    public void Typed_reads_are_little_endian_and_a_failed_read_gives_zero()
    {
        var image = new MemoryImage();
        image.Add(Base, [0x78, 0x56, 0x34, 0x12, 0xF0, 0xDE, 0xBC, 0x9A]);
        using var memory = ProcessMemory.FromSource(image);

        Assert.Equal(0x9ABC_DEF0_1234_5678UL, memory.ReadPtr(Base));
        Assert.Equal(0x12345678, memory.ReadInt32(Base));
        Assert.Equal((ushort)0x5678, memory.ReadUInt16(Base));
        Assert.Equal((byte)0x78, memory.ReadByte(Base));
        Assert.True(memory.ReadBool(Base));
        Assert.Equal(0UL, memory.ReadPtr(Base + 4)); // half of it unrecorded
        Assert.Null(memory.ReadBytes(Base + 6, 4));
        Assert.Equal(0, memory.ProcessId);
        Assert.False(memory.HasExited);
    }

    [Fact]
    public void Addresses_in_the_first_64_KB_are_never_read()
    {
        var image = new MemoryImage();
        image.Add(0x100, [1, 2, 3, 4]);
        using var memory = ProcessMemory.FromSource(image);

        Assert.True(image.TryRead(0x100, new byte[4]));
        Assert.Equal(0, memory.ReadInt32(0x100));
    }

    [Fact]
    public void A_c_string_stops_at_its_nul_or_at_the_end_of_readable_memory()
    {
        var image = new MemoryImage();
        image.Add(Base, Encoding.UTF8.GetBytes("NetCacheBaconRatingInfo\0trailing"));
        // A name running to the end of a mapped region with no NUL: the 64-byte chunk fails, single bytes go on to the edge.
        image.Add(Base + 0x1000, Encoding.UTF8.GetBytes("Hearthstone"));
        using var memory = ProcessMemory.FromSource(image);

        Assert.Equal("NetCacheBaconRatingInfo", memory.ReadCString(Base));
        Assert.Equal("Hearthstone", memory.ReadCString(Base + 0x1000));
        Assert.Null(memory.ReadCString(Base + 0x2000));
        Assert.Null(memory.ReadCString(0));
        Assert.Equal("Net", memory.ReadCString(Base, maxLength: 3));
    }

    [Fact]
    public void A_mono_string_is_read_from_its_length_and_utf16_chars_and_an_absurd_length_is_refused()
    {
        var image = new MemoryImage();
        image.Add(Base, MonoString("Рейтинг"));
        image.Add(Base + 0x100, MonoString(string.Empty));
        var bogus = MonoString("x");
        BitConverter.GetBytes(20_000).CopyTo(bogus, (int)MonoOffsets.StringLength);
        image.Add(Base + 0x200, bogus);
        using var memory = ProcessMemory.FromSource(image);

        Assert.Equal("Рейтинг", memory.ReadMonoString(Base));
        Assert.Equal(string.Empty, memory.ReadMonoString(Base + 0x100));
        Assert.Null(memory.ReadMonoString(Base + 0x200));
        Assert.Null(memory.ReadMonoString(0));
    }

    [Fact]
    public void A_spent_read_budget_fails_every_further_read_until_its_scope_ends()
    {
        var image = new MemoryImage();
        image.Add(Base, [1, 0, 0, 0]);
        using var memory = ProcessMemory.FromSource(image);

        using (ProcessMemory.Budget(maxReads: 2, TimeSpan.FromMinutes(1)))
        {
            Assert.Equal(1, memory.ReadInt32(Base));
            Assert.Equal(1, memory.ReadInt32(Base));
            Assert.False(ProcessMemory.BudgetExhausted);
            Assert.Equal(0, memory.ReadInt32(Base));
            Assert.True(ProcessMemory.BudgetExhausted);
        }

        Assert.False(ProcessMemory.BudgetExhausted);
        Assert.Equal(1, memory.ReadInt32(Base));
    }

    [Fact]
    public void A_recording_keeps_what_was_read_and_reads_back_the_same()
    {
        var game = new MemoryImage();
        game.Add(Base, Encoding.UTF8.GetBytes("Assembly-CSharp\0"));
        game.Add(Base + 0x100, [0x2A, 0, 0, 0]);
        game.AddModule("mono-2.0-bdwgc.dll", 0x7FFA_1000_0000);
        var recording = new RecordingMemorySource(game);
        using (var live = ProcessMemory.FromSource(recording, processId: 4242))
        {
            Assert.Equal("Assembly-CSharp", live.ReadCString(Base));
            Assert.Equal(42, live.ReadInt32(Base + 0x100));
            Assert.Equal(0, live.ReadInt32(Base + 0x200)); // fails in the game: not kept
            Assert.Equal(0x7FFA_1000_0000UL, live.FindModuleBase("mono-2.0-bdwgc.dll"));
            Assert.Equal(0UL, live.FindModuleBase("UnityPlayer.dll"));
        }

        using var replay = ProcessMemory.FromSource(recording.Image);
        Assert.Equal("Assembly-CSharp", replay.ReadCString(Base));
        Assert.Equal(42, replay.ReadInt32(Base + 0x100));
        Assert.Equal(0x7FFA_1000_0000UL, replay.FindModuleBase("mono-2.0-bdwgc.dll"));
        Assert.Single(recording.Image.Modules);
    }

    /// <summary>
    /// Recorded fixtures of the real game (Bench <c>memory</c>, <c>Fixtures/Memory/&lt;name&gt;/game.hpmem</c>): the image
    /// read back gives what the recorder saw. Skipped while there are none.
    /// </summary>
    [MemoryFixtureFact]
    public void Recorded_games_read_back_as_they_were_recorded()
    {
        foreach (var dir in MemoryFixtureFactAttribute.Fixtures())
        {
            using var stream = File.OpenRead(Path.Combine(dir, "game.hpmem"));
            var image = MemoryImage.Load(stream);
            using var memory = new HearthstoneMemory();
            Assert.True(memory.AttachTo(ProcessMemory.FromSource(image)), dir + ": " + memory.Status);
            var expected = File.ReadAllText(Path.Combine(dir, "game.expected.json"));
            Assert.Equal(expected, JsonSerializer.Serialize(MemoryFixture.Read(memory), MemoryFixture.Json));
        }
    }

    private static byte[] MonoString(string text)
    {
        var bytes = new byte[(int)MonoOffsets.StringChars + (text.Length * 2)];
        BitConverter.GetBytes(text.Length).CopyTo(bytes, (int)MonoOffsets.StringLength);
        Encoding.Unicode.GetBytes(text).CopyTo(bytes, (int)MonoOffsets.StringChars);
        return bytes;
    }

    private static byte[] Gzip(string text)
    {
        using var output = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(output, System.IO.Compression.CompressionMode.Compress))
        {
            gzip.Write(Encoding.UTF8.GetBytes(text));
        }

        return output.ToArray();
    }
}

/// <summary>Runs only when there are recorded memory fixtures (Fixtures/Memory/*/game.hpmem).</summary>
public sealed class MemoryFixtureFactAttribute : FactAttribute
{
    public MemoryFixtureFactAttribute()
    {
        if (!Fixtures().Any())
        {
            Skip = "No recorded memory fixtures yet (Bench: memory <dir>).";
        }
    }

    public static IEnumerable<string> Fixtures()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Memory");
        return Directory.Exists(root)
            ? Directory.EnumerateDirectories(root).Where(d => File.Exists(Path.Combine(d, "game.hpmem")))
            : [];
    }
}
