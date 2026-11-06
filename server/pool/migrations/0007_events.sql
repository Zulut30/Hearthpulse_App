-- Game records of every mode (docs/game-stats.md, ADR 0011). Same as the end of schema.sql.
CREATE TABLE IF NOT EXISTS e (
  id TEXT PRIMARY KEY,
  iid TEXT NOT NULL,
  day INTEGER NOT NULL,
  mode TEXT NOT NULL,
  fmt TEXT,
  v INTEGER NOT NULL,
  body TEXT NOT NULL
) WITHOUT ROWID;

CREATE INDEX IF NOT EXISTS e_by_day ON e (day, mode);
CREATE INDEX IF NOT EXISTS e_by_iid ON e (iid);

CREATE TABLE IF NOT EXISTS equota (
  day INTEGER PRIMARY KEY,
  n INTEGER NOT NULL
);
