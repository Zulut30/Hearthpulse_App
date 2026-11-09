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
export const FORMATS = ['standard', 'wild', 'classic', 'twist'];
export const ARENAS = ['normal', 'underground'];
export const CHOICE_TYPES = ['mulligan', 'general', 'other'];
export const LIFECYCLE_GAPS = ['late_attach', 'rotation', 'shutdown'];
const GAP_REASONS = ['late_attach', 'rotation', 'source_missing', 'unsupported', 'limit', 'invalid', 'shutdown'];
const hidden = (side, zone) => side !== 'local' && ['HAND', 'DECK', 'SETASIDE', 'UNKNOWN'].includes(zone);
function visibleEntity(x) {
  return SIDES.includes(x.side) && ZONES.includes(x.zone) && int(x.entity, 1) && int(x.position, 0, 1000)
    && nullable(x.card, card) && tags(x.tags)
    && (!hidden(x.side, x.zone) || (x.card === null && Object.keys(x.tags).every(t => t === 'ZONE_POSITION')))
    && x.side !== 'none' && (x.side !== 'shop' || x.zone === 'SHOP')
    && (x.side !== 'opponent' || x.zone !== 'SHOP') && (x.side !== 'partner_public' || ['PLAY', 'GRAVEYARD'].includes(x.zone));
}

/**
 * Strict allowlist, never a sanitizer of arbitrary raw log text. Source v2 (TASK-2026-11-09 C1) adds start.format and
 * start.arena, choice.choiceType and the gap reason combat_view; a v1 line carrying any of them is invalid.
 */
export function validRecord(r) {
  if (!fields(r, 'v seq kind turn side entity card target data') || (r.v !== 1 && r.v !== 2) || !int(r.seq, 0, 10_000_000)
    || !int(r.turn, 0, 1000) || !SIDES.includes(r.side) || !int(r.entity) || !int(r.target)
    || !nullable(r.card, card) || !object(r.data) || (r.side === 'none' && r.card !== null)) return false;
  const d = r.data;
  switch (r.kind) {
    case 'start': return fields(d, r.v === 1 ? 'captureKind mode day build' : 'captureKind mode day build format arena')
      && ['game', 'draft'].includes(d.captureKind) && MODES.includes(d.mode)
      && (r.v === 1 || (nullable(d.format, v => FORMATS.includes(v) && ['ranked', 'casual'].includes(d.mode))
        && nullable(d.arena, v => ARENAS.includes(v) && d.mode === 'arena'))) && int(d.day, 0, 100000) && nullable(d.build, v => int(v, 1, 2147483647))
      && r.turn === 0 && r.side === 'none' && r.entity === 0 && r.target === 0;
    case 'end': return fields(d, 'status result placement reason') && ['complete', 'incomplete'].includes(d.status)
      && nullable(d.result, v => ['W', 'L', 'T'].includes(v)) && nullable(d.placement, v => int(v, 1, 8))
      && ['normal', 'abandoned', 'rotation', 'shutdown', 'unsupported', 'unknown'].includes(d.reason)
      && r.side === 'none' && r.entity === 0 && r.target === 0;
    case 'entity': return fields(d, 'zone position tags') && visibleEntity({ ...d, side: r.side, entity: r.entity, card: r.card });
    case 'tag': return fields(d, 'tag value') && TAGS.has(d.tag) && number(d.value)
      && r.side !== 'shop' && (r.side === 'local' || r.card === null);
    case 'block_start': case 'block_end': return fields(d, 'block') && ['PLAY', 'POWER', 'TRIGGER', 'ATTACK', 'DEATHS', 'MOVE_MINION', 'OTHER'].includes(d.block)
      && (r.side === 'local' || r.card === null);
    case 'choice': return fields(d, r.v === 1 ? 'choiceId sourceCard options picked resolved' : 'choiceId sourceCard options picked resolved choiceType')
      && r.side === 'local' && (r.v === 1 || CHOICE_TYPES.includes(d.choiceType))
      && int(d.choiceId) && nullable(d.sourceCard, card) && cards(d.options, 64) && d.options.length > 0 && cards(d.picked, 16)
      && typeof d.resolved === 'boolean' && (d.resolved || d.picked.length === 0) && multisetSubset(d.picked, d.options);
    case 'action': return fields(d, 'action cost') && ['play', 'buy', 'sell', 'refresh', 'freeze', 'upgrade', 'move', 'hero_power', 'receive', 'draw', 'discard', 'unknown'].includes(d.action)
      && nullable(d.cost, v => int(v))
      && (['local', 'opponent', 'partner_public'].includes(r.side) || (r.side === 'none' && d.action === 'unknown' && r.entity === 0 && r.card === null && d.cost === null))
      && (r.side === 'local' || r.card === null || ['play', 'hero_power', 'move'].includes(d.action))
      && (!(r.side === 'opponent' && ['draw', 'receive', 'discard'].includes(d.action)) || (r.card === null && d.cost === null && r.target === 0));
    case 'snapshot': return fields(d, 'gold tier cards') && ['local', 'none'].includes(r.side) && nullable(d.gold, v => int(v))
      && nullable(d.tier, v => int(v, 1, 10)) && Array.isArray(d.cards) && d.cards.length <= 256
      && new Set(d.cards.map(x => x?.entity)).size === d.cards.length
      && d.cards.every(x => fields(x, 'entity card side zone position tags') && visibleEntity(x));
    case 'draft_choice': return fields(d, 'pick options picked underground') && r.side === 'local' && int(d.pick, 0, 1000)
      && Array.isArray(d.options) && d.options.length >= 1 && d.options.length <= 64
      && d.options.every(x => fields(x, 'card package') && card(x.card) && cards(x.package, 64))
      && nullable(d.picked, v => int(v, 0, d.options.length - 1)) && nullable(d.underground, v => typeof v === 'boolean');
    case 'gap': return fields(d, 'reason count') && (GAP_REASONS.includes(d.reason) || (r.v === 2 && d.reason === 'combat_view')) && int(d.count, 1, 1_000_000)
      && r.side === 'none' && r.entity === 0 && r.target === 0;
    default: return false;
  }
}
function multisetSubset(picked, options) {
  const remaining = [...options];
  return picked.every(c => { const i = remaining.indexOf(c); if (i < 0) return false; remaining.splice(i, 1); return true; });
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
    // One source has one wire version; the manifest later pins it (finishStream).
    if (result.length && r.v !== result[0].v) fail(400, 'mixed_source_version');
    result.push(r);
  }
  return result;
}

