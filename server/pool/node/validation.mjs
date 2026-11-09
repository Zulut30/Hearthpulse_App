import { createHash } from 'node:crypto';
import { createGunzip } from 'node:zlib';
import { Readable } from 'node:stream';

export const MiB = 1024 * 1024;
export const HEX32 = /^[a-f0-9]{32}$/;
export const HEX64 = /^[a-f0-9]{64}$/;
export const MODES = ['ranked', 'casual', 'arena', 'brawl', 'bg', 'duos'];
const SIDES = ['none', 'local', 'opponent', 'shop', 'partner_public'];
const ZONES = ['HAND', 'PLAY', 'DECK', 'GRAVEYARD', 'SETASIDE', 'SHOP', 'UNKNOWN'];
const TAGS = new Set(('TURN STEP RESOURCES RESOURCES_USED TEMP_RESOURCES OVERLOAD_LOCKED OVERLOAD_OWED DAMAGE HEALTH ATK ARMOR DURABILITY COST TECH_LEVEL PREMIUM ZONE_POSITION TAUNT DIVINE_SHIELD WINDFURY REBORN STEALTH FROZEN POISONOUS VENOMOUS').split(' '));
export class CaptureError extends Error {
  constructor(status, code, details = {}) { super(code); this.status = status; this.code = code; this.details = details; }
}
export const fail = (status, code, details) => { throw new CaptureError(status, code, details); };
export const int = (x, min = 0, max = 1_000_000) => Number.isSafeInteger(x) && x >= min && x <= max;
export const object = x => x !== null && typeof x === 'object' && !Array.isArray(x);
export const exact = (x, keys) => object(x) && Object.keys(x).length === keys.length && keys.every(k => Object.hasOwn(x, k));
const card = x => typeof x === 'string' && /^[A-Za-z0-9_]{1,80}$/.test(x);
const nullable = (x, check) => x === null || check(x);
const cards = (x, max) => Array.isArray(x) && x.length <= max && x.every(card);
const number = x => int(x, -1_000_000, 1_000_000);
const tags = x => object(x) && Object.keys(x).length <= TAGS.size && Object.entries(x).every(([k, v]) => TAGS.has(k) && number(v));
const fields = (d, names) => exact(d, names.split(' '));
const hidden = (side, zone) => side !== 'local' && ['HAND', 'DECK', 'SETASIDE', 'UNKNOWN'].includes(zone);
function visibleEntity(x) {
  return SIDES.includes(x.side) && ZONES.includes(x.zone) && int(x.entity) && int(x.position, 0, 1000)
    && nullable(x.card, card) && tags(x.tags)
    && (!hidden(x.side, x.zone) || (x.card === null && Object.keys(x.tags).every(t => t === 'ZONE_POSITION')))
    && x.side !== 'none' && (x.side !== 'shop' || x.zone === 'SHOP')
    && (x.side !== 'opponent' || x.zone !== 'SHOP') && (x.side !== 'partner_public' || ['PLAY', 'GRAVEYARD'].includes(x.zone));
}

