# Sharing: pictures, YouTube timestamps, year in review

Things players, streamers and video makers take out of the app and post elsewhere (`TASK-2026-11-07-growth-app`,
proposals A1, C1, C2, A3 of `TASK-2026-11-07-growth-features`). The OBS window "Session" (B1) is in
[streaming.md](streaming.md). Everything here is free, with no watermark gating; card art comes from the card art cache
like everywhere else in the app.

## What only the player saw

- Every picture and text is built from the player's own history (`history.db`): no opponent name, BattleTag or rating.
- The player's own BattleTag is **off** on every picture until they turn on "Show BattleTag" in the share window
  (`AppSettings.ShareShowBattleTag`, kept for the next picture). The picture then shows the name without `#1234`.
- Nothing is sent anywhere: "Copy image" puts a PNG on the clipboard, "Save" writes a file the player picks.

## The share window

One window in both UIs (`Main/Share/ShareWindow`): the picture as it will be saved, scaled down to fit, the deck
layout switch (deck only), "Show BattleTag", "Copy image", "Save", "Close" and a status line.

- The picture is a control of theme tokens (`Main/Share/*View`), so it follows the theme. It is saved at twice its
  size, as the turn picture (R4, `MatchViewModel.StepExportScaleFor`, at most 4096 px a side), and is opaque: the theme's
  `Bg` lies under it, so a card a fraction of a pixel high leaves no see-through edge for a pasted bitmap.
- Copy: WPF puts the PNG (`"PNG"` format) and a bitmap on the clipboard; Avalonia uses the clipboard's bitmap. When the
  clipboard refuses, the status says so and "Save" still works.

| Picture | Opened from | Size (DIP → PNG) | What it shows |
|---|---|---|---|
| Session recap | Home, "Session recap" on the recent games card | 640 wide → 1280 | the latest session (the ladder session's rule: Hearthstone's start or a break longer than the setting): Ranked per format (games and win rate, wins–losses, rank now with where it started and the change, the deck that won most, the longest win streak), Battlegrounds / Duos (games, average place, top 4 / top 2, rating change, the best hero by average place, the places), Arena (the run's record, finished or not, the rewards typed in on the run) |
| Deck, list | the deck page's list card, "Deck image"; the match page's header (deck icon) | 420 wide → 840 | the class portrait, the name, cards · dust · average cost, the rows as the tracker lists them, the curve, the deck code |
| Deck, 16:9 cover | the same, layout "16:9 cover" | 960 × 540 → 1920 × 1080 | the class art behind the name and facts, the curve, the cards in two columns (a 40-card deck shrinks to fit) |
| Year in review | the year page, "Share as a picture" | 720 wide → 1440 | the year's tiles (below) under the favourite class, Battlegrounds hero and most played card |

The deck picture is built from the deck code (`Core/Share/DeckImage.cs`), so the deck page (the main version) and the
match page (the game's deck) draw the same thing. Dust: 40 / 100 / 400 / 1600 by rarity, Core cards free.

## YouTube timestamps

History's header, "YouTube timestamps" (`Main/Share/TimestampsWindow`, `Core/Share/YouTubeTimestamps.cs`).

- Games: the last session, today or yesterday, with History's mode filter.
- Recording start: by default the first game's start minus the lead-in (30 s, `AppSettings.TimestampLeadInSeconds`,
  0–3600), or a time the player types (`14:05:30`, `14:05`), taken on the day of the range's first game (the nearest
  such moment, so a stream past midnight works). Games before the start are left out.
- Lines: `00:00 Intro` when the first game starts later, then `mm:ss Game N — Paladin vs Shaman (win)`;
  Battlegrounds `Game N — <hero> (place 3)`. Over an hour, every line is `h:mm:ss`. YouTube makes chapters when the
  first line is 00:00, there are at least three and they are 10 s apart; the window says so.

## Year in review ("Hearthpulse 2026 in review")

`Core/Share/YearInReview.cs`, `MainViewModel.YearReview.cs`.

- Shown from December 1 to January 31 (January shows the year before); any day with `HEARTHPULSE_YEAR_REVIEW=1`
  (tests, renders). Home then shows a banner that opens the year page, a window of its own: the sidebar and its
  Ctrl+1…8 shortcuts stay as they are.
- Tiles (each only with data): games (with wins–losses outside Battlegrounds), hours (a game counts at most 3 h),
  favourite class, favourite deck, longest win streak, highest rank, highest Battlegrounds rating (solo), most played own
  card (`cards_played`, read for the year only: `HistoryStore.PlayedCardCounts`), favourite Battlegrounds hero (skins
  count as their hero), Battlegrounds top-4 rate, busiest month.
- The year's games are read off the UI thread when the page opens.

## Chat commands (site side)

`!deck !winrate !rank !mmr` are answered by hearthpulse.net (growth-site). What the app would send for them, and why the
Twitch publisher of `claude/twitch-app` does not carry it yet, is in the task card `TASK-2026-11-07-growth-app/task.md`;
`Core/Share/StreamSession.cs` is the model those fields come from.