export function validateManifest(b) {
  identity(b);
  if (Array.isArray(b.chunks) && b.chunks.length > 64) fail(413, 'capture_limit');
  if (!fields(b, 'v iid generation captureId captureKind mode day sourceVersion captureStatus chunks gaps')
    || !['game', 'draft'].includes(b.captureKind) || !MODES.includes(b.mode) || !int(b.day, 0, 100000)
    || ![1, 2].includes(b.sourceVersion) || !['complete', 'incomplete'].includes(b.captureStatus) || !int(b.gaps)
    || !Array.isArray(b.chunks) || !int(b.chunks.length, 1, 64)) fail(400, 'invalid_manifest');
  for (const [i, c] of b.chunks.entries()) {
    if (!fields(c, 'index sha256 uncompressedBytes') || c.index !== i || !HEX64.test(c.sha256 ?? '') || !int(c.uncompressedBytes, 1, 4 * MiB)) fail(400, 'invalid_manifest');
  }
}

export function streamState() { return { version: null, combatView: 0, seq: 0, turn: 0, first: null, last: null, gaps: 0, ended: false, unsupported: false, blocks: [], choices: new Map(), concealed: new Set(), publicRefs: new Set() }; }
export function addRecords(state, list) {
  for (const r of list) {
    if (r.seq !== state.seq++ || state.ended || (r.kind === 'start' && r.seq !== 0)) fail(400, 'invalid_sequence');
    state.version ??= r.v;
    if (r.v !== state.version) fail(400, 'mixed_source_version');
    if (r.kind === 'gap' && r.data.reason === 'combat_view') {
      // C1: one marker, only in a Battlegrounds/Duos game capture.
      const s = state.first?.data;
      if (++state.combatView > 1 || s?.captureKind !== 'game' || !['bg', 'duos'].includes(s?.mode)) fail(400, 'invalid_combat_view');
    }
    if (r.turn < state.turn || (r.seq === 0 && r.kind !== 'start')) fail(400, 'invalid_sequence');
    state.turn = r.turn;
    state.first ??= r;
    state.last = r;
    if (r.kind === 'end') state.ended = true;
    if (r.kind === 'gap') state.gaps += r.data.count;
    if ((r.kind === 'action' && r.data.action === 'unknown') || r.data.block === 'OTHER') state.unsupported = true;
    if (r.kind === 'block_start') {
      if (state.blocks.length >= 64) fail(413, 'block_limit');
      state.blocks.push(r.data.block);
    }
    if (r.kind === 'block_end') {
      if (state.blocks.at(-1) !== r.data.block) {
        if (!state.gaps) fail(400, 'invalid_blocks');
        state.blocks.length = 0; state.unsupported = true;
      } else state.blocks.pop();
    }
    if (r.kind === 'choice') {
      const old = state.choices.get(r.data.choiceId);
      if (old && old.choiceType !== r.data.choiceType) fail(400, 'invalid_choice_progression');
      if (old?.resolved && (JSON.stringify(old.options) !== JSON.stringify(r.data.options) || old.sourceCard !== r.data.sourceCard
        || !r.data.resolved || old.picked.some((x, i) => r.data.picked[i] !== x))) fail(400, 'invalid_choice_progression');
      state.choices.set(r.data.choiceId, r.data);
    }
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
  if (start?.kind !== 'start' || ['captureKind', 'mode', 'day'].some(k => start.data[k] !== b[k]) || state.gaps !== b.gaps
    || state.version !== b.sourceVersion) fail(400, 'manifest_source_mismatch');
  const end = state.last;
  if (b.captureStatus === 'complete' && (end?.kind !== 'end' || end.data.status !== 'complete' || state.gaps !== 0 || state.unsupported
    || state.blocks.length || [...state.choices.values()].some(x => !x.resolved))) fail(400, 'incomplete_stream');
  if (b.captureStatus === 'incomplete' && !(end?.kind === 'gap' || (end?.kind === 'end' && end.data.status === 'incomplete'))) fail(400, 'incomplete_stream');
  if (b.captureKind === 'draft' && end?.kind === 'end' && (end.data.result !== null || end.data.placement !== null)) fail(400, 'draft_result');
}
