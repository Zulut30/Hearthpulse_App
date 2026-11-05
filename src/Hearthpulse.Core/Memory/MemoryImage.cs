using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Hearthpulse.Core.Memory;

/// <summary>
/// The bytes a memory scan read, by address, and the module bases it asked for: a recording of the game's memory
/// (<see cref="RecordingMemorySource"/>) that reads back the same, for tests. A read succeeds only within what was
/// recorded, as an unmapped page fails in the game.
/// </summary>
public sealed class MemoryImage : IMemorySource
{
    private const string Magic = "HPMEM1";

    // Region start -> bytes; regions never overlap or touch (Add merges them).
    private readonly SortedList<ulong, byte[]> _regions = [];
    private readonly Dictionary<string, ulong> _modules = new(StringComparer.OrdinalIgnoreCase);

    public int RegionCount => _regions.Count;

    public long ByteCount => _regions.Values.Sum(r => (long)r.Length);

    public bool HasExited => false;

    public IReadOnlyDictionary<string, ulong> Modules => _modules;

    public void AddModule(string name, ulong address) => _modules[name] = address;

    public ulong FindModuleBase(string moduleName) => _modules.GetValueOrDefault(moduleName);

    /// <summary>Records <paramref name="bytes"/> at <paramref name="address"/>, merged with the regions it overlaps or touches.</summary>
    public void Add(ulong address, ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return;
        }

        var start = address;
        var end = address + (ulong)bytes.Length;

        // Regions are sorted and apart, so the ones this touches sit together, ending with the last one starting at or
        // before its end.
        var keys = _regions.Keys;
        var last = LastAtOrBefore(end);
        var first = last;
        while (first >= 0 && keys[first] + (ulong)_regions.Values[first].Length >= start)
        {
            first--;
        }

        first++;
        var merged = new List<(ulong Start, byte[] Bytes)>();
        for (var i = first; i <= last; i++)
        {
            merged.Add((keys[i], _regions.Values[i]));
        }

        foreach (var (regionStart, regionBytes) in merged)
        {
            start = Math.Min(start, regionStart);
            end = Math.Max(end, regionStart + (ulong)regionBytes.Length);
            _regions.Remove(regionStart);
        }

        var combined = new byte[end - start];
        foreach (var (regionStart, regionBytes) in merged)
        {
            regionBytes.CopyTo(combined, (int)(regionStart - start));
        }

        // The newest read wins where it overlaps an older one.
        bytes.CopyTo(combined.AsSpan((int)(address - start)));
        _regions.Add(start, combined);
    }

    public bool TryRead(ulong address, Span<byte> buffer)
    {
        // The last region starting at or before the address must hold the whole buffer.
        var found = LastAtOrBefore(address);
        if (found < 0)
        {
            return false;
        }

        var region = _regions.Values[found];
        var offset = address - _regions.Keys[found];
        if (offset + (ulong)buffer.Length > (ulong)region.Length)
        {
            return false;
        }

        region.AsSpan((int)offset, buffer.Length).CopyTo(buffer);
        return true;
    }

    /// <summary>The index of the last region starting at or before <paramref name="address"/>, or -1.</summary>
    private int LastAtOrBefore(ulong address)
    {
        var keys = _regions.Keys;
        int lo = 0, hi = keys.Count - 1, found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (keys[mid] <= address)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return found;
    }

    /// <summary>Writes the image gzip-compressed: modules, then regions.</summary>
    public void Save(Stream stream)
    {
        using var gzip = new GZipStream(stream, CompressionLevel.SmallestSize, leaveOpen: true);
        using var writer = new BinaryWriter(gzip, Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(_modules.Count);
        foreach (var (name, address) in _modules.OrderBy(m => m.Key, StringComparer.Ordinal))
        {
            writer.Write(name);
            writer.Write(address);
        }

        writer.Write(_regions.Count);
        foreach (var (address, bytes) in _regions)
        {
            writer.Write(address);
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }
    }

    public static MemoryImage Load(Stream stream)
    {
        using var gzip = new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true);
        using var reader = new BinaryReader(gzip, Encoding.UTF8, leaveOpen: true);
        string magic;
        try
        {
            magic = reader.ReadString();
        }
        catch (EndOfStreamException)
        {
            magic = string.Empty;
        }

        if (magic != Magic)
        {
            throw new InvalidDataException("Not a Hearthpulse memory image");
        }

        var image = new MemoryImage();
        for (var i = reader.ReadInt32(); i > 0; i--)
        {
            image.AddModule(reader.ReadString(), reader.ReadUInt64());
        }

        for (var i = reader.ReadInt32(); i > 0; i--)
        {
            var address = reader.ReadUInt64();
            var length = reader.ReadInt32();
            image.Add(address, reader.ReadBytes(length));
        }

        return image;
    }

    /// <summary>The little-endian bytes of <paramref name="value"/>, for building an image by hand.</summary>
    public static byte[] Bytes(ulong value)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        return bytes;
    }
}
