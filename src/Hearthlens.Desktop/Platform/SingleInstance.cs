using Hearthlens.Core;

namespace Hearthlens.Desktop.Platform;

/// <summary>Keeps one Hearthlens per user session (named mutexes work on Windows, macOS and Linux).</summary>
internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex? _mutex;

    private SingleInstance(Mutex? mutex, bool first)
    {
        _mutex = mutex;
        IsFirst = first;
    }

    public bool IsFirst { get; }

    public static SingleInstance Acquire()
    {
        try
        {
            var mutex = new Mutex(true, @"Local\Hearthlens.SingleInstance", out var first);
            return new SingleInstance(mutex, first);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException or PlatformNotSupportedException)
        {
            // Better two copies than none.
            Diag.Error("Single-instance check unavailable", ex);
            return new SingleInstance(null, true);
        }
    }

    public void Dispose() => _mutex?.Dispose();
}
