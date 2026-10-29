# Hearthpulse docs

Feature specifications, data contracts and project records. How to build, test and ship lives in the project skills
(`.claude/skills/`, catalogue in [CLAUDE.md](../CLAUDE.md)); these files describe what a feature does and where its
data comes from. Code comments and skills link to them by path, so keep the names stable when editing.

## Features

| Doc | What it covers |
|---|---|
| [arena-draft.md](arena-draft.md) | Arena draft helper on the overlay, card scores, community Arena numbers |
| [counters-constructed.md](counters-constructed.md) | In-game counters for Constructed and Arena |
| [counters-battlegrounds.md](counters-battlegrounds.md) | Battlegrounds counter chips (`bg-counters` panel) |
| [discover-pool.md](discover-pool.md) | Card pool panel for Discover and random-card effects |
| [secrets-helper.md](secrets-helper.md) | Opponent Secrets panel |
| [mulligan-v2.md](mulligan-v2.md) | Mulligan guide: research, what the app computes, proposed endpoints |
| [match-replay.md](match-replay.md) | Match page: turn-by-turn replay and what it may reveal |
| [localization.md](localization.md) | English and Russian interface strings |

## Data sources and accounts

| Doc | What it covers |
|---|---|
| [constructed-meta-api.md](constructed-meta-api.md) | Constructed stats from the Hearthpulse API |
| [bg-meta-sources.md](bg-meta-sources.md) | Where each Battlegrounds number comes from |
| [bg-opponent-mmr.md](bg-opponent-mmr.md) | Battlegrounds opponents' MMR from Blizzard's public leaderboard, its conditions |
| [accounts-api.md](accounts-api.md) | Sign-in with a hearthpulse.net account (OAuth device flow) |
| [profile-upload.md](profile-upload.md) | Finished games sent to the hearthpulse.net profile |

## Project records

| Doc | What it covers |
|---|---|
| [upstream-sync.md](upstream-sync.md) | What was ported from HeartLens upstream and how to repeat the sync |
| [perf-architecture-audit.md](perf-architecture-audit.md) | Performance and architecture audit, stage by stage (in Russian); measured with `tools/Hearthpulse.Bench` |

## Elsewhere in the repository

- [src/Hearthpulse.Presentation/README.md](../src/Hearthpulse.Presentation/README.md) - shared view models map.
- [server/pool/README.md](../server/pool/README.md) - the stats pool Worker (Cloudflare) for shared Battlegrounds games, and its schema.
- [installer/RELEASE_NOTES.md](../installer/RELEASE_NOTES.md) - body of the next GitHub Release.
- `tools/` - generators for checked-in tables and fixtures (`cardpool`, `counters`, `effects`, `secrets`,
  `archetypes`, `mana`, `themes` for the backdrop motifs), `Hearthpulse.Bench` (measurements), `Hearthpulse.Probe` (dev probe of Power.log and
  client memory) and `skill_checks` (tests of the project skills, run by CI).
