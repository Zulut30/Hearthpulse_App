# Game records live in the stats pool (Cloudflare D1), not in the site's database

Every finished game (all modes: Ranked, Casual, Arena, Brawl, Battlegrounds solo and duos) goes
as one compact versioned record to the stats pool Worker, `POST /v1/events`, and is stored as one row of D1 table `e`
(server/pool/src/events.js, [game-stats.md](../game-stats.md)). The site's SQLite (hearthpulse.net) does not hold them.

## Why

- **Volume.** A record is 0.4-1.6 KB on the kept logs (a long Constructed game up to about 4 KB; the server caps one at
  24 KB). With hundreds of players sending 10-20 games a day that is a few thousand records and a few MB a day; at
  180 days of raw records, under 2 GB. D1's free plan holds 5 GB and 100,000 rows written a day; a record costs about
  six (row, day index, install index, and the same again when the clean-up deletes it), so the pool caps records at
  8,000 a day (`EVENTS_DAILY`) beside the 15,000 Battlegrounds games. Past that, Workers Paid ($5 a month: 10 GB a
  database, 50 million rows written a month) or moving raw records to R2 is the next step; the record format does not
  change. The site's SQLite would take the same writes, but it also carries accounts, sessions and the profile, its
  backups are replicated off the server, and bulk telemetry would grow every backup and compete with the site's own
  writes.
- **No account in the path.** The site is where players sign in; the pool has never seen an account, a cookie or a
  token. Records sent there cannot be joined to a profile by accident: they carry only a random install id (a keyed
  hash of the app's pool secret), needed so a player can have their records deleted.
- **No logs.** The Worker runs with Workers logs off (`[observability] enabled = false`), so no IP address or body is
  kept; the site's nginx needs a per-route `access_log off` on the origin and on every edge for the same guarantee.
- **One client.** The app already sends Battlegrounds games to the pool on the player's consent, with
  a keyed game id, a rate limit and a daily budget; game records reuse the endpoint setting, the secret and the queue
  rules.

## Consequences

- **Records are pseudonymous personal data** (GDPR recital 26), not anonymous: the install id links one install's
  games for 180 days. So: one install's records are never published, sold, shown in a paid feature or joined to a
  site account; only totals with at least 10 games from at least 5 installs leave the pool (free or paid). The rating
  is capped at 10,000 ("10000+": the few players above are on Blizzard's public leaderboard), the rank is the league
  only. Deleted records stay in D1's Time Travel history for up to 30 days; the pool is never restored to a point
  before a deletion without sending the deletion again (`reference/reviews/LEGAL-game-stats.md`).
- **On by default, with an opt-out** (the owner's decision, 2026-11-06). No consent prompt; the legal minimum for
  pseudonymous data is kept: one opt-out switch in Settings → Data, the installer notice, section 9 of the site's
  privacy policy, and deletion of the install's records when the switch goes off. The stats pool's first-start
  question (public Battlegrounds totals) is a separate purpose and does not change game records.

- Records are kept 180 days (the cron job deletes older ones); only a small aggregate is published
  (`/v1/events/summary`: games per day, most-picked Discover options and mulligan keep rate per card, each from 10
  games and 5 installs or more). Anything built on them later is Hearthpulse's own analytics and may be paid (ADR 0009).
- Deleting: switching the setting off drops the queue and sends `POST /v1/events/forget` with the install id until the
  pool confirms it.
- Deploying needs the owner's Cloudflare login (`wrangler login`); until the pool is deployed the app keeps the switch
  hidden (`PoolClient.Available`).
- Hidden information (ADR 0005): records are built from the replay as the player saw it, without its post-game part.

## Legal basis: legitimate interest, balancing test (LEGAL-game-stats-default-on)

| Question | Answer |
|---|---|
| Interest | Hearthpulse's own statistics of the game (picks, mulligans, decks, Battlegrounds), the basis of the app's hints and of paid analytics (ADR 0009) |
| Necessary? | Yes: the statistics are built from many players' games; there is no less intrusive source of our own data |
| Data | Gameplay only: no name, BattleTag, account, deck name, legend number or time of day; rating capped at 10,000; opponent cards only once shown |
| Linkability | Pseudonymous: a random install id, never shown and never joined to a site account; one install's records are never published, sold or shown in a paid feature |
| Expectation | Players of a tracker expect it to read their games; told in the installer, in a one-time notice in the main window (nothing is sent before it), in Settings and in section 9 of the site's policy |
| Safeguards | Opt-out at any time, which deletes the install's records on the server; 180 days; totals only from 10 games and 5 installs; Workers logs off |
| Children | Hearthstone's own age rules apply; no data that identifies a player is kept |

**Accepted risk (owner, 2026-11-06).** EDPB Guidelines 2/2023 read ePrivacy Art. 5(3) as needing consent for information sent
from a device for a purpose the user did not ask for. The owner chose default-on with an opt-out; the risk is
accepted and narrowed: nothing is sent before the one-time notice is shown, the install secret is made only once
sending is on, and turning off deletes. Russian 152-FZ (data of Russian citizens in a foreign database, RKN
notifications) is an open item for the owner (`reference/reviews/LEGAL-game-stats-default-on.md`, L-M5).
