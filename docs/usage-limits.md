# Daily free limits of our own analytics (plan 3.1)

Part of the app's analytics is free for a few games a day and unlimited with a subscription, as in Firestone. Only our
own analytics can be limited: numbers from the Hearthpulse stats pool (games our players share). Third-party data
(HSReplay, HSGuru, ArenaSmith: the mulligan guide, hero stats, Discover picks, the Arena draft) and Blizzard's cards
and art stay free: selling them would resell someone else's data (legal review `LEGAL-HeartLens-card-choices.md`,
L-M3). Site side: HeartPulse `docs/specs/app-usage-limits.md`.

## What is limited

`UsageGate.BgPoolStats` (`bg.poolStats`, section `battlegrounds`): the stats pool's comps and trinket stats **as in-game
hints on the Battlegrounds overlay**. `BattlegroundsAdvisor.Enrich` uses them only in a game the gate allows; otherwise
it ranks as without a pool (no comps: no top comps on the hero pick, no comp bonus in the tavern and Discover hints;
trinkets without stats). A pool with nothing to show spends no free game.

The main window stays free (coordinator's decision, review m4): the BG meta page, its comps and trinkets are a
reference anyone can read, as the pool's totals are public on the site; only the advice during a game is limited.

## What the player sees

`BattlegroundsView.UsageNote` → `OverlayViewModel.UsageNote`, a muted line with a lock under the hero pick, trinket and
tavern rows (both UIs):

- while free games remain: «Статистика пула Hearthpulse — бесплатных игр сегодня: осталось 2 из 3.» (`Usage.Limit.Left`);
- once they are used up: «… бесплатные игры на сегодня закончились (в день: 3). Без ограничения — с подпиской на
  разделы Полей сражений: Настройки → аккаунт Hearthpulse → «Подписка и лимиты». Обновится в 3:00.»
  (`Usage.Limit.Reached`; the reset is the UTC midnight in the player's clock);
- nothing for a subscriber, with limits off, or while the site's answer is on its way.

No clicks on the overlay (they would go to the game) and no pop-ups. The account card in Settings has «Подписка и
лимиты» (`OpenAccountSubscription` → `{profile}#account-access`). Strings: `Localization/Strings/{en,ru}/usage.json`.

## Who decides

`Core/Accounts/UsageGate.cs`, one per app (hosts set `Session.Advisor.Usage` and call `GameFinished` on the session's
`GameFinished`; the session marks each game by its start, `Advisor.Game`):

1. Until the site has said limits are on (`GET usage/limits`, asked on its own next to the BG meta every 6 hours and
   kept in `usage.json`), nothing is limited. The site has them off by default.
2. A subscription whose sections include the feature's (`Entitlements.Has`) is never limited and never counted.
3. Signed in: `POST usage/claim` `{feature, gameId}`; the site counts and decides. While it answers the pool's numbers
   wait; the answer makes the session publish again.
4. Soft mode: offline, no answer within 5 s, any other failure, or signed out — the game is counted here, in
   `usage.json`, against the last limit the site gave. It can be got around; that is accepted.

**Game ids.** `HMAC-SHA256(salt, start)` as a UUID; the salt is made once and kept in `usage.json`, never sent. A
restart mid-game reads the same start from the log, so it asks about the same game and today's decision (kept in
`usage.json` by id) answers without a new claim. A reconnect starts a new game in the log: a new start within 2 hours
of an allowed game that has not finished keeps that game's decision (a game that never finished can so carry over to
the next one — soft, in the player's favour).

**Expiry and offline.** Paid access is read at start and with every refresh of the account (`/me`); a subscription
that ends mid-session stays without a limit until the next refresh or restart, and offline the last known access
counts (`Entitlements` from disk). This errs in the player's favour on purpose.

## Tests

`UsageGateTests` (limits off; signed out with what is left, then why, kept on disk and reset the next UTC day; the
site's decision while the numbers wait; a restart in the same game; a reconnect; a subscriber; offline, timeout and a
non-200 answer counted here; the reset time in ru-RU), `PickSettingsTests.The_pool_s_comps_wait_for_today_s_free_games`,
`ViewModelContractTests.Overlay_usage_note_is_the_same_line_in_both_hosts`.
