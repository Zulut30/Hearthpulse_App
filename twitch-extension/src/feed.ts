import { FrameBuffer } from './buffer';
import { decodeEnvelope, type State } from './protocol';
import type { TwitchExt } from './twitch';

export type FeedStatus = 'loading' | 'ready';

export interface FeedUpdate {
  status: FeedStatus;
  /** The state shown now; null until the first frame (or after a 204 / failed snapshot, which means "not in game"). */
  state: State | null;
}

/** GET /api/v1/twitch/ext/snapshot with the viewer JWT: 200 envelope | 204 nothing recent | other = failure. */
export async function fetchSnapshot(apiBase: string, token: string, fetcher: typeof fetch = fetch): Promise<unknown | null> {
  const response = await fetcher(`${apiBase}/api/v1/twitch/ext/snapshot`, {
    headers: { Authorization: `Bearer ${token}` },
    cache: 'no-store',
    credentials: 'omit',
  });
  if (response.status === 204) return null;
  if (!response.ok) throw new Error(`snapshot ${response.status}`);
  return response.json();
}

/**
 * Wires Twitch.ext to the frame buffer: PubSub `broadcast` messages and the cold-start snapshot go in, the frame due
 * after hlsLatencyBroadcaster comes out through `onUpdate`. Bad envelopes are dropped silently (nothing to show the
 * viewer about them).
 */
export interface Feed {
  /** hlsLatencyBroadcaster from onContext (the caller owns the single onContext callback). */
  setLatency(seconds: number | undefined): void;
}

export function startFeed(
  ext: TwitchExt,
  apiBase: string,
  onUpdate: (update: FeedUpdate) => void,
  fetcher: typeof fetch = fetch,
): Feed {
  const buffer = new FrameBuffer();
  let timer: ReturnType<typeof setTimeout> | undefined;
  let snapshotAsked = false;
  let status: FeedStatus = 'loading';

  const pump = (): void => {
    if (timer !== undefined) clearTimeout(timer);
    timer = undefined;
    const frame = buffer.take();
    if (frame) {
      status = 'ready';
      onUpdate({ status, state: frame.state });
    }
    const wait = buffer.nextDueIn();
    if (wait !== null) timer = setTimeout(pump, wait + 5);
  };

  const accept = (raw: unknown): Promise<void> =>
    decodeEnvelope(raw).then((frame) => {
      if (buffer.push(frame)) pump();
    });

  const settleEmpty = (): void => {
    if (status === 'loading' && buffer.current === null && buffer.nextDueIn() === null) {
      status = 'ready';
      onUpdate({ status, state: null });
    }
  };

  ext.listen('broadcast', (_target, _contentType, message) => {
    accept(message).catch(() => undefined);
  });

  ext.onAuthorized((auth) => {
    if (snapshotAsked) return;
    snapshotAsked = true;
    fetchSnapshot(apiBase, auth.token, fetcher)
      .then((body) => (body === null ? settleEmpty() : accept(body)))
      .catch(settleEmpty);
  });

  return {
    setLatency(seconds) {
      buffer.setDelay(seconds);
      pump();
    },
  };
}
