# Hearthpulse Twitch extension

Viewers of a stream see the streamer's deck: the cards left, dimmed when none are left, and the card full size on
hover. Only the remaining deck list is shown - never the hand, the deck order or anything about the opponent.

Data path: Hearthpulse app → `POST https://hearthpulse.net/api/v1/twitch/state` → site (EBS) → Twitch Extension
PubSub (`broadcast`) → these pages. A viewer who opens the stream mid-game gets the last state from
`GET https://hearthpulse.net/api/v1/twitch/ext/snapshot` (viewer JWT from `Twitch.ext.onAuthorized`). Contract:
`reference/tasks/TASK-2026-11-07-twitch-extension/task.md`, "Контракты".

Card art and renders load from HearthstoneJSON (`art.hearthstonejson.com`) at view time; none ship in the zip. No
secrets live here: the extension secret and client secrets are in the site's environment only.

## Views

| File | Twitch slot | What it shows |
|---|---|---|
| `video_overlay.html` | Video - Fullscreen | the deck at the right edge of the video, out of the player's title bar and controls; collapses into a button with the number of cards left (remembered per viewer) |
| `panel.html` | Panel (318 × 496) | the same deck, the list scrolls inside the panel |
| `config.html` | Broadcaster config | how to connect: install Hearthpulse, sign in, link Twitch on hearthpulse.net, turn on Settings → Stream → "Twitch extension" |

Language follows `locale` / `language` from Twitch (Russian for `ru*`, English otherwise); light or dark follows
`theme` from `onContext`. A new state appears `hlsLatencyBroadcaster` seconds after it arrives, about when the
viewer sees that moment of the stream (the OBS delay is applied by the app before sending).

## Commands

```
npm ci
npm test          # vitest: envelope parsing, seq order, latency buffer, cold start
npm run build     # type check + vite build -> dist/
npm run zip       # dist/ -> hearthpulse-twitch-<version>.zip, fails over 1 MB
npm run dev       # dev server on http://localhost:8080
```

The build is not minified on purpose: Twitch review reads the JS, and minified code would need the sources sent
separately. It has no `eval`, no inline scripts and no remote JS besides the Extension Helper, which every page loads
first.

### Dev server with a fake Twitch

Open a page with `?mock` and the dev server stands in for Twitch: a fake `Twitch.ext` (`src/dev/mock.ts`) and a
sample snapshot at `/api/v1/twitch/ext/snapshot` (`vite.config.ts`). Neither is in a build.

```
http://localhost:8080/video_overlay.html?mock&locale=ru-RU&theme=dark
http://localhost:8080/panel.html?mock&locale=en-US&theme=light&latency=1
http://localhost:8080/panel.html?mock&scenario=empty     # snapshot 204, nothing comes: "not in a game"
http://localhost:8080/panel.html?mock&scenario=none      # a broadcast with mode "none"
```

`scenario=deck` (default) sends a broadcast every 4 s with one card fewer. In the console: `__mock.send(state)`,
`__mock.context({ theme: 'light' })`.

## Twitch developer console

Create the extension at https://dev.twitch.tv/console/extensions (name "Hearthpulse", no "Twitch" in it). Then, in the
version's settings:

**Asset Hosting**
- Testing Base URI: `https://localhost:8080/` (Local Test only)
- Video - Fullscreen Viewer Path: `video_overlay.html`
- Panel Viewer Path: `panel.html`, Panel Height: `496`
- Config Path: `config.html`
- Live Config Path: empty
- Mobile: not supported in this version

**Capabilities**
- Type: Video Overlay and Panel
- Bits: off. Request Identity Link: off. Subscription status: off. Chat: off.
- Extension Configuration Service: none (nothing to store per channel)
- Allowlist for Image Domains: `https://art.hearthstonejson.com`
- Allowlist for URL Fetching Domains: `https://hearthpulse.net`
- Allowlist for Media Domains: empty

**Extension settings (site)**
- Client ID, the extension secret (base64) and the owner's Twitch user id go into the site's environment
  (`TWITCH_EXTENSION_CLIENT_ID`, `TWITCH_EXTENSION_SECRET`, `TWITCH_EXTENSION_OWNER_ID`), never into this folder.
  The site answers CORS for `https://<client id>.ext-twitch.tv`.

## Local Test

Twitch loads the pages from the Testing Base URI over HTTPS, so serve a build on `https://localhost:8080`:

1. Build. To point the cold start at another server (a local site), set it at build time and add that host to the
   URL Fetching allowlist of the test version:
   ```
   npm run build
   VITE_API_BASE=https://localhost:3000 npm run build
   ```
2. Serve `dist/` over HTTPS on port 8080 with a locally trusted certificate, for example with
   [mkcert](https://github.com/FiloSottile/mkcert) and any static server:
   ```
   mkcert -install && mkcert localhost
   npx http-server dist -S -C localhost.pem -K localhost-key.pem -p 8080
   ```
   Open `https://localhost:8080/panel.html` once in the browser to accept the certificate.
3. In the console set the version to **Local Test**, install it on your channel (Invite only → your account), and
   activate it as an overlay and a panel on https://dashboard.twitch.tv/extensions.
4. Start Hearthpulse with the extension on in Settings → Stream, go live and start a Constructed game: the deck shows
   on your channel page.

## Hosted Test

1. `npm ci && npm test && npm run build && npm run zip` → `hearthpulse-twitch-<version>.zip` (well under 1 MB).
2. Console → the version → **Files** → Upload Version Assets → the zip (files sit at the zip root).
3. Move the version to **Hosted Test**; Twitch now serves the pages from its CDN, the Testing Base URI is not used.
4. Check on a live channel again: overlay, panel, both themes (Twitch settings → dark theme), Russian and English.

Raise `version` in `package.json` for every new upload: a released version cannot be changed.

Review (outside this MVP) needs a channel that is live with Hearthstone during the check, a walkthrough, privacy
policy and EULA links, a 100×100 icon, a 300×200 discovery image and a 1024×768 screenshot (`research.md`, §2).
