# Crash reports (plan 0.5)

When the app hits an unexpected error, the player may send a report to hearthpulse.net. Off by default; nothing is
written or sent without consent. Site side: HeartPulse `docs/specs/crash-reports.md` (`POST /api/v1/crash-reports`).

## Consent

- **"Send report"** in the error window (WPF `Infrastructure/CrashDialog.cs`, Avalonia `Desktop/CrashWindow.cs`): this one
  report, now; when the app has to close, it is queued and goes at the next start ("The report goes at the next start").
- **Settings → Data → "Send crash reports automatically"** (`AppSettings.SendCrashReports`, off): every new kind of error
  (the first time `CrashLog.Record` sees it) and every crash that ends the process; the window then says the report went.
- The switch's note and the installer notice (`installer/windows/notice.txt`, "CRASH REPORTS") say what is sent.

## What a report holds

`Core/Diagnostics/CrashReport.cs`: the app's version, the UI (`wpf` / `avalonia`), the OS and its architecture, the .NET
version, the interface language, the handler that caught it ("UI error", "Fatal error", "Unhandled error", "Background
error"), how long the app ran, the time (UTC), a random report id, and up to five exceptions of the chain: the type, the
message and the stack. `CrashReport.Scrub` cuts out e-mails, BattleTags, the user profile path and the OS user name, any
other absolute path, IP addresses and numbers of 7 digits and more; stack lines lose their " in file:line" part; texts are
cut at 300 characters. No account, token, history, settings, game logs or memory values.

## Sending

`Core/Diagnostics/CrashReporter.cs`, one per app (hosts: WPF `App.xaml.cs`, Avalonia `AppHost.CrashReporter`, used by the
global handlers). A report is written to `crash-reports/` in the data folder first (at most 5 wait; older than 30 days are
dropped), then sent; the app start sends what waits. Taken (2xx) or refused as malformed (400/413/422): deleted. Offline,
throttled or down: it waits for the next start. The site keeps reports 90 days without IP or account.

## Tests

`CrashReportTests`: no personal data in a report, nothing without consent, the button sends now, a fatal crash waits for
the next start (and offline waits again), refused vs throttled, at most five waiting.
