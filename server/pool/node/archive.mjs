import * as fs from 'node:fs';
import path from 'node:path';
import { createHash, randomUUID } from 'node:crypto';
import { spawn } from 'node:child_process';
import { gzipSync } from 'node:zlib';
import { SerialQueue } from './sqlite.mjs';
import { CaptureError, MiB, exact, fail, identity, inflate, int, HEX64, records, sha, validateManifest, streamState, addRecords, finishStream, validRecord } from './validation.mjs';

function syncDirectory(dir) {
  if (process.platform === 'win32') return; // Windows fsync cannot open directory handles through node:fs.
  const fd = fs.openSync(dir, 'r');
  try { fs.fsyncSync(fd); } finally { fs.closeSync(fd); }
}
function mkdir(dir) { fs.mkdirSync(dir, { recursive: true, mode: 0o700 }); }
function atomicWrite(file, bytes) {
  const tmp = `${file}.${randomUUID()}.tmp`;
  let fd;
  try {
    fd = fs.openSync(tmp, 'wx', 0o600);
    fs.writeFileSync(fd, bytes);
    fs.fsyncSync(fd);
    fs.closeSync(fd); fd = undefined;
    fs.renameSync(tmp, file);
    syncDirectory(path.dirname(file));
  } finally {
    if (fd !== undefined) fs.closeSync(fd);
    fs.rmSync(tmp, { force: true });
  }
}
const transaction = (db, action) => {
  db.exec('BEGIN IMMEDIATE');
  try { const out = action(); db.exec('COMMIT'); return out; }
  catch (e) { db.exec('ROLLBACK'); throw e; }
};
const nowDay = () => Math.floor(Date.now() / 86_400_000);

