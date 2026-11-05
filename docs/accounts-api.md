# Hearthpulse accounts — sign-in with a hearthpulse.net account (OAuth device flow)

A Hearthpulse account is the player's account on the HeartPulse site. The apps never ask for a password: they use the
site's application API with the OAuth 2.0 Device Authorization Grant (RFC 8628). The app shows a short code, the
player confirms it on hearthpulse.net/connect in the browser, the app polls until the site answers.
Code: `src/Hearthpulse.Core/Accounts/`.

| File | Role |
|---|---|
| `AccountClient.cs` | HTTP: device code, token polling and refresh, revoke, `/me`; error envelopes |
| `AccountService.cs` | Session for the UI: sign in (poll loop), restore at start, access tokens, sign out |
| `Entitlements.cs` | Paid access from `/me` (`subscription`): providers, sections, `Has(section)`, `Summary` |
| `UsageGate.cs` | Daily free limits of our own analytics: `GET usage/limits`, `POST usage/claim`, soft mode ([usage-limits.md](usage-limits.md)) |
| `TokenStore.cs` | Refresh token, profile and last known paid access on disk (DPAPI on Windows, owner-only file elsewhere) |
| `SystemBrowser.cs` | Opens a URL in the default browser (`UseShellExecute` / `open` / `xdg-open`) |

Site code (reference): `server/modules/applicationAuth/` (`routes.ts`, `model.ts`, `contracts.ts`, `scopes.ts`),
`server/app/registerApplicationAuth.ts`, `server/app/applicationAuthProfile.ts`; spec `docs/specs/tracker-ingestion.md`.

## Contract

| | |
|---|---|
| Base | `https://hearthpulse.net/api/v1/` (`HEARTHPULSE_SITE_API` overrides it for a test site; it must be https, plain http only on this machine, otherwise the default is used) |
| Client | `client_id=manacost-tracker`, public, no secret |
| Scopes | `profile.read subscription.read catalog.read images.read statistics.read tracker.write` |
| Bodies | `application/x-www-form-urlencoded`; answers JSON, `Cache-Control: private, no-store` |
| Lifetimes | device code 10 min, poll interval 5 s, access token 15 min, refresh token 30 days from the first sign-in |

| Method | Path | Body | Success |
|---|---|---|---|
| POST | `oauth/device/code` | `client_id`, `scope` | `device_code`, `user_code` (`ABCD-EFGH`), `verification_uri` (`https://hearthpulse.net/connect/`), `verification_uri_complete` (`…/connect/?user_code=ABCD-EFGH`), `expires_in`, `interval` |
| POST | `oauth/token` | `grant_type=urn:ietf:params:oauth:grant-type:device_code`, `device_code`, `client_id` | `access_token`, `refresh_token`, `token_type=Bearer`, `expires_in`, `scope` |
| POST | `oauth/token` | `grant_type=refresh_token`, `refresh_token`, `client_id` | same; the refresh token is rotated |
| POST | `oauth/revoke` | `token` (the refresh token) | `{}`; ends the whole token family |
| GET | `me` | `Authorization: Bearer <access_token>` (needs `profile.read subscription.read`) | `{"user": {...}, "subscription": {...}}` |
| GET | `usage/limits` | none (public) | `{"enabled", "day", "resetsAt", "features": {key: {"section", "freePerDay"}}}` (plan 3.1) |
| POST | `usage/claim` | JSON `{"feature", "gameId"}`, Bearer (`subscription.read`) | `{"allowed", "unlimited", "used", "limit", "resetsAt"}` (plan 3.1) |

Errors: the OAuth endpoints answer `400 {"error": "<code>"}` (`authorization_pending`, `slow_down`, `access_denied`,
`expired_token`, `invalid_grant`, `invalid_request`, `unsupported_grant_type`), rate limits `429` and outages `503`
answer `{"error": "temporarily_unavailable"}`. `/me` answers `{"error": {"code", "message"}}`: `401
INVALID_ACCESS_TOKEN`, `403 INSUFFICIENT_SCOPE`. Rate limits per IP over 15 minutes: device/code 20, token 240,
revoke 60.

`/me`:

```json
{"user": {"id": "…", "publicProfileId": "…", "profileUrl": "https://hearthpulse.net/profile/", "email": "…",
          "name": "…", "avatarInitials": "ZU"},
 "subscription": {"hasAccess": true, "source": "boosty,patreon", "checkedAt": "2026-10-23T10:00:00.000Z",
                  "stale": false,
                  "entitlements": {"arena": true, "battlegrounds": true, "standard": false, "contests": false,
                                   "guidesArchive": true, "arenaArticles": false, "battlegroundsArticles": false}}}
```

The app keeps `id`, `name` and `profileUrl` (the profile link on the card). `email` is personal data: the app
does not read, store, log or show it. `source` lists the providers that grant access
(`boosty`, `patreon`, `telegram`; `none` without access).

