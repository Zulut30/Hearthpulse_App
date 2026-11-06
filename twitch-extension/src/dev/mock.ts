// Dev-server stand-in for window.Twitch.ext (open a view with ?mock). Never part of a build: twitch.ts imports it
// only under import.meta.env.DEV. URL options:
//   theme=light|dark  locale=ru-RU|en-US  latency=<seconds, default 2>
//   scenario=deck (default: snapshot + a broadcast every 4 s) | none (mode "none") | empty (snapshot 204, silence)
// In the console: __mock.send(state), __mock.context({ theme: 'light' }).
import type { TwitchAuth, TwitchContext, TwitchExt } from '../twitch';
import { makeEnvelope } from './encode';
import sample from './sample-deck.json';

type ContextCallback = (context: Partial<TwitchContext>, changed: string[]) => void;
type BroadcastCallback = (target: string, contentType: string, message: string) => void;

interface SampleCard {
  id: string;
  dbf: number;
  cost: number;
  name: string;
  nameRu: string;
  left: number;
  total: number;
}

/** The sample deck with names in the view's language (the app sends names in the streamer's language). */
export function sampleState(ru: boolean): unknown {
  const deck = sample.deck;
  const cards = (deck.cards as SampleCard[])
    .map(({ nameRu, ...card }) => ({ ...card, name: ru ? nameRu : card.name }))
    .sort((a, b) => a.cost - b.cost || a.name.localeCompare(b.name));
  const left = cards.reduce((sum, card) => sum + card.left, 0);
  return {
    mode: 'constructed',
    deck: { name: ru ? deck.nameRu : deck.name, hero: deck.hero, format: deck.format, left, cards },
    game: sample.game,
  };
}

export function installMock(): TwitchExt {
  const params = new URLSearchParams(location.search);
  const scenario = params.get('scenario') ?? 'deck';
  const locale = params.get('locale') ?? 'en-US';
  const ru = locale.toLowerCase().startsWith('ru');
  let context: Partial<TwitchContext> = {
    theme: params.get('theme') === 'light' ? 'light' : 'dark',
    language: locale.slice(0, 2),
    hlsLatencyBroadcaster: Number(params.get('latency') ?? 2),
  };
  let contextCallback: ContextCallback | null = null;
  const listeners: BroadcastCallback[] = [];
  let seq = 100;

  const emit = async (state: unknown): Promise<void> => {
    seq += 1;
    const message = await makeEnvelope(seq, state, Date.now());
    console.info('[mock] broadcast seq', seq);
    for (const listener of listeners) listener('broadcast', 'application/json', message);
  };

  const state = sampleState(ru) as { deck: { left: number; cards: SampleCard[] } };
  if (scenario === 'deck') {
    setInterval(() => {
      const live = state.deck.cards.filter((card) => card.left > 0);
      if (live.length === 0) return;
      live[Math.floor(Math.random() * live.length)].left -= 1;
      state.deck.left = state.deck.cards.reduce((sum, card) => sum + card.left, 0);
      void emit(state);
    }, 4000);
  } else if (scenario === 'none') {
    setTimeout(() => void emit({ mode: 'none' }), 300);
  }

  const mock: TwitchExt = {
    onAuthorized(callback) {
      const auth: TwitchAuth = { token: `mock-jwt.${scenario}`, channelId: '1', clientId: 'mock', userId: 'U1' };
      setTimeout(() => callback(auth), 50);
    },
    onContext(callback) {
      contextCallback = callback;
      setTimeout(() => callback(context, Object.keys(context)), 20);
    },
    listen(target, callback) {
      if (target === 'broadcast') listeners.push(callback);
    },
  };

  (window as unknown as { __mock: unknown }).__mock = {
    send: emit,
    context(change: Partial<TwitchContext>) {
      context = { ...context, ...change };
      contextCallback?.(context, Object.keys(change));
    },
  };
  return mock;
}