/** Private file archive. SQL stores receipts, immutable manifests, current results and permanent tombstones. */
export class CaptureArchive {
  constructor({ sqlite, root, queue = new SerialQueue(), maxArchiveBytes = 256 * MiB, dailyBytes = 128 * MiB,
    dailyCaptures = 1000, minFreeBytes = 64 * MiB, retentionDays = 180, unfinishedHours = 24,
    worker = null, workerTimeoutMs = 120_000, maxWorkerOutputBytes = 64 * MiB }) {
    this.db = sqlite;
    this.root = path.resolve(root);
    this.queue = queue;
    Object.assign(this, { maxArchiveBytes, dailyBytes, dailyCaptures, minFreeBytes, retentionDays, unfinishedHours, worker, workerTimeoutMs, maxWorkerOutputBytes });
    this.active = new Map();
    this.jobs = new Set();
    this.revocations = new Set();
    this.storageNeedsRecovery = false;
    this.closed = false;
    mkdir(this.root);
    this.ledger = path.join(this.root, 'revocations.jsonl');
    this.captureRoot = path.join(this.root, 'captures');
    mkdir(this.captureRoot);
    syncDirectory(this.root);
    syncDirectory(path.dirname(this.root));
  }
  dir(id) { return path.join(this.captureRoot, id.generation, id.key); }
  chunkPath(id, index) { return path.join(this.dir(id), `${index}.jsonl.gz`); }
  revoked(generation) { return this.revocations.has(generation) || !!this.db.prepare('SELECT 1 FROM source_revoked WHERE generation=?').get(generation); }
  check(id) {
    if (this.revoked(id.generation)) fail(410, 'revoked_generation');
    if (this.db.prepare("SELECT 1 FROM source_captures WHERE key=? AND status='deleted'").get(id.key)) fail(410, 'deleted_capture');
  }
  row(key) { return this.db.prepare('SELECT * FROM source_captures WHERE key=?').get(key); }
  usage() {
    return this.db.prepare(`SELECT (SELECT COALESCE(SUM(bytes + compressed_bytes),0) FROM source_chunks)
      + (SELECT COALESCE(SUM(derived_bytes),0) FROM source_captures) AS n`).get().n
      + this.db.prepare("SELECT COUNT(*) AS n FROM source_captures WHERE status='processing'").get().n * this.maxWorkerOutputBytes;
  }
  pressure(extra) {
    if (this.usage() + extra > this.maxArchiveBytes) fail(503, 'archive_pressure');
    const space = fs.statfsSync(this.root);
    if (space.bavail * space.bsize < this.minFreeBytes + extra) fail(503, 'disk_pressure');
  }
  /** Replay the non-rollback revocation ledger before serving anything, clean orphan writes and interrupted work. */
  async recover() {
    return this.queue.run(async () => {
      if (fs.existsSync(this.ledger)) {
        const data = fs.readFileSync(this.ledger, 'utf8');
        // A torn last append is safe to discard: no deletion was acknowledged before fsync succeeded.
        const complete = data.slice(0, data.lastIndexOf('\n') + 1);
        for (const line of complete.split('\n').filter(Boolean)) {
          let item;
          try { item = JSON.parse(line); } catch { throw new Error('revocation_ledger_corrupt'); }
          if (!exact(item, ['generation', 'at']) || !HEX64.test(item.generation) || !int(item.at, 0, Number.MAX_SAFE_INTEGER)) throw new Error('revocation_ledger_corrupt');
          this.revocations.add(item.generation);
          this.db.prepare('INSERT OR IGNORE INTO source_revoked(generation,revoked_at) VALUES(?,?)').run(item.generation, item.at);
        }
        if (data.length !== complete.length) {
          const fd = fs.openSync(this.ledger, 'r+');
          try { fs.ftruncateSync(fd, Buffer.byteLength(complete)); fs.fsyncSync(fd); } finally { fs.closeSync(fd); }
        }
      }
      this.db.prepare("UPDATE source_captures SET status='stored',revision=revision+1 WHERE status='processing'").run();
      for (const generation of fs.readdirSync(this.captureRoot)) {
        if (!HEX64.test(generation)) throw new Error('unexpected_archive_entry');
        const gdir = path.join(this.captureRoot, generation);
        for (const key of fs.readdirSync(gdir)) {
          if (!HEX64.test(key)) throw new Error('unexpected_archive_entry');
          const id = { generation, key };
          const row = this.row(key);
          if (!row || row.status === 'deleted' || this.revoked(generation)) { fs.rmSync(this.dir(id), { force: true, recursive: true }); continue; }
          const keep = new Set(this.db.prepare('SELECT idx FROM source_chunks WHERE capture=?').all(key).map(r => `${r.idx}.jsonl.gz`));
          for (const name of fs.readdirSync(this.dir(id))) {
            if (!keep.has(name)) fs.rmSync(path.join(this.dir(id), name), { force: true, recursive: true });
          }
        }
      }
      for (const r of this.db.prepare('SELECT generation FROM source_revoked').all()) await this.cleanGeneration(r.generation);
    });
  }
  async upload(body) {
    return this.queue.run(async () => {
      const id = identity(body);
      this.check(id);
      if (this.storageNeedsRecovery) fail(503, 'storage_recovery_required');
      if (!exact(body, ['v', 'iid', 'generation', 'captureId', 'index', 'sha256', 'encoding', 'uncompressedBytes', 'data'])
        || !int(body.index, 0, 1000000) || typeof body.sha256 !== 'string' || !HEX64.test(body.sha256) || body.encoding !== 'gzip'
        || !int(body.uncompressedBytes, 1, Number.MAX_SAFE_INTEGER) || typeof body.data !== 'string') fail(400, 'invalid_chunk');
      if (body.index > 63 || body.uncompressedBytes > 4 * MiB) fail(413, 'chunk_limit');
      if (body.data.length > Math.ceil(4 * MiB / 3) * 4) fail(413, 'compressed_limit');
      if (body.data.length % 4 || !/^[A-Za-z0-9+/]*={0,2}$/.test(body.data)) fail(400, 'invalid_base64');
      const compressed = Buffer.from(body.data, 'base64');
      if (compressed.toString('base64') !== body.data) fail(400, 'invalid_base64');
      if (compressed.length > 4 * MiB) fail(413, 'compressed_limit');
      const bytes = await inflate(compressed);
      if (bytes.length !== body.uncompressedBytes || sha(bytes) !== body.sha256) fail(400, 'checksum_mismatch');
      records(bytes);
      // A gzip filename/comment is arbitrary client text too. Archive only a fresh gzip of validated exact source bytes.
      const canonical = gzipSync(bytes);
      const receipt = this.db.prepare('SELECT * FROM source_chunks WHERE capture=? AND idx=?').get(id.key, body.index);
      if (receipt && (receipt.sha !== body.sha256 || receipt.bytes !== bytes.length)) fail(409, 'chunk_conflict');
      const row = this.row(id.key);
      if (!receipt && row?.manifest) fail(409, 'capture_sealed');
      if (receipt) {
        // Repair a missing/corrupt physical chunk after a crash/restore, rather than acknowledge a nonexistent file.
        let valid = false;
        try { valid = sha(await inflate(fs.readFileSync(this.chunkPath(id, body.index)))) === receipt.sha; } catch {}
        if (!valid) {
          this.pressure(canonical.length);
          atomicWrite(this.chunkPath(id, body.index), canonical);
        }
        return { statusCode: 200, body: { ok: true, stored: true, duplicate: true, index: body.index, sha256: body.sha256 } };
      }
      const total = this.db.prepare('SELECT COALESCE(SUM(bytes),0) AS n FROM source_chunks WHERE capture=?').get(id.key).n;
      if (total + bytes.length > 256 * MiB) fail(413, 'capture_limit');
      const quota = this.db.prepare('SELECT * FROM source_quota WHERE day=?').get(nowDay());
      if ((quota?.bytes ?? 0) + bytes.length > this.dailyBytes || (!row && (quota?.captures ?? 0) >= this.dailyCaptures)) fail(503, 'daily_pressure');
      this.pressure(bytes.length + canonical.length);
      mkdir(this.dir(id));
      syncDirectory(path.dirname(this.dir(id)));
      syncDirectory(this.captureRoot);
      atomicWrite(this.chunkPath(id, body.index), canonical);
      try { transaction(this.db, () => {
        this.check(id);
        this.db.prepare(`INSERT INTO source_captures(key,generation,owner,created,touched) VALUES(?,?,?,?,?)
          ON CONFLICT(key) DO UPDATE SET touched=excluded.touched`).run(id.key, id.generation, id.owner, Date.now(), Date.now());
        this.db.prepare('INSERT INTO source_chunks(capture,idx,sha,bytes,compressed_bytes) VALUES(?,?,?,?,?)').run(id.key, body.index, body.sha256, bytes.length, canonical.length);
        this.db.prepare(`INSERT INTO source_quota(day,bytes,captures) VALUES(?,?,?) ON CONFLICT(day)
          DO UPDATE SET bytes=bytes+excluded.bytes,captures=captures+excluded.captures`).run(nowDay(), bytes.length, row ? 0 : 1);
      }); } catch (e) {
        // A file without a committed receipt is not allowed to bypass the archive budget on later requests.
        if (!this.db.prepare('SELECT 1 FROM source_chunks WHERE capture=? AND idx=?').get(id.key, body.index)) {
          try { fs.rmSync(this.chunkPath(id, body.index), { force: true }); syncDirectory(this.dir(id)); }
          catch { this.storageNeedsRecovery = true; }
        }
        throw e;
      }
      return { statusCode: 201, body: { ok: true, stored: true, duplicate: false, index: body.index, sha256: body.sha256 } };
    });
  }
  async verify(id, manifest, sink = null) {
    const receipts = this.db.prepare('SELECT * FROM source_chunks WHERE capture=? ORDER BY idx').all(id.key);
    const missing = manifest.chunks.filter(c => !receipts.some(r => r.idx === c.index && r.sha === c.sha256 && r.bytes === c.uncompressedBytes)).map(c => c.index);
    if (missing.length || receipts.length !== manifest.chunks.length) fail(409, 'missing_chunks', { missing });
    const state = streamState();
    const hash = createHash('sha256');
    for (const c of manifest.chunks) {
      let bytes;
      try { bytes = await inflate(fs.readFileSync(this.chunkPath(id, c.index))); }
      catch { fail(409, 'missing_chunks', { missing: [c.index] }); }
      if (bytes.length !== c.uncompressedBytes || sha(bytes) !== c.sha256) fail(409, 'missing_chunks', { missing: [c.index] });
      addRecords(state, records(bytes));
      hash.update(bytes);
      if (sink !== null) fs.writeFileSync(sink, bytes);
    }
    finishStream(state, manifest);
    return { hash: hash.digest('hex'), records: state.seq, gaps: state.gaps };
  }
  async complete(body) {
    return this.queue.run(async () => {
      validateManifest(body);
      const id = identity(body);
      this.check(id);
      const normalized = JSON.stringify({ captureKind: body.captureKind, mode: body.mode, day: body.day, sourceVersion: body.sourceVersion,
        captureStatus: body.captureStatus, chunks: body.chunks.map(c => ({ index: c.index, sha256: c.sha256, uncompressedBytes: c.uncompressedBytes })), gaps: body.gaps });
      const row = this.row(id.key);
      if (row?.manifest && row.manifest !== normalized) fail(409, 'manifest_conflict');
      const verified = await this.verify(id, body);
      if (!row?.manifest) this.db.prepare(`UPDATE source_captures SET manifest=?,source_sha=?,records=?,gaps=?,status='stored',touched=? WHERE key=?`)
        .run(normalized, verified.hash, verified.records, verified.gaps, Date.now(), id.key);
      return { statusCode: 202, body: { ok: true, status: 'stored', captureId: body.captureId } };
    });
  }
  status(body) {
    return this.queue.run(() => {
      const id = identity(body);
      if (!exact(body, ['v', 'iid', 'generation', 'captureId'])) fail(400, 'invalid_status');
      const r = this.row(id.key);
      if (this.revoked(id.generation) || r?.status === 'deleted') return { ok: true, status: 'deleted', captureId: body.captureId, parserVersion: null, records: 0, unsupported: 0, gaps: 0 };
      if (!r) fail(404, 'capture_not_found');
      return { ok: true, status: r.status, captureId: body.captureId, parserVersion: r.parser_version, records: r.records, unsupported: r.unsupported, gaps: r.gaps };
    });
  }
  reprocess(body) {
    return this.queue.run(() => {
      const id = identity(body);
      if (!exact(body, ['v', 'iid', 'generation', 'captureId'])) fail(400, 'invalid_reprocess');
      this.check(id);
      const r = this.row(id.key);
      if (!r?.manifest) fail(409, 'archive_incomplete');
      if (r.status === 'processing') fail(409, 'already_processing');
      this.db.prepare("UPDATE source_captures SET status='stored',revision=revision+1 WHERE key=?").run(id.key);
      return { statusCode: 202, body: { ok: true, status: 'stored', captureId: body.captureId } };
    });
  }
  revoke(generation) {
    if (this.revoked(generation)) return;
    const at = Date.now();
    const fd = fs.openSync(this.ledger, 'a', 0o600);
    try { fs.writeFileSync(fd, `${JSON.stringify({ generation, at })}\n`); fs.fsyncSync(fd); } finally { fs.closeSync(fd); }
    this.revocations.add(generation);
    syncDirectory(this.root);
    this.db.prepare('INSERT OR IGNORE INTO source_revoked(generation,revoked_at) VALUES(?,?)').run(generation, at);
  }
  async cleanGeneration(generation) {
    for (const job of this.active.values()) if (job.row.generation === generation) { job.stop(); await job.exited; }
    // Remove SQL-derived data as soon as revoked; pending filesystem cleanup still reports deleting.
    transaction(this.db, () => {
      this.db.prepare('DELETE FROM source_captures WHERE generation=?').run(generation);
      this.db.prepare("DELETE FROM snap WHERE mode='source'").run();
    });
    try {
      this.removeDirectory(path.join(this.captureRoot, generation));
      syncDirectory(this.captureRoot);
      this.db.prepare('UPDATE source_revoked SET cleaned=1 WHERE generation=?').run(generation);
      this.db.exec('PRAGMA wal_checkpoint(TRUNCATE)');
      return true;
    } catch { this.db.prepare('UPDATE source_revoked SET cleaned=0 WHERE generation=?').run(generation); return false; }
  }
  removeDirectory(directory) { fs.rmSync(directory, { recursive: true, force: true }); }
  forget(body) {
    return this.queue.run(async () => {
      const id = identity(body, false);
      if (!exact(body, ['v', 'iid', 'generation'])) fail(400, 'invalid_forget');
      this.revoke(id.generation);
      return await this.cleanGeneration(id.generation) ? { statusCode: 200, body: { ok: true, status: 'deleted' } }
        : { statusCode: 202, body: { ok: true, status: 'deleting' } };
    });
  }
  async maintain(time = Date.now()) {
    return this.queue.run(async () => {
      for (const row of this.db.prepare("SELECT * FROM source_captures WHERE status<>'deleted'").all()) {
        const ttl = row.manifest ? this.retentionDays * 86_400_000 : this.unfinishedHours * 3600_000;
        if (row.created + ttl >= time) continue;
        const id = { generation: row.generation, key: row.key };
        // Tombstone first, so late uploads and a running worker cannot recreate an expired capture.
        transaction(this.db, () => {
          this.db.prepare("UPDATE source_captures SET status='deleted',manifest=NULL,source_sha=NULL,parser_version=NULL,derived=NULL,derived_bytes=0,records=0,gaps=0,unsupported=0,revision=revision+1 WHERE key=?").run(row.key);
          this.db.prepare('DELETE FROM source_chunks WHERE capture=?').run(row.key);
        });
        const job = this.active.get(row.key);
        if (job) { job.stop(); await job.exited; }
        try { fs.rmSync(this.dir(id), { force: true, recursive: true }); } catch { /* retried below and on recovery */ }
      }
      for (const row of this.db.prepare("SELECT key,generation FROM source_captures WHERE status='deleted'").all()) {
        try { fs.rmSync(this.dir(row), { force: true, recursive: true }); } catch {}
      }
      for (const row of this.db.prepare('SELECT generation FROM source_revoked WHERE cleaned=0').all()) await this.cleanGeneration(row.generation);
      this.db.prepare('DELETE FROM source_quota WHERE day<?').run(nowDay() - 7);
      // SQLite secure_delete scrubs rows; truncate WAL so a successful forget does not leave old facts in it.
      this.db.exec('PRAGMA wal_checkpoint(TRUNCATE)');
    });
  }
  async tick() {
    if (!this.worker || this.closed || this.jobs.size) return false;
    const job = this.runNext();
    this.jobs.add(job);
    try { return await job; } finally { this.jobs.delete(job); }
  }
  async runNext() {
    let task;
    await this.queue.run(async () => {
      if (this.closed) return;
      const row = this.db.prepare("SELECT * FROM source_captures WHERE status='stored' AND manifest IS NOT NULL ORDER BY created LIMIT 1").get();
      if (!row || this.revoked(row.generation)) return;
      const id = { generation: row.generation, key: row.key };
      // Unique paths isolate a restarted job from an orphaned worker of a crashed API process.
      const jobId = randomUUID();
      const input = path.join(this.dir(id), `worker-${jobId}-input.jsonl`);
      const output = path.join(this.dir(id), `worker-${jobId}-output.json`);
      try {
        this.pressure(this.maxWorkerOutputBytes);
        const fd = fs.openSync(input, 'w', 0o600);
        let verified;
        try { verified = await this.verify(id, JSON.parse(row.manifest), fd); fs.fsyncSync(fd); } finally { fs.closeSync(fd); }
        if (verified.hash !== row.source_sha) fail(409, 'source_changed');
        fs.rmSync(output, { force: true });
        this.db.prepare("UPDATE source_captures SET status='processing' WHERE key=?").run(row.key);
        const process = launchWorker(this.worker, input, output, this.workerTimeoutMs);
        task = { row, id, input, output, ...process };
        this.active.set(row.key, task);
      } catch (e) {
        if (!(e instanceof CaptureError && e.status === 503)) this.db.prepare("UPDATE source_captures SET status='failed' WHERE key=?").run(row.key);
        fs.rmSync(input, { force: true });
      }
    });
    if (!task) return false;
    const outcome = await task.exited;
    await this.queue.run(async () => {
      try {
        const latest = this.row(task.row.key);
        if (this.closed || this.revoked(task.row.generation) || !latest || latest.status !== 'processing' || latest.revision !== task.row.revision) return;
        if (!outcome.ok) throw new Error('worker_failed');
        const stat = fs.lstatSync(task.output);
        if (!stat.isFile() || stat.isSymbolicLink() || stat.size > this.maxWorkerOutputBytes) throw new Error('worker_output_limit');
        const result = JSON.parse(fs.readFileSync(task.output, 'utf8'));
        await this.validateResult(result, task);
        const data = JSON.stringify(result);
        this.pressure(Math.max(0, Buffer.byteLength(data) - latest.derived_bytes) - this.maxWorkerOutputBytes);
        transaction(this.db, () => {
          this.check(task.id);
          this.db.prepare(`UPDATE source_captures SET status=?,parser_version=?,records=?,unsupported=?,gaps=?,derived=?,derived_bytes=?
            WHERE key=? AND revision=? AND status='processing'`).run(result.status, result.parserVersion, result.records, result.unsupported, result.gaps, data, Buffer.byteLength(data), task.row.key, task.row.revision);
          this.db.prepare("DELETE FROM snap WHERE mode='source'").run();
        });
      } catch {
        this.db.prepare("UPDATE source_captures SET status='failed' WHERE key=? AND status='processing' AND revision=?").run(task.row.key, task.row.revision);
      } finally {
        this.active.delete(task.row.key);
        // No mkdir here: a late completion cannot recreate a deleted generation.
        try {
          for (const name of fs.readdirSync(this.dir(task.id))) {
            if (!/^\d+\.jsonl\.gz$/.test(name)) fs.rmSync(path.join(this.dir(task.id), name), { force: true, recursive: true });
          }
        } catch {}
      }
    });
    return true;
  }
  /** Derived output is bounded and can only refer to validated source facts, never new arbitrary strings. */
  async validateResult(r, task) {
    const baseSummary = ['plays', 'buys', 'sells', 'choices', 'turns'];
    const counters = [...baseSummary, 'shopSnapshots', 'resources', 'draftChoices'];
    const fullSummary = [...counters, 'captureKind', 'mode', 'captureStatus', 'hasEnd', 'completeForAggregation', 'result', 'placement'];
    const manifest = JSON.parse(task.row.manifest);
    if (!exact(r, ['v', 'parserVersion', 'sourceSha256', 'status', 'records', 'unsupported', 'gaps', 'events', 'summary'])
      || r.v !== 1 || typeof r.parserVersion !== 'string' || !/^[A-Za-z0-9_.-]{1,64}$/.test(r.parserVersion)
      || r.sourceSha256 !== task.row.source_sha || !['parsed', 'partial'].includes(r.status)
      || r.records !== task.row.records || !int(r.unsupported, 0, r.records) || !int(r.gaps)
      || r.gaps < task.row.gaps || ((r.gaps > 0 || r.unsupported > 0) && r.status !== 'partial')
      || !Array.isArray(r.events) || r.events.length > r.records || !(exact(r.summary, baseSummary) || exact(r.summary, fullSummary))
      || !counters.filter(k => Object.hasOwn(r.summary, k)).every(k => int(r.summary[k], 0, Math.max(r.records, 1000)))) throw new Error('invalid_worker_output');
    if (Object.hasOwn(r.summary, 'captureKind')) {
      const s = r.summary;
      if (s.captureKind !== manifest.captureKind || s.mode !== manifest.mode || !['complete', 'incomplete'].includes(s.captureStatus)
        || typeof s.hasEnd !== 'boolean' || typeof s.completeForAggregation !== 'boolean'
        || !(s.result === null || ['W', 'L', 'T'].includes(s.result)) || !(s.placement === null || int(s.placement, 1, 8))
        || (s.completeForAggregation && (s.captureKind !== 'game' || s.captureStatus !== 'complete' || r.status !== 'parsed' || !s.hasEnd || r.gaps !== 0))
        || (s.captureStatus === 'incomplete' && (s.result !== null || s.placement !== null))
        || (s.captureKind === 'draft' && (s.result !== null || s.placement !== null))) throw new Error('invalid_worker_summary');
    }
    let previous = -1;
    const bySeq = new Map(r.events.map(e => [e.seq, e]));
    for (const e of r.events) {
      if (!(exact(e, ['seq', 'kind', 'turn', 'card', 'data']) || exact(e, ['seq', 'kind', 'turn', 'side', 'entity', 'card', 'target', 'data']))
        || !int(e.seq, 0, r.records - 1) || e.seq <= previous) throw new Error('invalid_worker_event');
      previous = e.seq;
    }
    // Compare output to typed source; avoids accepting fields/identifiers invented by a faulty worker.
    let endClaim = null;
    for (const c of manifest.chunks) {
      for (const source of records(await inflate(fs.readFileSync(this.chunkPath(task.id, c.index))))) {
        if (source.kind === 'end') endClaim = source.data;
        const e = bySeq.get(source.seq);
        if (!e) continue;
        if (![source.kind, derivedKind(source)].includes(e.kind) || e.turn !== source.turn || e.card !== source.card || !subset(e.data, source.data)
          || (Object.hasOwn(e, 'side') && ['side', 'entity', 'target'].some(k => e[k] !== source[k]))) throw new Error('worker_event_not_in_source');
      }
    }
    if (Object.hasOwn(r.summary, 'hasEnd')) {
      const s = r.summary;
      if (s.hasEnd !== !!endClaim || (s.result !== null && s.result !== endClaim?.result)
        || (s.placement !== null && s.placement !== endClaim?.placement)
        || (s.captureStatus === 'complete' && endClaim?.status !== 'complete')
        || (s.completeForAggregation && manifest.captureStatus !== 'complete')) throw new Error('worker_summary_not_in_source');
    }
  }
  async close() {
    this.closed = true;
    for (const job of this.active.values()) job.stop();
    await this.queue.run(() => { for (const job of this.active.values()) job.stop(); });
    await Promise.allSettled([...this.jobs]);
  }
}

