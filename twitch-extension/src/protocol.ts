// The PubSub envelope and the state inside it (contract: reference/tasks/TASK-2026-11-07-twitch-extension/task.md,
// "Сайт → Twitch → фронт"). The site sends one JSON string per message:
//   { "v": 1, "seq": 42, "at": 1762500000000, "z": "<base64 gzip JSON of the state without v/seq>" }
// and GET /api/v1/twitch/ext/snapshot returns the same envelope. Everything coming in is untrusted: it is checked
// field by field, unknown fields are dropped, and only the streamer's remaining deck is kept (no hand, no deck order,
// nothing about the opponent - AC9).

export type DeckFormat = 'standard' | 'wild' | 'twist' | 'classic' | 'casual' | 'other';

export interface DeckCard {
  id: string;
  cost: number;
  name: string;
  left: number;
  total: number;
}

export interface Deck {
  name: string | null;
  hero: string | null;
  format: DeckFormat;
  left: number;
  cards: DeckCard[];
}

export type State = { mode: 'none' } | { mode: 'constructed'; deck: Deck };

export interface Envelope {
  v: 1;
  seq: number;
  at: number;
  z: string;
}

export interface Frame {
  seq: number;
  at: number;
  state: State;
}

/** Twitch caps a PubSub message at 5 KB; anything much bigger is not ours. */
const MAX_ENVELOPE_CHARS = 16 * 1024;
/** Decompressed state stays well under the app's 16 KB request limit; cap it to stop a zip bomb. */
const MAX_STATE_BYTES = 64 * 1024;
const MAX_CARDS = 80;
const MAX_NAME = 80;
const CARD_ID = /^[A-Za-z0-9_]{1,64}$/;
const FORMATS: readonly DeckFormat[] = ['standard', 'wild', 'twist', 'classic', 'casual', 'other'];

export class EnvelopeError extends Error {}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function isCount(value: unknown, max: number): value is number {
  return typeof value === 'number' && Number.isInteger(value) && value >= 0 && value <= max;
}

/** Parses the envelope (a PubSub string or an already parsed snapshot body). Throws EnvelopeError on a bad one. */
export function parseEnvelope(raw: unknown): Envelope {
  let value = raw;
  if (typeof raw === 'string') {
    if (raw.length > MAX_ENVELOPE_CHARS) throw new EnvelopeError('envelope too large');
    try {
      value = JSON.parse(raw);
    } catch {
      throw new EnvelopeError('envelope is not JSON');
    }
  }
  if (!isRecord(value)) throw new EnvelopeError('envelope is not an object');
  if (value.v !== 1) throw new EnvelopeError('unknown envelope version');
  if (!isCount(value.seq, Number.MAX_SAFE_INTEGER)) throw new EnvelopeError('bad seq');
  if (!isCount(value.at, Number.MAX_SAFE_INTEGER)) throw new EnvelopeError('bad at');
  if (typeof value.z !== 'string' || value.z.length === 0 || value.z.length > MAX_ENVELOPE_CHARS) {
    throw new EnvelopeError('bad payload');
  }
  return { v: 1, seq: value.seq, at: value.at, z: value.z };
}

function base64ToBytes(text: string): Uint8Array {
  let binary: string;
  try {
    binary = atob(text);
  } catch {
    throw new EnvelopeError('payload is not base64');
  }
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
  return bytes;
}

/** gunzip through the browser's DecompressionStream, stopping at MAX_STATE_BYTES. */
export async function gunzip(bytes: Uint8Array): Promise<string> {
  const stream = new Blob([bytes as BlobPart]).stream().pipeThrough(new DecompressionStream('gzip'));
  const reader = stream.getReader();
  const chunks: Uint8Array[] = [];
  let size = 0;
  try {
    for (;;) {
      const { done, value } = await reader.read();
      if (done) break;
      size += value.byteLength;
      if (size > MAX_STATE_BYTES) {
        await reader.cancel();
        throw new EnvelopeError('state too large');
      }
      chunks.push(value);
    }
  } catch (error) {
    if (error instanceof EnvelopeError) throw error;
    throw new EnvelopeError('payload is not gzip');
  }
  const all = new Uint8Array(size);
  let offset = 0;
  for (const chunk of chunks) {
    all.set(chunk, offset);
    offset += chunk.byteLength;
  }
  return new TextDecoder('utf-8', { fatal: false }).decode(all);
}

function readCard(raw: unknown): DeckCard | null {
  if (!isRecord(raw)) return null;
  if (typeof raw.id !== 'string' || !CARD_ID.test(raw.id)) return null;
  if (!isCount(raw.cost, 99) || !isCount(raw.left, 99) || !isCount(raw.total, 99)) return null;
  const name = typeof raw.name === 'string' && raw.name.trim() ? raw.name.trim().slice(0, MAX_NAME) : raw.id;
  return { id: raw.id, cost: raw.cost, name, left: raw.left, total: raw.total };
}

function readText(raw: unknown): string | null {
  return typeof raw === 'string' && raw.trim() ? raw.trim().slice(0, MAX_NAME) : null;
}

/** Validates the decompressed state. Only the deck list is kept; any other field (game, future ones) is ignored. */
export function readState(raw: unknown): State {
  if (!isRecord(raw)) throw new EnvelopeError('state is not an object');
  if (raw.mode === 'none') return { mode: 'none' };
  if (raw.mode !== 'constructed') throw new EnvelopeError('unknown mode');
  const deck = raw.deck;
  if (!isRecord(deck) || !Array.isArray(deck.cards)) throw new EnvelopeError('bad deck');
  const cards = deck.cards.slice(0, MAX_CARDS).map(readCard).filter((card): card is DeckCard => card !== null);
  const format = FORMATS.includes(deck.format as DeckFormat) ? (deck.format as DeckFormat) : 'other';
  const left = isCount(deck.left, 999) ? deck.left : cards.reduce((sum, card) => sum + card.left, 0);
  return {
    mode: 'constructed',
    deck: { name: readText(deck.name), hero: readText(deck.hero), format, left, cards },
  };
}

/** Envelope -> frame: base64, gunzip, JSON, validation. Throws EnvelopeError on any bad step. */
export async function decodeEnvelope(raw: unknown): Promise<Frame> {
  const envelope = parseEnvelope(raw);
  const text = await gunzip(base64ToBytes(envelope.z));
  let json: unknown;
  try {
    json = JSON.parse(text);
  } catch {
    throw new EnvelopeError('state is not JSON');
  }
  return { seq: envelope.seq, at: envelope.at, state: readState(json) };
}
