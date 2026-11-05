namespace Hearthpulse.Core.Memory;

/// <summary>
/// Where <see cref="ProcessMemory"/> takes its bytes from: the running game (<see cref="ProcessMemory.Open"/>), or a
/// recorded image of the reads one made (<see cref="MemoryImage"/>), so the parsing of Mono structures and of the client's
/// objects is tested on fixtures instead of a live process.
/// </summary>
public interface IMemorySource
{
    /// <summary>Fills <paramref name="buffer"/> from <paramref name="address"/>; false when any byte cannot be read.</summary>
    bool TryRead(ulong address, Span<byte> buffer);

    /// <summary>The load address of a module ("mono-2.0-bdwgc.dll"), or 0 when it is not loaded.</summary>
    ulong FindModuleBase(string moduleName);

    bool HasExited { get; }
}
