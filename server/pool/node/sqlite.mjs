import { DatabaseSync } from 'node:sqlite';
import { readFileSync } from 'node:fs';

/** The D1 subset used by the legacy Worker, backed by a real private SQLite file. */
export function openDatabase(file) {
  const sqlite = new DatabaseSync(file);
  sqlite.exec('PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000; PRAGMA secure_delete=ON;');
  sqlite.exec(readFileSync(new URL('../schema.sql', import.meta.url), 'utf8'));
  sqlite.exec(readFileSync(new URL('../migrations/0008_captures.sql', import.meta.url), 'utf8'));
  sqlite.exec(readFileSync(new URL('../migrations/0009_analytics.sql', import.meta.url), 'utf8'));
  function statement(sql, args = []) {
    const execute = () => {
      const s = sqlite.prepare(sql);
      // node:sqlite treats numbered placeholders as named parameters. Bare positional '?' also work.
      const params = /\?[1-9]/.test(sql) ? [Object.fromEntries(args.map((v, i) => [String(i + 1), v]))] : args;
      if (s.columns().length) return { results: s.all(...params), success: true, meta: { changes: 0 } };
      const result = s.run(...params);
      return { results: [], success: true, meta: { changes: Number(result.changes), last_row_id: Number(result.lastInsertRowid) } };
    };
    return {
      bind: (...values) => statement(sql, values),
      first: async () => execute().results[0] ?? null,
      all: async () => execute(),
      run: async () => execute(),
      execute,
    };
  }
  const DB = {
    prepare: sql => statement(sql),
    batch: async list => {
      sqlite.exec('SAVEPOINT d1_batch');
      try { const out = list.map(s => s.execute()); sqlite.exec('RELEASE d1_batch'); return out; }
      catch (e) { sqlite.exec('ROLLBACK TO d1_batch; RELEASE d1_batch'); throw e; }
    },
  };
  return { sqlite, DB, close: () => sqlite.close() };
}

/** In-process HTTP cache with bounded size. Never installed on globalThis. */
export class ResponseCache {
  #items = new Map();
  async match(request) {
    const item = this.#items.get(request.url);
    if (!item || item.expires < Date.now()) { this.#items.delete(request.url); return null; }
    return new Response(item.body, { status: item.status, headers: item.headers });
  }
  async put(request, response) {
    const age = Number(/max-age=(\d+)/.exec(response.headers.get('cache-control') ?? '')?.[1] ?? 0);
    if (!age) return;
    const body = await response.arrayBuffer();
    if (body.byteLength > 2 * 1024 * 1024) return;
    if (this.#items.size >= 64) this.#items.delete(this.#items.keys().next().value);
    this.#items.set(request.url, { body, status: response.status, headers: [...response.headers], expires: Date.now() + age * 1000 });
  }
  clear() { this.#items.clear(); }
}

/** Serializes intake, legacy transactions and worker commits in this single-writer service. */
export class SerialQueue {
  #tail = Promise.resolve();
  run(operation) {
    const result = this.#tail.then(operation);
    this.#tail = result.catch(() => {});
    return result;
  }
}
