using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Hearthpulse.Tests;

/// <summary>
/// T3: recorded memory images (tests/Hearthpulse.Tests/Fixtures/Memory/**) hold whatever the read objects held. None may
/// carry a BattleTag (a #-number of four digits or more) or a user's folder, in UTF-8 or UTF-16, the image unpacked.
/// </summary>
public sealed partial class MemoryFixtureGuardTests
{
    [Fact]
    public void Memory_fixtures_carry_no_battletag_and_no_user_folder()
    {
        var root = Path.Combine(RepoFiles.Root, "tests", "Hearthpulse.Tests", "Fixtures", "Memory");
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            Assert.Empty(Leaks(Contents(file)).Select(l => Path.GetRelativePath(root, file) + ": " + l));
        }
    }

    [Fact]
    public void The_guard_finds_a_battletag_or_a_user_folder_in_either_encoding_also_packed()
    {
        Assert.Equal(["#12345"], Leaks(Encoding.UTF8.GetBytes("\0\u0001Player#12345\0")));
        Assert.Equal(["#4321"], Leaks(Encoding.Unicode.GetBytes("Игрок#4321")));
        Assert.Equal([@"C:\Users\"], Leaks(Encoding.Unicode.GetBytes(@"C:\Users\someone\AppData")));
        Assert.Equal(["/home/"], Leaks(Encoding.UTF8.GetBytes("/home/someone/.config")));
        Assert.Equal(["#99999"], Leaks(Contents(Packed("x#99999"))));

        // Card ids, small numbers and the game's own folders are fine.
        Assert.Empty(Leaks(Encoding.Unicode.GetBytes("TOY_100t #12 C:\\Program Files (x86)\\Hearthstone")));
    }

    private static List<string> Leaks(byte[] bytes) =>
    [
        .. new[] { Encoding.UTF8.GetString(bytes), Encoding.Unicode.GetString(bytes), Encoding.Unicode.GetString(bytes.AsSpan(1)) }
            .SelectMany(text => Leak().Matches(text).Select(m => m.Value))
            .Distinct(StringComparer.Ordinal),
    ];

    [GeneratedRegex(@"#\d{4,}|[A-Za-z]:\\Users\\|/home/|/Users/", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Leak();

    /// <summary>A file's bytes, unpacked when it is gzip (game.hpmem is).</summary>
    private static byte[] Contents(string file) => Contents(File.ReadAllBytes(file));

    private static byte[] Contents(byte[] bytes)
    {
        if (bytes is not [0x1F, 0x8B, ..])
        {
            return bytes;
        }

        using var gzip = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] Packed(string text)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionMode.Compress))
        {
            gzip.Write(Encoding.UTF8.GetBytes(text));
        }

        return output.ToArray();
    }
}
