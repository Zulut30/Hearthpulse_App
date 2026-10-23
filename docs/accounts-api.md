# Hearthpulse accounts — server contract

The apps (`src/Hearthpulse.Core/Accounts/AccountService.cs`) talk to these endpoints on the Hearthpulse API,
`https://api.kolodahearthstone.com/` (override with the `HEARTHPULSE_API` environment variable for a test server).
Until the server implements them, sign-in answers 404 and the apps show "Hearthpulse accounts are not open yet".

All bodies are JSON, snake_case. Successful answers are wrapped in `{"data": ...}` like the rest of the API.
Errors use the API's existing shape: `{"detail": {"code": "<CODE>", "message": "<text>"}}`.

## Endpoints

| Method | Path | Body | Success |
|---|---|---|---|
| POST | `/v1/accounts/register` | `{"email", "password", "display_name"}` | `201`/`200` + Session |
| POST | `/v1/accounts/login` | `{"email", "password"}` | `200` + Session |
| POST | `/v1/accounts/refresh` | `{"refresh_token"}` | `200` + Session (rotated refresh token) |
| POST | `/v1/accounts/logout` | `{"refresh_token"}` | `204` (revokes that refresh token) |
| GET | `/v1/accounts/me` | — (`Authorization: Bearer <access_token>`) | `200` + `{"data": User}` |

**Session**

```json
{
  "data": {
    "access_token": "<JWT or opaque>",
    "refresh_token": "<opaque, long>",
    "token_type": "bearer",
    "expires_in": 900,
    "user": { "id": "u_123", "email": "a@b.c", "display_name": "Anna", "created_at": "2026-10-23T12:00:00Z" }
  }
}
```

`expires_in` is the access token's lifetime in seconds (the app renews one minute before). `id` may be a string or a number.

## Error codes the app understands

| Status | `code` | When |
|---|---|---|
| 401 | `INVALID_CREDENTIALS` | wrong email or password |
| 409 | `EMAIL_TAKEN` | register with an existing email |
| 422 | `WEAK_PASSWORD` | password shorter than 8 characters (FastAPI validation lists are also accepted) |
| 401 | `INVALID_TOKEN` / `TOKEN_EXPIRED` | bad or revoked refresh/access token — the app forgets the saved session |
| 429 | `RATE_LIMITED` | too many attempts |
| 5xx | any | server trouble — the app keeps the saved session and retries on the next start |

## Server requirements

- Hash passwords with Argon2id or bcrypt; never log them. Normalise email to lower case.
- Refresh tokens: random ≥ 32 bytes, stored hashed, rotated on every `/refresh`, revoked on `/logout`;
  reusing a rotated token should revoke the whole chain.
- Rate-limit `/login` and `/register` per IP and per email.
- The existing admin API tokens (`/admin/api-tokens`) stay separate from player accounts.

## Client side (already implemented)

- The refresh token and the profile are kept in `<data folder>/account.dat`: DPAPI-encrypted for the Windows user,
  owner-only file permissions on macOS/Linux. The password is never stored; the access token lives in memory only.
- Without network the saved profile stays shown; a session the server rejects (401) is forgotten.
- UI: Settings → "Hearthpulse account" in both apps (WPF and Avalonia).
