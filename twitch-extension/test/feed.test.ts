import { describe, expect, it } from 'vitest';
import { makeEnvelope } from '../src/dev/encode';
import { startFeed, type FeedUpdate } from '../src/feed';
import type { TwitchAuth, TwitchExt } from '../src/twitch';

const deck = {
  mode: 'constructed',
  deck: { name: 'Test', hero: null, format: 'wild', left: 2, cards: [{ id: 'EX1_116', cost: 5, name: 'Leeroy', left: 1, total: 1 }] },
};

function fakeExt() {
  let authorized: ((auth: TwitchAuth) => void) | null = null;
  let broadcast: ((target: string, contentType: string, message: string) => void) | null = null;
  const ext: TwitchExt = {
    onAuthorized: (callback) => (authorized = callback),
    onContext: () => undefined,
    listen: (target, callback) => {
      if (target === 'broadcast') broadcast = callback;
    },
  };
  return {
    ext,
    authorize: (token = 'jwt') => authorized?.({ token, channelId: '1', clientId: 'c', userId: 'U1' }),
    send: (message: string) => broadcast?.('broadcast', 'application/json', message),
  };
}

const settle = () => new Promise((resolve) => setTimeout(resolve, 30));

describe('startFeed', () => {
  it('cold start: fetches the snapshot once with the viewer JWT and shows it', async () => {
    const twitch = fakeExt();
    const updates: FeedUpdate[] = [];
    const calls: { url: string; auth: string | null }[] = [];
    const body = JSON.parse(await makeEnvelope(5, deck));
    const fetcher = (async (url: string, init?: RequestInit) => {
      calls.push({ url, auth: new Headers(init?.headers).get('Authorization') });
      return new Response(JSON.stringify(body), { status: 200 });
    }) as typeof fetch;
    startFeed(twitch.ext, 'https://api.test', (u) => updates.push(u), fetcher);
    twitch.authorize('jwt-1');
    twitch.authorize('jwt-2'); // Twitch re-sends onAuthorized on token refresh
    await settle();
    expect(calls).toEqual([{ url: 'https://api.test/api/v1/twitch/ext/snapshot', auth: 'Bearer jwt-1' }]);
    expect(updates.at(-1)?.state).toMatchObject({ mode: 'constructed', deck: { name: 'Test' } });
  });

  it('204 or a failed snapshot means "not in game" when nothing else came', async () => {
    for (const fetcher of [
      (async () => new Response(null, { status: 204 })) as unknown as typeof fetch,
      (async () => new Response('{}', { status: 401 })) as unknown as typeof fetch,
      (async () => {
        throw new TypeError('offline');
      }) as unknown as typeof fetch,
    ]) {
      const twitch = fakeExt();
      const updates: FeedUpdate[] = [];
      startFeed(twitch.ext, 'https://api.test', (u) => updates.push(u), fetcher);
      twitch.authorize();
      await settle();
      expect(updates).toEqual([{ status: 'ready', state: null }]);
    }
  });

  it('a broadcast newer than the snapshot wins; bad messages are ignored', async () => {
    const twitch = fakeExt();
    const updates: FeedUpdate[] = [];
    const old = JSON.parse(await makeEnvelope(3, deck, 1000));
    let release: () => void = () => undefined;
    const gate = new Promise<void>((resolve) => (release = resolve));
    const fetcher = (async () => {
      await gate;
      return new Response(JSON.stringify(old), { status: 200 });
    }) as unknown as typeof fetch;
    startFeed(twitch.ext, 'https://api.test', (u) => updates.push(u), fetcher);
    twitch.authorize();
    twitch.send('not json');
    twitch.send(await makeEnvelope(4, { mode: 'none' }, 2000));
    await settle();
    release();
    await settle();
    expect(updates).toEqual([{ status: 'ready', state: { mode: 'none' } }]);
  });
});
