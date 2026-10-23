# Hearthpulse accounts — sign-in through hearthpulse.net (OpenID Connect)

A Hearthpulse account is the player's account on the HeartPulse site. The apps never ask for a password: they open
the system browser on the site's OpenID Connect provider and receive the result on a loopback redirect
(RFC 8252, "OAuth 2.0 for Native Apps"). Code: `src/Hearthpulse.Core/Accounts/`.

| File | Role |
|---|---|
| `OidcClient.cs` | Discovery (cached 12 h), authorization URL, token exchange/refresh, userinfo, revocation, id_token check |
| `BrowserSignIn.cs` | One browser sign-in: loopback listener, browser, `state` check, code exchange, profile; `SystemBrowser` |
| `LoopbackListener.cs` | `TcpListener` on `127.0.0.1:{free port}`, answers one `GET /callback` |
| `CallbackPage.cs` | The self-contained page the browser shows afterwards ("Можно вернуться в приложение") |
| `Pkce.cs` | Code verifier, S256 challenge, `state`/`nonce` |
| `AccountService.cs` | Session for the UI: sign in, restore at start, access tokens, sign out |
| `TokenStore.cs` | Refresh token + profile on disk (DPAPI on Windows, owner-only file elsewhere) |

## Provider contract

| | |
|---|---|
| Issuer | `https://hearthpulse.net/identity` (`HEARTHPULSE_ISSUER` overrides it for a test site) |
| Discovery | `{issuer}/.well-known/openid-configuration`; `issuer` in the document must equal the issuer exactly |
| Client | `client_id=hearthpulse-desktop`, public native client, no secret (`token_endpoint_auth_method=none`) |
| Grant | authorization code + PKCE `S256`; `refresh_token` |
| Redirect | `http://127.0.0.1:{port}/callback`, port chosen per sign-in (RFC 8252 7.3: the provider must ignore the port) |
| Scopes | `openid profile offline_access`, sent with `prompt=consent` (OIDC Core 11) |
| Profile | `sub` (account id) and `name` (display name) |
| id_token | signed `RS256` or `ES256`, keys from `jwks_uri` |

Endpoints used from discovery: `authorization_endpoint`, `token_endpoint`, `jwks_uri`, `userinfo_endpoint`,
`revocation_endpoint` (optional: without it sign-out is local only). Endpoints must be `https` (plain `http` only
for an issuer on this machine).

## Sign-in

1. Discovery (from cache when fresh).
2. Fresh `code_verifier` (32 random bytes, base64url), `code_challenge = base64url(SHA-256(verifier))`, random
   `state` and `nonce`.
3. Listen on `127.0.0.1`, port 0 (the OS picks a free one). Only the loopback interface, so nothing else on the
   network can answer the redirect.
4. Open the system browser (`UseShellExecute` on Windows, `open` on macOS, `xdg-open` on Linux) at

   ```
   {authorization_endpoint}?response_type=code&client_id=hearthpulse-desktop
     &redirect_uri=http%3A%2F%2F127.0.0.1%3A{port}%2Fcallback&scope=openid%20profile%20offline_access
     &state=…&nonce=…&code_challenge=…&code_challenge_method=S256&prompt=consent
   ```

5. The player signs in on the site (if not already) and confirms. The provider redirects to
   `http://127.0.0.1:{port}/callback?code=…&state=…[&iss=…]` or `?error=access_denied&state=…`.
   Requests to other paths get 404 and the wait goes on. The wait ends after 5 minutes or when the player presses
   "Cancel" in the app.
6. Checks, in this order: `state` equals ours (otherwise the redirect is ignored whatever it says); `iss`, if present
   (RFC 9207), equals the issuer; then `error`; then `code`.
