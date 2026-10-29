namespace Hearthpulse.Core;

/// <summary>
/// Errors that reached one of the apps' global handlers (UI thread, a dying thread, an unobserved task). Each is
/// written to the log; the hosts show the player a window the first time a kind of error happens.
/// </summary>
public static class CrashLog
{
    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, int> Seen = new(StringComparer.Ordinal);

    /// <summary>
    /// Logs the error and returns true the first time this kind (type and throwing method) is seen, when the player
    /// should be told. A repeating error (a timer that throws on every tick) is logged with its stack the first three
    /// times and then every hundredth time, so it cannot push everything else out of the 4 MB log.
    /// </summary>
    public static bool Record(string where, Exception ex)
    {
        int count;
        lock (Gate)
        {
            var key = $"{ex.GetType().FullName}|{ex.TargetSite?.DeclaringType?.FullName}.{ex.TargetSite?.Name}";
            count = Seen[key] = Seen.GetValueOrDefault(key) + 1;
        }

        if (count <= 3 || count % 100 == 0)
        {
            Diag.Error(count == 1 ? where : $"{where} (seen {count} times)", ex);
        }

        return count == 1;
    }

    /// <summary>One line for the error window: "InvalidOperationException: the message".</summary>
    public static string Describe(Exception ex)
    {
        var inner = ex is AggregateException { InnerExceptions.Count: 1 } aggregate ? aggregate.InnerExceptions[0] : ex;
        var message = inner.Message.ReplaceLineEndings(" ");
        return $"{inner.GetType().Name}: {(message.Length > 300 ? message[..300] + "…" : message)}";
    }
}
