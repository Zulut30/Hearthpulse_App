-- Analytics facts from source worker results v2 and published snapshots (TASK-2026-11-09 F2, docs/analytics-api.md).
-- Additive and idempotent: no change to legacy or 0008 tables. Facts are replaced with the derived result in one
-- transaction; forget, retention and reprocess failure delete them explicitly (no cascade is relied on).
CREATE TABLE IF NOT EXISTS source_fact_game (
  capture TEXT PRIMARY KEY,
  generation TEXT NOT NULL,
  -- owner = source_captures.owner (hash of the install id): counts installations, deleted with the generation.
  owner TEXT NOT NULL,
  parser_version TEXT NOT NULL,
  source_version INTEGER NOT NULL,
  mode TEXT NOT NULL,
  day INTEGER NOT NULL,
  build INTEGER,
  outcome_ok INTEGER NOT NULL,
  offers_ok INTEGER NOT NULL,
  result TEXT,
  placement INTEGER
) WITHOUT ROWID;
CREATE INDEX IF NOT EXISTS source_fact_game_by_generation ON source_fact_game(generation);
CREATE INDEX IF NOT EXISTS source_fact_game_by_mode_day ON source_fact_game(mode, day);
CREATE TABLE IF NOT EXISTS source_fact_offer (
  capture TEXT NOT NULL,
  seq INTEGER NOT NULL,
  kind TEXT NOT NULL,
  slot INTEGER NOT NULL,
  card TEXT NOT NULL,
  picked INTEGER NOT NULL,
  PRIMARY KEY (capture, seq, slot)
) WITHOUT ROWID;
-- body '' = the published numbers were withdrawn (deletion or replaced facts) and a rebuild is pending.
CREATE TABLE IF NOT EXISTS analytics_snap (
  key TEXT PRIMARY KEY,
  rules_version TEXT NOT NULL,
  built INTEGER NOT NULL,
  stale INTEGER NOT NULL DEFAULT 0,
  body TEXT NOT NULL
) WITHOUT ROWID;
