-- One row per shared game. No player name, account, BattleTag or IP address is stored, and nothing about other players.
CREATE TABLE IF NOT EXISTS games (
  id TEXT PRIMARY KEY,          -- random id the app makes per game, so a resend is ignored
  created_at INTEGER NOT NULL,  -- unix ms
  mode TEXT NOT NULL,           -- 'solo' or 'duos'
  bracket INTEGER NOT NULL,     -- rating rounded down to 500; 0 when unknown
  hero TEXT NOT NULL,           -- hero played (base card id, no skins)
  placement INTEGER NOT NULL,   -- final placement
  version INTEGER NOT NULL DEFAULT 1, -- 2 when the details below were shared
  turns INTEGER,                -- game length in turns
  tier INTEGER,                 -- final tavern tier
  triples INTEGER,              -- triples made
  anomaly TEXT,                 -- anomaly card id, if the lobby had one
  tribes TEXT,                  -- tribes in the lobby, comma separated
  comp TEXT                     -- main tribe of the final board (4+ minions), or MIXED
);

-- The heroes offered at the start of each shared game, and which one was picked.
CREATE TABLE IF NOT EXISTS offers (
  game_id TEXT NOT NULL,
  hero TEXT NOT NULL,
  picked INTEGER NOT NULL,
  PRIMARY KEY (game_id, hero)
);

-- The player's final board.
CREATE TABLE IF NOT EXISTS boards (
  game_id TEXT NOT NULL,
  slot INTEGER NOT NULL,
  card TEXT NOT NULL,
  golden INTEGER NOT NULL,
  attack INTEGER NOT NULL,
  health INTEGER NOT NULL,
  PRIMARY KEY (game_id, slot)
);

-- Trinkets offered to the player, and which ones were picked.
CREATE TABLE IF NOT EXISTS trinkets (
  game_id TEXT NOT NULL,
  card TEXT NOT NULL,
  picked INTEGER NOT NULL,
  PRIMARY KEY (game_id, card)
);

-- The turn the player reached each tavern tier.
CREATE TABLE IF NOT EXISTS tierups (
  game_id TEXT NOT NULL,
  tier INTEGER NOT NULL,
  turn INTEGER NOT NULL,
  PRIMARY KEY (game_id, tier)
);

-- Each fight: result (W, T, L), damage taken and dealt, and the odds Hearthpulse' simulator predicted.
CREATE TABLE IF NOT EXISTS combats (
  game_id TEXT NOT NULL,
  idx INTEGER NOT NULL,
  turn INTEGER NOT NULL,
  result TEXT NOT NULL,
  taken INTEGER NOT NULL,
  dealt INTEGER NOT NULL,
  p_win REAL,
  p_tie REAL,
  p_loss REAL,
  PRIMARY KEY (game_id, idx)
);

CREATE INDEX IF NOT EXISTS games_by_mode ON games (mode, created_at, bracket);
CREATE INDEX IF NOT EXISTS boards_by_card ON boards (card);