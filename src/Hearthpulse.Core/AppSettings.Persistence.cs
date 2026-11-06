using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Hearthpulse.Core;

/// <summary>Reading settings.json with retries and salvage, and saving it atomically (unreadable files are kept aside).</summary>
public sealed partial class AppSettings
{
    public static AppSettings Load() => Load(AppPaths.Settings);

    /// <summary>Reads a specific file without changing the process-wide data folder.</summary>
    internal static AppSettings Load(string path) => Load(path, RetryPause);

    /// <summary>
    /// <see cref="Load(string)"/> with the pause between read attempts given: tests release a held file there instead
    /// of racing a timer.
    /// </summary>
    internal static AppSettings Load(string path, Action retryPause)
    {
        string? unpreserved = null;
        try
        {
            if (File.Exists(path))
            {
                return FromJson(ReadWithRetry(path, retryPause));
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Diag.Error("Settings unreadable, using defaults", ex);
            if (!PreserveUnreadable(path))
            {
                unpreserved = path;
            }
            else if (ex is JsonException && Salvage(path) is { } salvaged)
            {
                return salvaged;
            }
        }

        var fresh = new AppSettings { _unpreservedFile = unpreserved };
        fresh.Migrate();
        return fresh;
    }

    /// <summary>
    /// The file's text. A file another program holds for a moment (an antivirus scan, a cloud sync) is read again for up
    /// to a second before it counts as unreadable: defaults would otherwise be saved over it later.
    /// </summary>
    private static string ReadWithRetry(string path, Action retryPause)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return File.ReadAllText(path);
            }
            catch (IOException) when (attempt < 10 && File.Exists(path))
            {
                retryPause();
            }
        }
    }

    private static void RetryPause() => Thread.Sleep(100);

    /// <summary>
    /// Keeps every setting of a file that failed to load except the values that cannot be read (a null number, an enum
    /// name from another version...): one bad value must not reset the overlay layout, the pool id and the rest. Null
    /// when the file is not a JSON object at all.
    /// </summary>
    private static AppSettings? Salvage(string path)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject file)
            {
                return null;
            }

            var kept = new JsonObject();
            foreach (var (name, value) in file)
            {
                var one = new JsonObject { [name] = value?.DeepClone() };
                try
                {
                    JsonSerializer.Deserialize<AppSettings>(one.ToJsonString(), Json);
                    kept[name] = value?.DeepClone();
                }
                catch (JsonException)
                {
                    Diag.Info($"Setting {name} unreadable, using its default");
                }
            }

            return FromJson(kept.ToJsonString());
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save() => Save(AppPaths.Settings);

    internal void Save(string path)
    {
        // If recovery could not make a diagnostic copy (for example the disk was full), do not silently replace the
        // original on shutdown. Retry copying first, so normal saving resumes when the storage problem is resolved.
        if (_unpreservedFile is { } unreadable)
        {
            if (!PreserveUnreadable(unreadable)) return;
            _unpreservedFile = null;
        }

        try
        {
            var temp = path + ".tmp";
            File.WriteAllText(temp, ToJson());
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diag.Error("Settings not saved", ex);
        }
    }

    private static bool PreserveUnreadable(string path)
    {
        try
        {
            var backup = path + ".invalid";
            if (File.Exists(backup)) backup += "." + Guid.NewGuid().ToString("N");
            File.Copy(path, backup, overwrite: false);
            Diag.Info($"Unreadable settings preserved at {backup}");
            return true;
        }
        catch (FileNotFoundException)
        {
            // The original was removed since the read failed; there is no longer anything to overwrite.
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diag.Error("Unreadable settings could not be preserved; saving is paused", ex);
            return false;
        }
    }
}
