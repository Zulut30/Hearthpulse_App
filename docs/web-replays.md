# Web replays (hearthpulse.net/replay/{id})

Plan 2.2. A player puts one finished game on the site by pressing **Upload to the site** on its card in the replay
library (History), and confirming in the card. Nothing is uploaded otherwise. Only the player's own games have the
button (Recent, Saved): an imported replay is someone else's game. The site part lives in the HeartPulse repository
(`server/modules/replayShares`, page `apps/public-web/app/replay/[id]`, spec `docs/specs/web-replays.md`).

## What is uploaded

`ReplayUpload.Prepare`: the Share copy (`ReplayImport.Sanitize`, date only: no names, BattleTag, ranks, rating, time of
day), **a Duos game's partner minions (`P`) dropped and the file sent as `HPR4`** until the site reads format 5,
**without the post-game part of a Constructed game** (`N`/`K`: opponent cards the log revealed, which the player may
never have seen on screen). A public page shows the game as the player saw it; the full post-game reveal stays on the
player's own match page. Gzip, base64, `POST {site}/api/v1/tracker/replays` `{"v":1,"hpr":"..."}` with the account's
sign-in (`tracker.write`, `AccountService.UploadReplayAsync`). The answer is `{id, url, deleteToken}`; the link goes to
the clipboard and the status line.

Plan 2.3: when the card is a History game the profile upload sends (the app's own record of a finished Ranked, Casual,
Battlegrounds, Duos or Arena game; not one brought over from HDT or Firestone, not a left game, not Brawl or Practice)
and games go to the profile (`AppSettings.UploadGamesToProfile`), the body also carries `gameId`, the game's profile
event id (`ProfileEvents.ProfileGameId`: start second and game type, no BattleTag), so the account's «Последние игры»
on the site links the game to its replay. Otherwise it is not sent: the id is derived from the start time, which the
site then does not have. The site never publishes it.

`replays\uploads.json` (`ReplayUploads`) remembers each upload by `ReplayUploads.KeyOf`: Recent and Saved share the key
(`games/<file>`, Saved is the starred copy of the same game). It is read once per library tab, off the UI thread, and
written through a temporary file, so a broken write never loses the delete tokens.

While an upload runs the card's button shows a cross and cancels it ("Uploading… click to cancel"); a second upload
cannot start. A cancelled upload the site had already taken stays on the site: it is in the account page's list.

## Deleting

The card's cloud button deletes by the upload's token (`DELETE …/tracker/replays/{id}`, `X-Delete-Token`,
`AccountService.DeleteReplayAsync`): deleted; already gone on the site (404, deleted on the account page or expired:
forgotten in the app too); or still there (offline, rate limited, server error: the link and token stay, the status says
which). On the site: the account page's "Реплеи на сайте" list, and with the account (cascade). The account's data
export includes the replays.

## What the site checks again

It does not trust the app and repeats its rules: the app's event grammar token for token, the opponent's hand and
secrets as card backs without their tag changes, Battlegrounds cards out of view dropped, the header rebuilt field by
field from fixed lists (mode, result, format, game type, classes), the date without the time, the opponent's archetype
only when the site knows it, the `G` line from the app's keys only. Limits: 512 KB gzip, 8 MB text, 100,000 lines;
20 uploads an hour per address; 200 replays per account; kept 180 days (pruned daily). The page shows a card only by its
known (Russian) name, never a raw id, and is `noindex`. Details: the site's `docs/specs/web-replays.md`.