## Sign-in

1. `POST oauth/device/code`. `verification_uri` must be https (plain http only on this machine) and on the same host
   as the base URL.
2. The card shows the code large with a "Copy" button and "Open hearthpulse.net/connect and enter the code"; the app
   opens `verification_uri_complete` (or `verification_uri`) in the browser itself. If the browser does not open, the
   player follows the address on the card ("Open the address above in your browser").
3. Poll `POST oauth/token` every `interval` seconds:
   - `authorization_pending` — keep waiting;
   - `slow_down` — add 5 s to the interval (RFC 8628 3.5; the site adds the same on its side);
   - offline, `5xx`, `temporarily_unavailable` — keep waiting with a backoff on top of the interval (+5 s per failure,
     at most 30 s), reset by the next normal answer; only `slow_down` grows the interval for good;
   - `access_denied`, `expired_token`, `invalid_grant` — stop and explain;
   - the code's `expires_in` passed locally — stop as expired;
   - "Cancel" in the app — stop between polls (the code then expires on the site by itself). A poll already sent is
     not cancelled: the one the site answers with tokens consumes the code, so its tokens are taken even after Cancel.
4. Tokens → saved at once (with a placeholder profile) → `GET me` → profile and paid access saved. `/me` is not
   cancelled either. If it fails, the new refresh token is revoked and the previous session, if any, stays as it was.

## Session

- Access tokens stay in memory and are renewed a minute before `expires_in` runs out.
- Refresh tokens rotate and the site has no grace window: presenting a rotated-out token, or two refreshes at once,
  revokes the whole token family. So token requests run one at a time under a lock, a refresh is never cancelled by
  its caller (only the caller's wait is; the refresh finishes and stores its answer), and the new pair is written to
  disk (temp file + rename, DPAPI) before the access token is used for anything, `/me` included. A failed write is
  logged; the session then lasts until the app closes.
- What the client cannot cover: the site rotated the token but its answer was lost (connection dropped, 40 s client
  timeout, process killed). The next refresh presents the old token and the site ends the sign-in. Only a short
  grace window on the site (accept the predecessor while the successor is unused) would cover it.
- The sign-in lasts 30 days from the token exchange; rotation does not extend it. The app records the end
  (`AccountService.SessionExpires`, saved with the session). The card says "Signed in until <date>"; from two days
  before the end it says "Sign-in ends on <date>. Sign in again to stay connected." with a "Sign in again" button. A
  new sign-in replaces the session and revokes the old refresh token. While the app runs the line is re-checked every
  hour; after the end it reads "Sign-in ended on <date>. Sign in again."
- At start the saved session is refreshed and `/me` re-read (new name, changed subscription). Offline, a server error,
  rate limiting or an unexpected answer keep the saved session with its last known paid access. A refresh the site
  rejects (`invalid_grant`), at start or later, forgets the session. If the refresh works but `/me` is rejected
  (`INVALID_ACCESS_TOKEN`, `INSUFFICIENT_SCOPE`, another account), the new refresh token is revoked and the session
  forgotten.
- `account.dat` carries a format number and the site's origin. A file from an older draft or for another site (the
  variable was switched) is deleted unread, so its token never goes to a service that did not issue it.
- Sign-out forgets the session locally first, then `POST oauth/revoke` with the refresh token (best effort).

## Paid access (`Entitlements`)

`AccountService.Entitlements` (`Entitlements.None` when signed out) is what paywalls in the app read:
`Has(Entitlements.Arena | Battlegrounds | Standard | Contests | GuidesArchive | ArenaArticles |
BattlegroundsArticles)`, `HasAccess`, `Boosty`, `Patreon`, `Stale`, `CheckedAt`. A missing or malformed
`subscription`, `hasAccess` without any `true` section, or a non-boolean value counts as no access (fail closed).
After a start the last known access is loaded with `Stale = true` and its `CheckedAt` until `/me` answers (offline it
stays so); a paywall that must be strict can require `!Stale` and a recent `CheckedAt`.

## Errors shown to the player

| Code | When | Message (account card) |
|---|---|---|
| `CANCELLED` | "Cancel" pressed | Sign-in cancelled. |
| `ACCESS_DENIED` | player declined on the site, or the account is blocked | Sign-in was declined on hearthpulse.net. |
| `EXPIRED_TOKEN` | code not confirmed within 10 minutes | The code expired before it was confirmed… |
| `INVALID_REQUEST`, `INVALID_CLIENT`, `UNSUPPORTED_GRANT_TYPE`, `NOT_AVAILABLE` (404) | client or scope not accepted | Signing in from the app isn't available on hearthpulse.net right now… |
| `INVALID_GRANT`, `INVALID_ACCESS_TOKEN` | session revoked or expired | The sign-in has ended. Sign in again. |
| `INSUFFICIENT_SCOPE` | token without profile/subscription scopes | hearthpulse.net didn't grant the access the app needs… |
| `OFFLINE`, `SERVER_ERROR`, `TEMPORARILY_UNAVAILABLE`, `RATE_LIMITED` | network / site trouble | connection / busy / wait a few minutes |
| `BAD_RESPONSE` | malformed answer, non-https code page | hearthpulse.net sent an answer the app doesn't understand… |

