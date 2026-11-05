# Crash reports (plan 0.5)

When the app hits an unexpected error, the player may send a report to hearthpulse.net. Off by default; nothing is
written or sent without consent. Site side: HeartPulse `docs/specs/crash-reports.md` (`POST /api/v1/crash-reports`).

## Consent

- **"Send report"** in the error window (WPF `Infrastructure/CrashDialog.cs`, Avalonia `Desktop/CrashWindow.cs`): this one
  report, now; when the app has to close, it is queued and goes at the next start ("The report goes at the next start").
- **Settings → Data → "Send crash reports automatically"** (`AppSettings.SendCrashReports`, off): every new kind of error
  (the first time `CrashLog.Record` sees it) and every crash that ends the process; the window then says the report will
  be sent (`Host.Crash.AutoSent`: the send runs in the background and may wait for the next start, so the window never
  claims it went), or, for a crash that ends the app, that it goes at the next start. Turning it off deletes what waits
  (`CrashReporter.ClearPending`): consent withdrawn.
- The switch's note and the installer notice (`installer/windows/notice.txt`, "CRASH REPORTS") say what is sent.

## What a report holds

`Core/Diagnostics/CrashReport.cs`: the app's version, the UI (`wpf` / `avalonia`), the OS and its architecture, the .NET
version, the interface language, the handler that caught it ("UI error", "Fatal error", "Unhandled error", "Background
error"), how long the app ran, the time (UTC), a random report id, and up to five exceptions of the chain: the type, the
message and the stack. `CrashReport.Scrub` cuts out e-mails, BattleTags, quoted values (`The given key '<value>'…`: the
runtime puts the failing key or input there), `key=<value>` pairs, the user profile path and the OS user name, every
absolute path whole (from its drive, UNC root, `%USERPROFILE%` or /home, /Users… to a quote or the end, spaces included),
IP addresses and numbers of 7 digits and more; stack lines lose their " in file:line" part; texts are cut at 300
characters, and the report at 30 KB (frames of the innermost exceptions go first). No account, token, history, settings,
game logs or memory values.

## Sending

`Core/Diagnostics/CrashReporter.cs`, one per app, built by `CrashReporter.ForApp` right after the settings load (WPF
`App.xaml.cs`, Avalonia `DesktopApp`, before the history, the session and the windows), so a crash at start is reported
too; never while rendering off-screen. It has its own HTTP client and sends no `Authorization`. A report is written to `crash-reports/` in the data folder first (at most 5 wait; older than 30 days are
dropped), then sent; the app start sends what waits. Taken (2xx) or refused as malformed (400/413/422): deleted. Offline,
throttled or down: it waits for the next start. The site keeps reports 90 days without IP or account. Each send result
goes to the account center's journal (`CrashReporter.Activity`, attached by the hosts once the services exist; a send
before that is recorded on attach): Settings → Account → "Crash reports" shows the switch's state, that reports go to
hearthpulse.net, the number waiting, and "Not available on the site yet." while the site answers 404.

## Tests

`CrashReportTests`: no personal data in a report (names in quotes and `key=value`, paths with spaces, under the profile),
the 30 KB cap, nothing without consent, the button sends now without `Authorization`, a fatal crash waits for the next
start (and offline waits again), refused vs throttled, at most five waiting, older than 30 days dropped, switching off
deletes what waits, the reporter built from the settings alone.

The installer notice (`installer/windows/notice.txt`) and the Linux and macOS READMEs have the same CRASH REPORTS section.
