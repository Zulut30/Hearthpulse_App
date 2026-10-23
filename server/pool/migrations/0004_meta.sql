-- Base Battlegrounds data for /v1/meta: one JSON document split into numbered chunks.
-- Hearthpulse ships the table empty. Upstream's import of third-party stats is not part of this repository;
-- /v1/meta answers 503 "meta not imported" until rows from a source we may use are added.
CREATE TABLE IF NOT EXISTS meta (seq INTEGER PRIMARY KEY, body TEXT NOT NULL);
