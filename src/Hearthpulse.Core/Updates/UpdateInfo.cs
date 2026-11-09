using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Hearthpulse.Core.Localization;

namespace Hearthpulse.Core.Updates;

/// <summary>How this copy of Hearthpulse is replaced when a newer release is out.</summary>
public enum UpdateTarget
{
    /// <summary>The Windows setup wizard installed this copy: run the new setup silently.</summary>
    WindowsInstalled,

    /// <summary>A Windows zip copy: unpack the new preview zip over the app's own folder.</summary>
    WindowsPortable,

    /// <summary>macOS: download the .pkg and hand it to the installer.</summary>
    MacPackage,

    /// <summary>Linux: download the package that fits this distribution and hand it to the installer.</summary>
    LinuxPackage,
}

/// <summary>A newer release than the running one, together with the file this platform needs.</summary>
/// <param name="Sha256">The file's SHA-256 as GitHub publishes it (lower-case hex), or null when the release has none.</param>
/// <param name="Delta">The installed Windows app only: the release's package of the files changed since the running
/// version, when the release has one with a published SHA-256 (<see cref="DeltaUpdate"/>).</param>
public sealed record UpdateInfo(string Version, string AssetName, string DownloadUrl, string? Sha256 = null, DeltaAsset? Delta = null)
{
    /// <summary>Release body as plain text (markdown stripped), at most 1500 chars cut at a line end with "…"; null when empty.</summary>
    public string? Notes { get; init; }

    /// <summary>The release page, only when it starts with https://github.com/Zulut30/Hearthpulse_App/releases/; else null.</summary>
    public Uri? PageUrl { get; init; }

    /// <summary>The release's SHA256SUMS.txt, when it has one.</summary>
    public string? ChecksumsUrl { get; init; }

    /// <summary>The release's SHA256SUMS.txt.sig (the project's signature on the sums), when it has one.</summary>
    public string? SignatureUrl { get; init; }
}

/// <summary>What an update check found.</summary>
public enum UpdateCheckOutcome
{
    /// <summary>This copy is the newest published release.</summary>
    UpToDate,

    /// <summary>A newer release with a file for this platform is out (<see cref="UpdateCheck.Update"/>).</summary>
    Available,

    /// <summary>GitHub could not be asked or gave an answer other than a release (see the message).</summary>
    Failed,
}

/// <summary>The result of one update check, with a sentence for the settings page.</summary>
/// <param name="Message">The sentence in the language of the moment of the check (logs, tests).</param>
public sealed record UpdateCheck(UpdateCheckOutcome Outcome, UpdateInfo? Update, string Message)
{
    /// <summary>GitHub's limit: no request before this moment (Retry-After / X-RateLimit-Reset); null otherwise.</summary>
    public DateTimeOffset? RetryAfter { get; init; }

    /// <summary>The same sentence built on each call, so the settings page follows a language switch; by default
    /// <see cref="Message"/> as it is.</summary>
    public Func<string> Text { get; init; } = () => Message;

    /// <summary>A check whose sentence is <paramref name="text"/>, built now for <see cref="Message"/> and again on
    /// each <see cref="Text"/> call.</summary>
    internal static UpdateCheck Of(UpdateCheckOutcome outcome, UpdateInfo? update, Func<string> text) =>
        new(outcome, update, text()) { Text = text };

    /// <summary>Equal by what was found and said; <see cref="Text"/> is how the sentence is built, not part of it.</summary>
    public bool Equals(UpdateCheck? other) =>
        other is not null && Outcome == other.Outcome && Equals(Update, other.Update) && Message == other.Message && RetryAfter == other.RetryAfter;

    public override int GetHashCode() => HashCode.Combine(Outcome, Update, Message, RetryAfter);
}
