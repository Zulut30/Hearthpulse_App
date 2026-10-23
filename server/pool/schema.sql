-- One row per shared Battlegrounds game. No player name, account, BattleTag or IP address is stored, and nothing about
-- other players. Lists are stored as JSON in the row, so a game costs one row write (plus its index entry).
CREATE TABLE IF NOT EXISTS g (
  id TEXT PRIMARY KEY,        -- random id per game from the app, so sending a game twice stores it once
  day INTEGER NOT NULL,       -- day the game was played (days since 1970-01-01)
  mode TEXT NOT NULL,         -- 'solo' or 'duos'
  bracket INTEGER NOT NULL,   -- rating rounded down to 500; 0 when unknown
  hero TEXT NOT NULL,         -- hero played (base card id, no skins)
  placement INTEGER NOT NULL, -- final placement
  src TEXT NOT NULL,          -- 'live' (sent when it ended) or 'history' (an earlier game sent once)
  turns INTEGER,              -- game length in turns
  tier INTEGER,               -- final tavern tier
  triples INTEGER,            -- triples made
  anomaly TEXT,               -- anomaly card id, if the lobby had one
  tribes TEXT,                -- tribes in the lobby, comma separated
  comp TEXT,                  -- main tribe of the final board (4+ minions), or MIXED
  offers TEXT,                -- heroes offered: ["id", ...]
  board TEXT,                 -- final board: [{"c": card, "g": golden, "a": attack, "h": health}, ...]
  trinkets TEXT,              -- {"o": offered, "p": picked}
  tierups TEXT,               -- [[turn, tier], ...]
  combats TEXT,               -- [[turn, "W"|"T"|"L", damage taken, damage dealt, predicted win chance or null], ...]
  choices TEXT,               -- picks offered: [[turn, kind, source card or null, [offered], [picked]], ...]
  plays TEXT                  -- cards played from hand: [[turn, card], ...]
);

CREATE INDEX IF NOT EXISTS g_by_mode_day ON g (mode, day);

-- The totals the app shows, worked out by the cron job and served as they are.
CREATE TABLE IF NOT EXISTS snap (
  mode TEXT PRIMARY KEY,
  last_row INTEGER NOT NULL,  -- newest game counted
  built INTEGER NOT NULL,     -- unix ms
  body TEXT NOT NULL          -- the JSON returned by /v1/stats
);

-- Firestone's Battlegrounds data, imported once (import-firestone.mjs + migrations/0004_meta.sql) and served by
-- /v1/meta. The body is one JSON document split into chunks small enough for a D1 statement.
CREATE TABLE IF NOT EXISTS meta (
  seq INTEGER PRIMARY KEY,    -- chunk order
  body TEXT NOT NULL          -- part of the /v1/meta JSON
);