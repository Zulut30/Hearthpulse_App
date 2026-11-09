-- Local Node service owns source_* tables. No destructive change to legacy tables.
CREATE TABLE IF NOT EXISTS source_revoked (
  generation TEXT PRIMARY KEY,
  revoked_at INTEGER NOT NULL,
  cleaned INTEGER NOT NULL DEFAULT 0
) WITHOUT ROWID;
CREATE TABLE IF NOT EXISTS source_captures (
  key TEXT PRIMARY KEY,
  generation TEXT NOT NULL,
  owner TEXT NOT NULL,
  created INTEGER NOT NULL,
  touched INTEGER NOT NULL,
  status TEXT NOT NULL DEFAULT 'stored',
  manifest TEXT,
  source_sha TEXT,
  parser_version TEXT,
  records INTEGER NOT NULL DEFAULT 0,
  unsupported INTEGER NOT NULL DEFAULT 0,
  gaps INTEGER NOT NULL DEFAULT 0,
  revision INTEGER NOT NULL DEFAULT 0,
  derived TEXT,
  derived_bytes INTEGER NOT NULL DEFAULT 0
) WITHOUT ROWID;
CREATE INDEX IF NOT EXISTS source_by_generation ON source_captures(generation);
CREATE TABLE IF NOT EXISTS source_chunks (
  capture TEXT NOT NULL REFERENCES source_captures(key) ON DELETE CASCADE,
  idx INTEGER NOT NULL,
  sha TEXT NOT NULL,
  bytes INTEGER NOT NULL,
  compressed_bytes INTEGER NOT NULL,
  PRIMARY KEY(capture, idx)
) WITHOUT ROWID;
CREATE TABLE IF NOT EXISTS source_quota (
  day INTEGER PRIMARY KEY,
  bytes INTEGER NOT NULL DEFAULT 0,
  captures INTEGER NOT NULL DEFAULT 0
);
-- COUNT/MAX(rowid) alone miss deletion followed by an equal-sized replacement. Invalidation is transactional.
CREATE TRIGGER IF NOT EXISTS e_snap_insert AFTER INSERT ON e BEGIN DELETE FROM snap WHERE mode='events'; END;
CREATE TRIGGER IF NOT EXISTS e_snap_delete AFTER DELETE ON e BEGIN DELETE FROM snap WHERE mode='events'; END;
CREATE TRIGGER IF NOT EXISTS e_snap_update AFTER UPDATE ON e BEGIN DELETE FROM snap WHERE mode='events'; END;
CREATE TRIGGER IF NOT EXISTS g_snap_insert AFTER INSERT ON g BEGIN DELETE FROM snap WHERE mode IN ('solo','duos'); END;
CREATE TRIGGER IF NOT EXISTS g_snap_delete AFTER DELETE ON g BEGIN DELETE FROM snap WHERE mode IN ('solo','duos'); END;
CREATE TRIGGER IF NOT EXISTS g_snap_update AFTER UPDATE ON g BEGIN DELETE FROM snap WHERE mode IN ('solo','duos'); END;
CREATE TRIGGER IF NOT EXISTS c_snap_insert AFTER INSERT ON c BEGIN DELETE FROM snap WHERE mode LIKE 'choices-%'; END;
CREATE TRIGGER IF NOT EXISTS c_snap_delete AFTER DELETE ON c BEGIN DELETE FROM snap WHERE mode LIKE 'choices-%'; END;
CREATE TRIGGER IF NOT EXISTS c_snap_update AFTER UPDATE ON c BEGIN DELETE FROM snap WHERE mode LIKE 'choices-%'; END;
