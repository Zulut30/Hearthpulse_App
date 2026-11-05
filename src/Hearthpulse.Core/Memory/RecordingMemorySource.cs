namespace Hearthpulse.Core.Memory;

/// <summary>
/// Passes reads through to <paramref name="inner"/> and keeps every byte that came back, and every module base asked
/// for, in <see cref="Image"/>: a scan of the running game recorded as a test fixture. Reads that failed are not kept,
/// so they fail again on replay.
/// </summary>
public sealed class RecordingMemorySource(IMemorySource inner) : IMemorySource
{
    public MemoryImage Image { get; } = new();

    public bool HasExited => inner.HasExited;

    public bool TryRead(ulong address, Span<byte> buffer)
    {
        if (!inner.TryRead(address, buffer))
        {
            return false;
        }

        Image.Add(address, buffer);
        return true;
    }

    public ulong FindModuleBase(string moduleName)
    {
        var address = inner.FindModuleBase(moduleName);
        if (address != 0)
        {
            Image.AddModule(moduleName, address);
        }

        return address;
    }
}
