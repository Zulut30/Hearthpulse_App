using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Hearthpulse.Core.SourceCapture.Processing;

/// <summary>Testable CLI boundary shared by the standalone worker; it never uses application data or diagnostics.</summary>
public static class SourceWorkerCommand
{
    /// <summary>Stable camel-case wire serialization, including explicit nulls. No clock or machine-dependent fields.</summary>
    public static string Serialize(SourceCaptureResult result) => JsonSerializer.Serialize(result, JsonSerializerOptions.Web);

    /// <summary>Runs --input/--output, atomically replaces output, and emits one fixed-shape safe status line.</summary>
    [SuppressMessage("Usage", "VSTHRD103:Call async methods when in an async method",
        Justification = "Flush(true) is required before atomic rename; .NET has no asynchronous disk-durability flush.")]
    public static async Task<int> RunAsync(string[] args, TextWriter stdout, CancellationToken cancellationToken = default)
    {
        string? temp = null;
        object status;
        int exit;
        try
        {
            var paths = Arguments(args);
            var input = new FileStream(paths.Input, FileMode.Open, FileAccess.Read, FileShare.Read,
                8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var inputScope = input.ConfigureAwait(false);
            var result = await new SourceCaptureParser().ParseAsync(input, cancellationToken).ConfigureAwait(false);
            string folder = Path.GetDirectoryName(paths.Output)!;
            temp = Path.Combine(folder, ".source-worker-" + Guid.NewGuid().ToString("N") + ".tmp");
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough,
            };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            var output = new FileStream(temp, options);
            await using (output.ConfigureAwait(false))
            {
                await JsonSerializer.SerializeAsync(output, result, JsonSerializerOptions.Web, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temp, paths.Output, overwrite: true);
            temp = null;
            status = new { ok = true, status = result.Status, parserVersion = result.ParserVersion,
                records = result.Records, unsupported = result.Unsupported, gaps = result.Gaps };
            exit = 0;
        }
        catch (SourceCaptureException e) { status = Failure(e.Code); exit = 2; }
        catch (OperationCanceledException) { status = Failure("cancelled"); exit = 3; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
            or System.Security.SecurityException)
        { status = Failure("io_error"); exit = 4; }
        catch (Exception) { status = Failure("worker_failed"); exit = 1; }
        finally
        {
            if (temp is not null)
            {
                try { File.Delete(temp); }
                catch (IOException) { /* No input/path details may enter diagnostics. */ }
                catch (UnauthorizedAccessException) { /* Parent's private job-directory cleanup is the fallback. */ }
            }
        }
        await stdout.WriteLineAsync(JsonSerializer.Serialize(status, JsonSerializerOptions.Web)).ConfigureAwait(false);
        return exit;
    }

    private static object Failure(string error) => new { ok = false, status = "failed", error };

    private static (string Input, string Output) Arguments(string[] args)
    {
        if (args.Length != 4) throw new SourceCaptureException("invalid_arguments");
        string? input = null, output = null;
        for (int i = 0; i < args.Length; i += 2)
        {
            if (string.IsNullOrWhiteSpace(args[i + 1])) throw new SourceCaptureException("invalid_arguments");
            if (args[i] == "--input" && input is null) input = args[i + 1];
            else if (args[i] == "--output" && output is null) output = args[i + 1];
            else throw new SourceCaptureException("invalid_arguments");
        }
        if (input is null || output is null) throw new SourceCaptureException("invalid_arguments");
        input = Path.GetFullPath(input);
        output = Path.GetFullPath(output);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(input, output, comparison)) throw new SourceCaptureException("invalid_arguments");
        return (input, output);
    }
}
