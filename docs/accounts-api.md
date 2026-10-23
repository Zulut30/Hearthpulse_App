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
| `TokenStore.cs` | Refresh token, profile and last known paid access on disk (DPAPI on Windows, owner-only file elsewhere) |
| `SystemBrowser.cs` | Opens a URL in the default browser (`UseShellExecute` / `open` / `xdg-open`) |

Site code (reference): `server/modules/applicationAuth/` (`routes.ts`, `model.ts`, `contracts.ts`, `scopes.ts`),
`server/app/registerApplicationAuth.ts`, `server/app/applicationAuthProfile.ts`; spec `docs/specs/tracker-ingestion.md`.

## Contract

| | |
|---|---|
| Base | `https://hearthpulse.net/api/v1/` (`HEARTHPULSE_SITE_API` overrides it for a test site) |
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

1. `POST oauth/device/code`. `verification_uri` must be https (plain http only on this machine).
2. The card shows the code large with a "Copy" button and "Open hearthpulse.net/connect and enter the code"; the app
   opens `verification_uri_complete` (or `verification_uri`) in the browser itself. If the browser does not open, the
   player follows the address on the card.
3. Poll `POST oauth/token` every `interval` seconds:
   - `authorization_pending` — keep waiting;
   - `slow_down` — add 5 s to the interval (RFC 8628 3.5; the site adds the same on its side);
   - offline, `5xx`, `temporarily_unavailable` — keep waiting with 5 s more, until the code expires;
   - `access_denied`, `expired_token`, `invalid_grant` — stop and explain;
   - the code's `expires_in` passed locally — stop as expired;
   - "Cancel" in the app — stop (the code then expires on the site by itself).
4. Tokens → `GET me` → profile and paid access → the refresh token, profile and access are saved.

## Session

- Access tokens stay in memory and are renewed a minute before `expires_in` runs out.
- Refresh tokens rotate and the site has no grace window: presenting a rotated-out token, or two refreshes at once,
  revokes the whole token family. So token requests run one at a time under a lock, and the new pair is written to
  disk (temp file + rename, DPAPI) before the access token is used for anything, `/me` included.
- The sign-in lasts 30 days from the token exchange; rotation does not extend it. The app records the end
  (`AccountService.SessionExpires`, saved with the session). The card says "Signed in until <date>"; from two days
  before the end it says "Sign-in ends on <date>. Sign in again to stay connected." with a "Sign in again" button. A
  new sign-in replaces the session and revokes the old refresh token.
- At start the saved session is refreshed and `/me` re-read (new name, changed subscription). Offline, a server error,
  rate limiting or an unexpected answer keep the saved session with its last known paid access; `invalid_grant`
  forgets it.
- Sign-out forgets the session locally first, then `POST oauth/revoke` with the refresh token (best effort).

## Paid access (`Entitlements`)

`AccountService.Entitlements` (`Entitlements.None` when signed out) is what paywalls in the app read:
`Has(Entitlements.Arena | Battlegrounds | Standard | Contests | GuidesArchive | ArenaArticles |
BattlegroundsArticles)`, `HasAccess`, `Boosty`, `Patreon`, `Stale`, `CheckedAt`. A missing or malformed
`subscription`, `hasAccess` without any `true` section, or a non-boolean value counts as no access (fail closed).
After an offline start the last known access is shown with its `CheckedAt`; a paywall that must be strict can
require `!Stale` and a recent `CheckedAt`.

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
