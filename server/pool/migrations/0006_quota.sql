-- Daily count of games stored, against the pool's own write budget (POST answers 503 past it). Plan 2.1 review.
CREATE TABLE IF NOT EXISTS quota (
  day INTEGER PRIMARY KEY, -- days since 1970-01-01
  n INTEGER NOT NULL       -- games stored that day, both kinds
);