## Account center (Settings → Account)

The account card is the account center (TASK-2026-11-05-account-center), the same tree in both apps
(`Main/SettingsTabs/AccountSettings.xaml` / `.axaml`, shared model `Presentation/Main/MainViewModel.AccountCenter.cs`):

- **Connection**: one state line (`Core/Accounts/AccountConnection.cs`) with "Check now" (`AccountService.CheckNowAsync`:
  `/me` again, and `CheckRequested` makes `AppServices` re-read `usage/limits` past its six-hour pause). States:
  "Connected · checked at 14:05" (the newest success of `/me`, the game upload, replays or limits), "Can't reach the
  site — retrying at 14:20" (the newest of those is an outage: `OFFLINE`, `SERVER_ERROR`/5xx, `TEMPORARILY_UNAVAILABLE`,
  `RATE_LIMITED`, `BAD_RESPONSE`, `NO_TOKEN`; the time is the uploader's next try), "Sign-in expired — sign in again"
  (the site rejected the session, at start or later, or its 30 days ran out), "Not signed in". A `NOT_AVAILABLE` (404:
  the route is not on the site yet) is shown as "Not available on the site yet." in its section, never as an outage.
- **Games in your profile**: the switch; "12 games sent · the last at 14:02", "Waiting to be sent: 3", the pause with
  its reason and next try, refused / stored without statistics (all time). Turning the switch off while games wait
  asks first ("… won't reach your profile": "Turn off" / "Keep it on").
- **Replays on the site**: the uploads this app remembers (`replays/uploads.json`: count, the newest `UploadedAt`) and
  "My replays on the site" → `{profile}#account-replays`. In History an uploaded replay's card has its own "Open on the
  site" (only https on the site's host or a subdomain, `AccountService.OpenSiteLink`); the cloud still deletes, after
  its question.
- **Subscription and limits**: the summary as before, today's "Today you used 2 of 3 free games with the pool's stats;
  resets at 03:00" (`UsageGate` → `AccountActivity.Usage`), "Subscription checked at …" (`Entitlements.CheckedAt`, else
  the last `/me`).
- **Crash reports** (shown signed out too): whether they go by themselves, that they go to hearthpulse.net, how many
  wait in `crash-reports/`.

The journal behind it is `Core/Accounts/AccountActivity.cs`, `account-activity.json` in the data folder: per service
(`Account`, `Games`, `Replays`, `Limits`, `CrashReports`) the last success, the last error code and time and the next
try; the games sent, refused and stored without statistics, kept across restarts; today's use of the limited stats.
Nothing in it goes to the site. Written through a temporary file under a lock; a broken file starts over; it never
throws. `AppServices` builds it and hands it to `AccountService` (`Activity`), the uploader and the usage gate take it
from there, and the hosts attach it to the crash reporter, which is built before them. The restore at start keeps its
error (`AccountService.LastCheckError`) instead of dropping it.

The main window's top bar has the indicator from the same state (WPF `AccountLinkButton` in `MainWindow.xaml`,
Avalonia in `MainView.axaml`): a pill with a dot (`Positive` connected, `InfoCaution` offline, `Negative` sign-in
expired) and one word, the state line as tooltip and accessible name; a click opens Settings → Account. Signed out it
is hidden (nothing to report for a player who does not use the account); after an ended sign-in it stays, red, until
the player signs in again or signs out.

## Checking by hand

1. Run the app with a scratch data folder (`HEARTHPULSE_DATA`); `HEARTHPULSE_SITE_API` for a test site.
2. Settings → "Hearthpulse account" → "Sign in with HeartPulse". The card shows the code and
   hearthpulse.net/connect; the browser opens the page with the code filled in.
3. "Copy" puts the code on the clipboard ("Code copied.").
4. Sign in on the site if needed and approve "Manacost Tracker". Within ~5 s the card shows the name and the
   subscription line ("Boosty subscription: …" or "No paid subscription").
5. Restart the app: the card shows the same without the browser (`hearthpulse.log` has no "saved session rejected").
6. "Open profile on the site" opens the account page.
7. "Sign out": the card returns to the button; a refresh with the old token fails on the site.
8. Also try "Cancel" while waiting, declining on the site (→ "declined") and waiting more than 10 minutes (→ expired).
