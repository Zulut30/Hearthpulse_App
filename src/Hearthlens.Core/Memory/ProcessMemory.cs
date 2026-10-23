using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Hearthlens.Core.Memory;

/// <summary>Read-only handle to another process's address space.</summary>
public sealed class ProcessMemory : IDisposable
{
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint StillActive = 259;

    private nint _handle;

    private ProcessMemory(int processId, nint handle)
    {
        ProcessId = processId;
        _handle = handle;
    }

    public int ProcessId { get; }

    // A scan follows pointers it read a moment earlier. When the game frees an object mid-scan those pointers turn
    // into garbage, and a garbage length or link can keep a scan busy for minutes. Each scan therefore gets a read
    // budget: once it is spent every further read fails at once and the scan ends with nothing.
    [ThreadStatic] private static int t_readsLeft;
    [ThreadStatic] private static long t_deadline;
    [ThreadStatic] private static bool t_exhausted;

    /// <summary>True when the current thread's read budget ran out, so the scan's result is incomplete.</summary>
    public static bool BudgetExhausted => t_exhausted;

    /// <summary>Limits the reads of the current thread until the returned scope is disposed.</summary>
    public static ReadBudget Budget(int maxReads, TimeSpan maxTime) => new(maxReads, maxTime);

    private static bool TakeFromBudget()
    {
        if (t_deadline == 0)
        {
            return true;
        }

        if (t_exhausted || --t_readsLeft < 0 || Stopwatch.GetTimestamp() > t_deadline)
        {
            t_exhausted = true;
            return false;
        }

        return true;
    }

    public readonly struct ReadBudget : IDisposable
    {
        private readonly int _readsLeft;
        private readonly long _deadline;
        private readonly bool _exhausted;

        internal ReadBudget(int maxReads, TimeSpan maxTime)
        {
            (_readsLeft, _deadline, _exhausted) = (t_readsLeft, t_deadline, t_exhausted);
            t_readsLeft = maxReads;
            t_deadline = Stopwatch.GetTimestamp() + (long)(maxTime.TotalSeconds * Stopwatch.Frequency);
            t_exhausted = false;
        }

        public void Dispose() => (t_readsLeft, t_deadline, t_exhausted) = (_readsLeft, _deadline, _exhausted);
    }

    public bool HasExited => !GetExitCodeProcess(_handle, out var code) || code != StillActive;

    public static ProcessMemory? Open(int processId)
    {
        var handle = OpenProcess(ProcessVmRead | ProcessQueryLimitedInformation, false, processId);
        return handle == 0 ? null : new ProcessMemory(processId, handle);
    }

    public ulong FindModuleBase(string moduleName)
    {
        try
        {
            using var process = Process.GetProcessById(ProcessId);
            foreach (ProcessModule module in process.Modules)
            {
                if (string.Equals(module.ModuleName, moduleName, StringComparison.OrdinalIgnoreCase))
                {
                    return (ulong)module.BaseAddress;
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException)
        {
        }

        return 0;
    }

    public unsafe bool TryRead(ulong address, Span<byte> buffer)
    {
        if (address < 0x10000 || _handle == 0 || !TakeFromBudget())
        {
            return false;
        }

        fixed (byte* ptr = buffer)
        {
            return ReadProcessMemory(_handle, (nint)address, ptr, buffer.Length, out var read) && read == buffer.Length;
        }
    }

    public byte[]? ReadBytes(ulong address, int count)
    {
        var buffer = new byte[count];
        return TryRead(address, buffer) ? buffer : null;
    }

    public ulong ReadPtr(ulong address)
    {
        Span<byte> b = stackalloc byte[8];
        return TryRead(address, b) ? BinaryPrimitives.ReadUInt64LittleEndian(b) : 0;
    }

    public long ReadInt64(ulong address) => (long)ReadPtr(address);

    public int ReadInt32(ulong address)
    {
        Span<byte> b = stackalloc byte[4];
        return TryRead(address, b) ? BinaryPrimitives.ReadInt32LittleEndian(b) : 0;
    }

    public uint ReadUInt32(ulong address) => (uint)ReadInt32(address);

    public ushort ReadUInt16(ulong address)
    {
        Span<byte> b = stackalloc byte[2];
        return TryRead(address, b) ? BinaryPrimitives.ReadUInt16LittleEndian(b) : (ushort)0;
    }

    public byte ReadByte(ulong address)
    {
        Span<byte> b = stackalloc byte[1];
        return TryRead(address, b) ? b[0] : (byte)0;
    }

    public bool ReadBool(ulong address) => ReadByte(address) != 0;

    public float ReadSingle(ulong address) => BitConverter.Int32BitsToSingle(ReadInt32(address));

    public double ReadDouble(ulong address) => BitConverter.Int64BitsToDouble(ReadInt64(address));

    /// <summary>Reads a NUL-terminated ASCII/UTF-8 string (Mono metadata names).</summary>
    public string? ReadCString(ulong address, int maxLength = 512)
    {
        if (address == 0)
        {
            return null;
        }

        Span<byte> chunk = stackalloc byte[64];
        var bytes = new List<byte>(32);
        for (var offset = 0; offset < maxLength; offset += chunk.Length)
        {
            if (!TryRead(address + (ulong)offset, chunk))
            {
                // Chunk may straddle an unmapped page: fall back to byte reads.
                for (var i = 0; i < chunk.Length && offset + i < maxLength; i++)
                {
                    var single = ReadByteOrNull(address + (ulong)(offset + i));
                    if (single is null or 0)
                    {
                        return bytes.Count == 0 && single is null ? null : Encoding.UTF8.GetString(bytes.ToArray());
                    }

                    bytes.Add(single.Value);
                }

                continue;
            }

            var end = chunk.IndexOf((byte)0);
            if (end >= 0)
            {
                for (var i = 0; i < end; i++) bytes.Add(chunk[i]);
                return Encoding.UTF8.GetString(bytes.ToArray());
            }

            foreach (var c in chunk) bytes.Add(c);
        }

        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    /// <summary>Reads a managed System.String given the object address.</summary>
    public string? ReadMonoString(ulong stringObject)
    {
        if (stringObject == 0)
        {
            return null;
        }

        var length = ReadInt32(stringObject + MonoOffsets.StringLength);
        if (length is < 0 or > 16384)
        {
            return null;
        }

        if (length == 0)
        {
            return string.Empty;
        }

        var raw = ReadBytes(stringObject + MonoOffsets.StringChars, length * 2);
        return raw is null ? null : Encoding.Unicode.GetString(raw);
    }

    private byte? ReadByteOrNull(ulong address)
    {
        Span<byte> b = stackalloc byte[1];
        return TryRead(address, b) ? b[0] : null;
    }

    public void Dispose()
    {
        if (_handle != 0)
        {
            CloseHandle(_handle);
            _handle = 0;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern unsafe bool ReadProcessMemory(nint process, nint baseAddress, byte* buffer, nint size, out nint read);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(nint process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);
}
