import type { Frame } from './protocol';

/** hlsLatencyBroadcaster above this is treated as a bad value (seconds). */
const MAX_DELAY_SECONDS = 120;

interface Pending {
  frame: Frame;
  arrivedAt: number;
}

/**
 * Orders frames and holds each one back by the viewer's stream latency.
 *
 * Order: a frame is taken when its seq is newer than the last one taken. A lower seq is old (PubSub may deliver out of
 * order, the snapshot may be older than a broadcast) and is dropped - unless the site received it later than the last
 * taken frame (`at` is the site's receive time and only grows): then the app restarted and counts from the start again.
 *
 * Delay: there is no shared clock with the streamer (task.md, "Решения"), so a frame is shown hlsLatencyBroadcaster
 * seconds after it arrived here - roughly when the viewer sees that moment of the stream. The OBS delay is applied by
 * the app before sending.
 */
export class FrameBuffer {
  private lastSeq = -1;
  private lastAt = -1;
  private delayMs = 0;
  private pending: Pending[] = [];
  private shown: Frame | null = null;

  constructor(private readonly now: () => number = () => Date.now()) {}

  /** hlsLatencyBroadcaster from Twitch.ext.onContext, in seconds; missing or bad values mean no delay. */
  setDelay(seconds: number | undefined | null): void {
    const value = typeof seconds === 'number' && Number.isFinite(seconds) ? seconds : 0;
    this.delayMs = Math.round(Math.min(Math.max(value, 0), MAX_DELAY_SECONDS) * 1000);
  }

  get delay(): number {
    return this.delayMs;
  }

  /** Queues a frame if it is newer than the last accepted one. Returns false for a dropped (old or repeated) frame. */
  push(frame: Frame, arrivedAt: number = this.now()): boolean {
    const newer = frame.seq > this.lastSeq;
    const restarted = frame.seq < this.lastSeq && frame.at > this.lastAt;
    if (!newer && !restarted) return false;
    this.lastSeq = frame.seq;
    this.lastAt = frame.at;
    if (restarted) this.pending = [];
    this.pending.push({ frame, arrivedAt });
    return true;
  }

  /** The newest frame whose delay has passed, if it differs from the one already shown; otherwise null. */
  take(now: number = this.now()): Frame | null {
    let due = -1;
    for (let i = 0; i < this.pending.length; i++) {
      if (this.pending[i].arrivedAt + this.delayMs <= now) due = i;
    }
    if (due < 0) return null;
    const frame = this.pending[due].frame;
    this.pending = this.pending.slice(due + 1);
    if (frame === this.shown) return null;
    this.shown = frame;
    return frame;
  }

  /** Milliseconds until the next queued frame is due (0 if one is due now), or null when nothing waits. */
  nextDueIn(now: number = this.now()): number | null {
    if (this.pending.length === 0) return null;
    return Math.max(0, this.pending[0].arrivedAt + this.delayMs - now);
  }

  get current(): Frame | null {
    return this.shown;
  }
}
