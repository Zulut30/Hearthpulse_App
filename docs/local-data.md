# Local data: broken and busy files

What both apps do when a file in the data folder (`AppPaths`, `HEARTHPULSE_DATA`) cannot be read, and what they do not
do yet.

| File | Cannot be read | Code |
|---|---|---|
| `settings.json` | Held by another program (an antivirus scan, a cloud sync): read again for up to a second. Broken JSON: every readable setting is kept, the rest is the default, the original is copied to `settings.json.invalid` first (saving is paused while that copy fails). | `AppSettings.Load`, `ReadWithRetry`, `Salvage`, `PreserveUnreadable` |
| `history.db` | Not a database (SQLite 26, or 11 "corrupt" on open): put aside as `history.db.broken-<UTC time>` with its journals, and a new history starts. | `HistoryStore` constructor |
| `cache/api/*.json` | Younger than 6 hours but not JSON: deleted and asked for again once; when offline, an older readable answer stands in. | `HearthpulseData.FetchAsync` |

## Known limits

- **Settings held longer.** The read is retried for one second and only for `IOException`. A file held longer (a cloud
  sync of a large folder) or a lock Windows reports as `UnauthorizedAccessException` still gives defaults that a later
  save writes over the file (the original is kept in `settings.json.invalid`). The full fix: after a read error that is
  not broken JSON, do not save over the file in that session. The retry waits on the UI thread, twice in WPF at start.
- **History put aside without a word.** The player sees an empty history and nothing says why (only a `Diag` line).
  A database SQLite calls corrupt on open is put aside whole even when part of it could still be read. Showing it needs a
  property on `HistoryStore`, a text in both languages and a notice raised after the hosts' start, in both apps.
- **Replaced log watcher.** A watcher replaced by the watchdog checks that it was stopped before it calls
  `OnSessionStarted` or `OnResync`. That narrows the race with the new watcher but does not close it (check, then act);
  closing it needs a generation shared with the consumer or one lock with `Stop`.
