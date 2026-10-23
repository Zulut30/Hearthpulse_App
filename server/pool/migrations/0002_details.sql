-- Upgrade a pool created with the first schema (v1 games only). Run once:
-- npx wrangler d1 execute hearthpulse-pool --remote --file=migrations/0002_details.sql
ALTER TABLE games ADD COLUMN version INTEGER NOT NULL DEFAULT 1;
ALTER TABLE games ADD COLUMN turns INTEGER;
ALTER TABLE games ADD COLUMN tier INTEGER;
ALTER TABLE games ADD COLUMN triples INTEGER;
ALTER TABLE games ADD COLUMN anomaly TEXT;
ALTER TABLE games ADD COLUMN tribes TEXT;
ALTER TABLE games ADD COLUMN comp TEXT;
CREATE TABLE IF NOT EXISTS boards (game_id TEXT NOT NULL, slot INTEGER NOT NULL, card TEXT NOT NULL, golden INTEGER NOT NULL, attack INTEGER NOT NULL, health INTEGER NOT NULL, PRIMARY KEY (game_id, slot));
CREATE TABLE IF NOT EXISTS trinkets (game_id TEXT NOT NULL, card TEXT NOT NULL, picked INTEGER NOT NULL, PRIMARY KEY (game_id, card));
CREATE TABLE IF NOT EXISTS tierups (game_id TEXT NOT NULL, tier INTEGER NOT NULL, turn INTEGER NOT NULL, PRIMARY KEY (game_id, tier));
CREATE TABLE IF NOT EXISTS combats (game_id TEXT NOT NULL, idx INTEGER NOT NULL, turn INTEGER NOT NULL, result TEXT NOT NULL, taken INTEGER NOT NULL, dealt INTEGER NOT NULL, p_win REAL, p_tie REAL, p_loss REAL, PRIMARY KEY (game_id, idx));
CREATE INDEX IF NOT EXISTS boards_by_card ON boards (card);