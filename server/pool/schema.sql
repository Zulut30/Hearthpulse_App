-- One row per shared game. No player name, account, BattleTag or IP address is stored.
CREATE TABLE IF NOT EXISTS games (
  id TEXT PRIMARY KEY,          -- random id the app makes per game, so a resend is ignored
  created_at INTEGER NOT NULL,  -- unix ms
  mode TEXT NOT NULL,           -- 'solo' or 'duos'
  bracket INTEGER NOT NULL,     -- rating rounded down to 500; 0 when unknown
  hero TEXT NOT NULL,           -- hero played (base card id, no skins)
  placement INTEGER NOT NULL    -- final placement
);

-- The heroes offered at the start of each shared game, and which one was picked.
CREATE TABLE IF NOT EXISTS offers (
  game_id TEXT NOT NULL,
  hero TEXT NOT NULL,
  picked INTEGER NOT NULL,
  PRIMARY KEY (game_id, hero)
);

CREATE INDEX IF NOT EXISTS games_by_mode ON games (mode, created_at, bracket);