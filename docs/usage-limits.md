# Daily free limits of our own analytics (plan 3.1)

Part of the app's analytics is free for a few games a day and unlimited with a subscription, as in Firestone. Only our
own analytics can be limited: numbers from the Hearthpulse stats pool (games our players share). Third-party data
(HSReplay, HSGuru, ArenaSmith: the mulligan guide, hero stats, Discover picks, the Arena draft) and Blizzard's cards
and art stay free: selling them would resell someone else's data (legal review `LEGAL-HeartLens-card-choices.md`,
L-M3). Site side: HeartPulse `docs/specs/app-usage-limits.md`.

## What is limited

`UsageGate.BgPoolStats` (`bg.poolStats`, section `battlegrounds`): the stats pool's comps and trinket stats in the
Battlegrounds overlay. `BattlegroundsAdvisor.Enrich` uses them only in a game the gate allows; otherwise it ranks as
without a pool (no comps: no top comps on the hero pick, no comp bonus in the tavern and Discover hints; trinkets
without stats) and `BattlegroundsView.UsageNote` says why. The hero pick and trinket rows show that line under the
badges (both UIs, `OverlayViewModel.UsageNote`). A pool with nothing to show spends no free game.

## Who decides

`Core/Accounts/UsageGate.cs`, one per app (hosts set `Session.Advisor.Usage`; the session marks each game by its start
time in memory, `Advisor.Game`). A game counts once, the first time the pool has numbers for it:

1. Until the site has said limits are on (`GET usage/limits`, read with the meta every 6 hours and kept in
   `usage.json`), nothing is limited. The site has them off by default.
2. A subscription whose sections include the feature's (`Entitlements.Has`) is never limited and never counted.
3. Signed in: `POST usage/claim` `{feature, gameId}` with a random per-game id; the site counts and decides.
4. Soft mode: offline, no answer within 5 s, or signed out — the game is counted here, in `usage.json` (UTC day, random
   ids), against the last limit the site gave. It can be got around; that is accepted.

While the site's answer is on its way (signed in) the pool's numbers wait; the answer makes the session publish again.
When today's games are used up: «Бесплатных игр в день со статистикой пула Hearthpulse: 3, на сегодня они закончились.
Без ограничения — с подпиской на разделы Полей сражений (Boosty, Patreon, Telegram). Обновится в 03:00.» (string `Usage.Limit.Reached`,
`Localization/Strings/{en,ru}/usage.json`).

## Tests

`UsageGateTests` (limits off, signed out with the count kept on disk and the next day, the site's decision, a
subscriber, offline), `PickSettingsTests.The_pool_s_comps_wait_for_today_s_free_games`.
