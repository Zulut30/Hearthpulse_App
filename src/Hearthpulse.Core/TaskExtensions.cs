namespace Hearthpulse.Core;

public static class TaskExtensions
{
    /// <summary>
    /// Lets a task run without awaiting it. A failure is written to the log as soon as it happens; a discarded task
    /// (<c>_ = SomethingAsync()</c>) kept it until a later garbage collection raised it as unobserved, or lost it.
    /// </summary>
    /// <param name="what">What the task does, for the log line ("History refresh failed: ..."). A failure that repeats
    /// (an image load on every card while the disk is full) is logged as <see cref="CrashLog.Record"/> does: three times,
    /// then every hundredth.</param>
    public static void Forget(this Task task, string what) =>
        _ = task.ContinueWith(
            t => CrashLog.Record($"{what} failed", t.Exception!.InnerExceptions.Count == 1 ? t.Exception.InnerException! : t.Exception),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
}
