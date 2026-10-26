-- Constructed picks: one row per shared Constructed game with only its Discover-style picks, served by /v1/choices
-- from snap rows 'choices-standard' and 'choices-wild'.
CREATE TABLE IF NOT EXISTS c (
  id TEXT PRIMARY KEY,
  day INTEGER NOT NULL,
  fmt TEXT NOT NULL,
  won INTEGER NOT NULL,
  src TEXT NOT NULL,
  picks TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS c_by_fmt_day ON c (fmt, day);
