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

-- One row per shared Constructed game, holding only its Discover-style picks (migrations/0005_constructed.sql).
-- No deck, class, rank, opponent, player name or account.
CREATE TABLE IF NOT EXISTS c (
  id TEXT PRIMARY KEY,        -- keyed hash of the game from the app, so sending a game twice stores it once
  day INTEGER NOT NULL,       -- day the game was played (days since 1970-01-01)
  fmt TEXT NOT NULL,          -- 'standard' or 'wild'
  won INTEGER NOT NULL,       -- 1 when the player won
  src TEXT NOT NULL,          -- 'live' (sent when it ended) or 'history'
  picks TEXT NOT NULL         -- [[turn, source card, [offered], [picked]], ...]
);

CREATE INDEX IF NOT EXISTS c_by_fmt_day ON c (fmt, day);

-- The totals the app shows, worked out by the cron job and served as they are: one row per Battlegrounds mode
-- ('solo', 'duos') and per Constructed format of picks ('choices-standard', 'choices-wild').
CREATE TABLE IF NOT EXISTS snap (
  mode TEXT PRIMARY KEY,
  last_row INTEGER NOT NULL,  -- newest game counted
  built INTEGER NOT NULL,     -- unix ms
  body TEXT NOT NULL          -- the JSON returned by /v1/stats
);

-- The Battlegrounds base stats (migrations/0004_meta.sql, shipped empty), served by
-- /v1/meta. The body is one JSON document split into chunks small enough for a D1 statement.
CREATE TABLE IF NOT EXISTS meta (
  seq INTEGER PRIMARY KEY,    -- chunk order
  body TEXT NOT NULL          -- part of the /v1/meta JSON
);
-- Daily count of games stored, against the write budget (migrations/0006_quota.sql).
CREATE TABLE IF NOT EXISTS quota (
  day INTEGER PRIMARY KEY, -- days since 1970-01-01
  n INTEGER NOT NULL       -- games stored that day, both kinds
);

-- Game records (migrations/0007_events.sql, docs/game-stats.md): every finished game a player agreed to send, all
-- modes, one validated JSON record per row. No name, BattleTag or account: only the random id of the install that sent
-- it, so the player can have their records deleted. Kept 180 days.
CREATE TABLE IF NOT EXISTS e (
  id TEXT PRIMARY KEY,        -- keyed hash of the game from the app, so a record sent twice is stored once
  iid TEXT NOT NULL,          -- random install id (never shown, never linked to an account)
  day INTEGER NOT NULL,       -- day the game was played (days since 1970-01-01)
  mode TEXT NOT NULL,         -- ranked, casual, arena, brawl, bg, duos
  fmt TEXT,                   -- standard, wild, classic, twist; null for Battlegrounds and Arena
  v INTEGER NOT NULL,         -- record version
  body TEXT NOT NULL          -- the record as validated by src/events.js
) WITHOUT ROWID;

CREATE INDEX IF NOT EXISTS e_by_day ON e (day, mode);
CREATE INDEX IF NOT EXISTS e_by_iid ON e (iid);

-- Daily count of game records stored, against their own write budget.
CREATE TABLE IF NOT EXISTS equota (
  day INTEGER PRIMARY KEY,
  n INTEGER NOT NULL
);