/** Strict allowlist, never a sanitizer of arbitrary raw log text. */
export function validRecord(r) {
  if (!fields(r, 'v seq kind turn side entity card target data') || r.v !== 1 || !int(r.seq, 0, 10_000_000)
    || !int(r.turn, 0, 1000) || !SIDES.includes(r.side) || !int(r.entity) || !int(r.target)
    || !nullable(r.card, card) || !object(r.data) || (r.side === 'none' && r.card !== null)) return false;
  const d = r.data;
  switch (r.kind) {
    case 'start': return fields(d, 'captureKind mode day build') && ['game', 'draft'].includes(d.captureKind)
      && MODES.includes(d.mode) && int(d.day, 0, 100000) && nullable(d.build, v => int(v, 0, 100_000_000))
      && r.side === 'none' && r.entity === 0 && r.target === 0;
    case 'end': return fields(d, 'status result placement reason') && ['complete', 'incomplete'].includes(d.status)
      && nullable(d.result, v => ['W', 'L', 'T'].includes(v)) && nullable(d.placement, v => int(v, 1, 8))
      && ['normal', 'abandoned', 'rotation', 'shutdown', 'unsupported', 'unknown'].includes(d.reason)
      && (d.status === 'complete' || (d.result === null && d.placement === null)) && r.side === 'none';
    case 'entity': return fields(d, 'zone position tags') && visibleEntity({ ...d, side: r.side, entity: r.entity, card: r.card });
    case 'tag': return fields(d, 'tag value') && TAGS.has(d.tag) && number(d.value)
      && r.side !== 'shop' && (r.side === 'local' || r.card === null);
    case 'block_start': case 'block_end': return fields(d, 'block') && ['PLAY', 'POWER', 'TRIGGER', 'ATTACK', 'DEATHS', 'MOVE_MINION', 'OTHER'].includes(d.block);
    case 'choice': return fields(d, 'choiceId sourceCard options picked resolved') && r.side === 'local'
      && int(d.choiceId) && nullable(d.sourceCard, card) && cards(d.options, 64) && cards(d.picked, 16)
      && d.picked.every(c => d.options.includes(c)) && typeof d.resolved === 'boolean';
    case 'action': return fields(d, 'action cost') && ['play', 'buy', 'sell', 'refresh', 'freeze', 'upgrade', 'move', 'hero_power', 'receive', 'draw', 'discard', 'unknown'].includes(d.action)
      && nullable(d.cost, v => int(v, 0, 1000))
      && (!(r.side === 'opponent' && ['draw', 'receive', 'discard'].includes(d.action)) || (r.card === null && d.cost === null && r.target === 0));
    case 'snapshot': return fields(d, 'gold tier cards') && nullable(d.gold, v => int(v, 0, 1000))
      && nullable(d.tier, v => int(v, 1, 7)) && Array.isArray(d.cards) && d.cards.length <= 256
      && d.cards.every(x => fields(x, 'entity card side zone position tags') && visibleEntity(x));
    case 'draft_choice': return fields(d, 'pick options picked underground') && r.side === 'local' && int(d.pick, 0, 1000)
      && Array.isArray(d.options) && d.options.length >= 1 && d.options.length <= 64
      && d.options.every(x => fields(x, 'card package') && card(x.card) && cards(x.package, 64))
      && nullable(d.picked, v => int(v, 0, d.options.length - 1)) && nullable(d.underground, v => typeof v === 'boolean');
    case 'gap': return fields(d, 'reason count') && ['late_attach', 'rotation', 'source_missing', 'unsupported', 'limit', 'invalid', 'shutdown'].includes(d.reason) && int(d.count, 1, 1_000_000);
    default: return false;
  }
}
export const sha = bytes => createHash('sha256').update(bytes).digest('hex');
export function parseJson(text) {
  // JSON.parse silently accepts duplicate object keys. Keeping the original bytes would then archive the
  // shadowed, unvalidated value. Reject duplicates at every depth before accepting any source or envelope.
  const stack = [];
  const tokens = /"(?:[^"\\]|\\.)*"|[{}\[\]:,]|-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?|true|false|null/g;
  for (const token of text.matchAll(tokens)) {
    const t = token[0];
    if (t === '{' || t === '[') {
      if (stack.length >= 16) fail(400, 'json_depth');
      stack.push({ object: t === '{', keys: new Set(), key: t === '{' });
    } else if (t === '}' || t === ']') stack.pop();
    else if (t === ',') { if (stack.at(-1)?.object) stack.at(-1).key = true; }
    else if (t === ':') { if (stack.at(-1)) stack.at(-1).key = false; }
    else if (t[0] === '"' && stack.at(-1)?.object && stack.at(-1).key) {
      const key = JSON.parse(t);
      if (stack.at(-1).keys.has(key)) fail(400, 'duplicate_json_key');
      stack.at(-1).keys.add(key);
    }
  }
  try { return JSON.parse(text); } catch { fail(400, 'invalid_json'); }
}
export function identity(body, capture = true) {
  const hex = x => typeof x === 'string' && HEX32.test(x);
  if (body?.v !== 1 || !hex(body.iid) || !hex(body.generation) || (capture && !hex(body.captureId)))
    fail(400, 'invalid_identity');
  const generation = sha(`${body.iid}:${body.generation}`);
  return { generation, owner: sha(body.iid), key: capture ? sha(`${generation}:${body.captureId}`) : null };
}

/** Output is bounded while inflating, including concatenated gzip members. */
export async function inflate(bytes, max = 4 * MiB) {
  const gunzip = createGunzip();
  const parts = [];
  let length = 0;
  try {
    for await (const part of Readable.from([bytes]).pipe(gunzip)) {
      length += part.length;
      if (length > max) { gunzip.destroy(); fail(413, 'decompressed_limit'); }
      parts.push(part);
    }
  } catch (error) {
    if (error instanceof CaptureError) throw error;
    fail(400, 'invalid_gzip');
  }
  return Buffer.concat(parts, length);
}