7. `POST {token_endpoint}` (form): `grant_type=authorization_code&code&redirect_uri&code_verifier&client_id`.
8. id_token: signature by the JWKS key named by `kid` (the JWKS is reloaded once for an unknown key, to follow key
   rotation; `none` and HMAC are refused), `iss`, `aud` contains `hearthpulse-desktop` (`azp` too when there are
   several audiences), `exp`/`iat` with 2 minutes of clock skew, `nonce` equals ours.
9. `GET {userinfo_endpoint}` with the access token for `name` (in the code flow the provider keeps profile claims out
   of the id_token); its `sub` must equal the id_token's.
10. Only now the browser tab is answered: "Вход выполнен — можно вернуться в приложение" or "Войти не получилось".
    The refresh token and profile are saved.

## Session

- Access tokens stay in memory and are renewed 30 s before `expires_in` runs out.
- `POST {token_endpoint}`: `grant_type=refresh_token&refresh_token&client_id`. Refresh tokens rotate: the new one in
  the answer replaces the stored one immediately. Token requests are serialised, because presenting a rotated-out
  refresh token makes the provider revoke the whole grant.
- At start the saved session is refreshed and the profile re-read (a renamed profile shows up). Offline, a server
  error, rate limiting or an unexpected answer keep the saved session; an OAuth rejection (`invalid_grant`...)
  forgets it.
- Sign-out forgets the session locally first, then `POST {revocation_endpoint}`
  `token={refresh_token}&token_type_hint=refresh_token&client_id` (best effort). With `revokeGrantPolicy` the
  provider ends the whole grant.

## Errors shown to the player

| Code | When | Message (account card) |
|---|---|---|
| `CANCELLED` | "Cancel" pressed | Sign-in cancelled. |
| `TIMEOUT` | no redirect within 5 minutes | Sign-in timed out… |
| `ACCESS_DENIED` | player pressed "Отмена" on the site | Sign-in was declined in the browser. |
| `STATE_MISMATCH`, `ISSUER_MISMATCH` | redirect for another attempt / provider | The browser answered for a different sign-in attempt… |
| `INVALID_CLIENT`, `UNAUTHORIZED_CLIENT`, `INVALID_SCOPE`, `INVALID_REQUEST`, `NOT_AVAILABLE` (404) | client not registered on the site yet | Signing in from the app isn't open on hearthpulse.net yet… |
| `INVALID_GRANT` | refresh token revoked/expired | Your session has ended. Sign in again. |
| `OFFLINE`, `SERVER_ERROR`, `RATE_LIMITED` | network / site trouble | connection / try later / wait a minute |
| `BAD_RESPONSE`, `BAD_TOKEN` | malformed answer, bad id_token | hearthpulse.net sent an answer the app can't trust… |
| `BROWSER_FAILED` | no browser could be started | Couldn't open your browser… |

## What the provider has to allow for this client

The site registers `hearthpulse-desktop` as a native public client: `application_type=native`,
`token_endpoint_auth_method=none`, `redirect_uris=["http://127.0.0.1/callback"]` (any port, RFC 8252 7.3),
`grant_types=["authorization_code","refresh_token"]`, `response_types=["code"]`, and lets it be granted
`offline_access`. The consent page's form must be allowed to post to and redirect to `http://127.0.0.1:*`
(`form-action` in its CSP).

## Checking by hand

1. Run the app with a scratch data folder (`HEARTHPULSE_DATA`), optionally `HEARTHPULSE_ISSUER` for a test site.
2. Settings → "Hearthpulse account" → "Sign in with HeartPulse". The browser opens the site; the card says
   "Waiting for you to sign in in the browser…".
3. Sign in on the site and confirm. The tab says "Вход выполнен"; the card shows the profile name.
4. Restart the app: the card shows the same name without a browser (the log `hearthpulse.log` has no
   "saved session rejected").
5. "Open profile on the site" opens `https://hearthpulse.net/profile`.
6. "Sign out": the card returns to the button; on the site the app's grant is gone.
7. Also try "Cancel" while waiting, "Отмена" on the consent page (→ "declined") and waiting more than 5 minutes.