/** Configuration-only executable/argv. HTTP callers never select either, or supply paths. */
export function launchWorker(config, input, output, timeoutMs) {
  if (!path.isAbsolute(config.executable) || !Array.isArray(config.args) || !config.args.every(x => typeof x === 'string')) throw new Error('invalid_worker_config');
  const child = spawn(config.executable, [...config.args, '--input', input, '--output', output], {
    shell: false, windowsHide: true, detached: process.platform !== 'win32', stdio: ['ignore', 'pipe', 'pipe'],
    env: { ...process.env, DOTNET_CLI_TELEMETRY_OPTOUT: '1', DOTNET_NOLOGO: '1' },
  });
  let stopped = false;
  const stop = () => {
    stopped = true;
    try { if (process.platform !== 'win32' && child.pid) process.kill(-child.pid, 'SIGKILL'); else child.kill('SIGKILL'); } catch {}
  };
  const exited = new Promise(resolve => {
    let bytes = 0;
    const parts = [];
    let stderr = 0;
    const timer = setTimeout(stop, timeoutMs);
    child.stdout.on('data', data => { bytes += data.length; if (bytes > 16 * 1024) stop(); else parts.push(data); });
    child.stderr.on('data', data => { stderr += data.length; if (stderr > 16 * 1024) stop(); });
    child.on('error', () => { clearTimeout(timer); resolve({ ok: false }); });
    child.on('close', code => {
      clearTimeout(timer);
      let status;
      try { status = JSON.parse(Buffer.concat(parts).toString('utf8')); } catch {}
      resolve({ ok: !stopped && code === 0 && status?.ok === true && ['parsed', 'partial'].includes(status?.status) });
    });
  });
  return { stop, exited };
}

export { atomicWrite };

function subset(value, source) {
  if (value === null || typeof value !== 'object') return value === source;
  if (Array.isArray(value)) return Array.isArray(source) && value.length === source.length && value.every((v, i) => subset(v, source[i]));
  return source !== null && typeof source === 'object' && !Array.isArray(source)
    && Object.keys(value).every(k => Object.hasOwn(source, k) && subset(value[k], source[k]));
}

function derivedKind(source) {
  if (source.kind === 'action') return source.data.action;
  if (source.kind === 'tag') {
    if (source.data.tag === 'TURN') return 'turn';
    if (['RESOURCES', 'RESOURCES_USED', 'TEMP_RESOURCES', 'OVERLOAD_LOCKED', 'OVERLOAD_OWED'].includes(source.data.tag)) return 'resources';
  }
  if ((source.kind === 'entity' && source.side === 'shop') || (source.kind === 'snapshot' && source.data.cards.some(c => c.side === 'shop'))) return 'shop';
  return source.kind;
}
