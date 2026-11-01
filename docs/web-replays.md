# Web replays (hearthpulse.net/replay/{id})

Plan 2.2. A player puts one finished game on the site by pressing **Upload to the site** on its card in the replay
library (History), and confirming in the card. Nothing is uploaded otherwise. The site part lives in the HeartPulse
repository (`server/modules/replayShares`, page `apps/public-web/app/replay/[id]`).

## What is uploaded

`ReplayUpload.Prepare`: the Share copy (`ReplayImport.Sanitize`, date only: no names, BattleTag, ranks, rating, time of
day) **without the post-game part of a Constructed game** (`N`/`K`: opponent cards the log revealed, which the player may
never have seen on screen). A public page shows the game as the player saw it; the full post-game reveal stays on the
player's own match page. Gzip, base64, `POST {site}/api/v1/tracker/replays` `{"v":1,"hpr":"..."}` with the account's
sign-in (`tracker.write`, `AccountService.UploadReplayAsync`). The answer is `{id, url, deleteToken}`; the link goes to
the clipboard and the status line, and `replays\uploads.json` remembers it by file name (`ReplayUploads`), so the card
shows that the game is on the site and can delete it (`DELETE …/tracker/replays/{id}` with `X-Delete-Token`).

## What the site checks again

- The header is rebuilt from a whitelist of `summary` fields; strings are ids, classes, modes, dates, a deck code — no
  spaces, no free text. The opponent's archetype stays only when it is one of the site's archetype names.
- Every event line must match the replay grammar of its kind (record letter, number and class of each token);
  `N`/`K` lines are dropped; a Battlegrounds `G` line keeps only numbers, booleans and id-like strings, and no key that
  looks like a name.
- Limits: 512 KB gzip, 8 MB text, 100,000 lines, 512 characters a line; 20 uploads an hour per address; 200 replays per
  account; kept 180 days.
- The page shows a card only by its known (Russian) name, never a raw id, and is `noindex`.

## Deleting

In the app (the card's cloud button, by the upload's token), on the account page of the site (its "Реплеи на сайте"
list), and with the account (cascade). The site's privacy policy has a section on replays (what, how long, deleting).