/** Each transport chunk contains whole JSONL records, with the original bytes hashed unchanged. */
export function records(bytes) {
  if (!bytes.length || bytes[bytes.length - 1] !== 10 || bytes.includes(13) || (bytes[0] === 239 && bytes[1] === 187 && bytes[2] === 191)) fail(400, 'invalid_jsonl');
  let decoded;
  try { decoded = new TextDecoder('utf-8', { fatal: true }).decode(bytes); } catch { fail(400, 'invalid_utf8'); }
  const result = [];
  for (const line of decoded.slice(0, -1).split('\n')) {
    if (Buffer.byteLength(line) + 1 > 64 * 1024) fail(413, 'line_limit');
    let r;
    r = parseJson(line);
    if (!validRecord(r)) fail(400, 'invalid_record');
    result.push(r);
  }
  return result;
}

export function validateManifest(b) {
  identity(b);
  if (Array.isArray(b.chunks) && b.chunks.length > 64) fail(413, 'capture_limit');
  if (!fields(b, 'v iid generation captureId captureKind mode day sourceVersion captureStatus chunks gaps')
    || !['game', 'draft'].includes(b.captureKind) || !MODES.includes(b.mode) || !int(b.day, 0, 100000)
    || b.sourceVersion !== 1 || !['complete', 'incomplete'].includes(b.captureStatus) || !int(b.gaps)
    || !Array.isArray(b.chunks) || !int(b.chunks.length, 1, 64)) fail(400, 'invalid_manifest');
  for (const [i, c] of b.chunks.entries()) {
    if (!fields(c, 'index sha256 uncompressedBytes') || c.index !== i || !HEX64.test(c.sha256 ?? '') || !int(c.uncompressedBytes, 1, 4 * MiB)) fail(400, 'invalid_manifest');
  }
}

export function streamState() { return { seq: 0, first: null, last: null, gaps: 0, ended: false, unsupported: false, concealed: new Set(), publicRefs: new Set() }; }
export function addRecords(state, list) {
  for (const r of list) {
    if (r.seq !== state.seq++ || state.ended || (r.kind === 'start' && r.seq !== 0)) fail(400, 'invalid_sequence');
    state.first ??= r;
    state.last = r;
    if (r.kind === 'end') state.ended = true;
    if (r.kind === 'gap') state.gaps += r.data.count;
    if ((r.kind === 'action' && r.data.action === 'unknown') || r.data.block === 'OTHER') state.unsupported = true;
    const entities = r.kind === 'snapshot' ? r.data.cards : r.kind === 'entity' ? [{ ...r.data, entity: r.entity, side: r.side, card: r.card }] : [];
    for (const e of entities) {
      if (!e.entity) continue;
      if (hidden(e.side, e.zone)) {
        if (state.publicRefs.has(e.entity)) fail(400, 'concealed_reference_reused');
        state.concealed.add(e.entity);
      } else {
        if (state.concealed.has(e.entity)) fail(400, 'concealed_reference_reused');
        state.publicRefs.add(e.entity);
      }
    }
    if (r.target && state.concealed.has(r.target)) fail(400, 'concealed_reference_reused');
    if (r.card !== null && r.entity && state.concealed.has(r.entity)) fail(400, 'concealed_reference_reused');
    if (r.kind === 'tag' && r.side === 'opponent' && r.entity && r.data.tag !== 'ZONE_POSITION' && !state.publicRefs.has(r.entity)) fail(400, 'unobserved_opponent_tag');
  }
}
export function finishStream(state, b) {
  const start = state.first;
  if (start?.kind !== 'start' || ['captureKind', 'mode', 'day'].some(k => start.data[k] !== b[k]) || state.gaps !== b.gaps) fail(400, 'manifest_source_mismatch');
  const end = state.last;
  if (b.captureStatus === 'complete' && (end?.kind !== 'end' || end.data.status !== 'complete' || state.gaps !== 0 || state.unsupported)) fail(400, 'incomplete_stream');
  if (b.captureStatus === 'incomplete' && !(end?.kind === 'gap' || (end?.kind === 'end' && end.data.status === 'incomplete'))) fail(400, 'incomplete_stream');
  if (b.captureKind === 'draft' && end?.kind === 'end' && (end.data.result !== null || end.data.placement !== null)) fail(400, 'draft_result');
}
