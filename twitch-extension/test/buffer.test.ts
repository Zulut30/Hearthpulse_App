import { describe, expect, it } from 'vitest';
import { FrameBuffer } from '../src/buffer';
import type { Frame } from '../src/protocol';

const frame = (seq: number, at = 1000 + seq): Frame => ({ seq, at, state: { mode: 'none' } });

describe('FrameBuffer order (seq)', () => {
  it('takes newer seq and drops older or repeated ones', () => {
    const buffer = new FrameBuffer(() => 0);
    expect(buffer.push(frame(5))).toBe(true);
    expect(buffer.push(frame(4, 1004))).toBe(false);
    expect(buffer.push(frame(5))).toBe(false);
    expect(buffer.push(frame(6))).toBe(true);
  });

  it('drops a snapshot older than a broadcast that already came', () => {
    const buffer = new FrameBuffer(() => 0);
    buffer.push(frame(10, 5000));
    expect(buffer.push(frame(9, 4000))).toBe(false);
    expect(buffer.take(0)?.seq).toBe(10);
  });

  it('treats a lower seq received later by the site as an app restart', () => {
    const buffer = new FrameBuffer(() => 0);
    buffer.push(frame(50, 5000));
    expect(buffer.push(frame(1, 9000))).toBe(true);
    expect(buffer.take(0)?.seq).toBe(1);
    expect(buffer.push(frame(2, 9100))).toBe(true);
  });
});

describe('FrameBuffer delay (hlsLatencyBroadcaster)', () => {
  it('shows a frame only after the latency has passed since it arrived', () => {
    const buffer = new FrameBuffer();
    buffer.setDelay(4.5);
    buffer.push(frame(1), 10_000);
    expect(buffer.take(14_499)).toBeNull();
    expect(buffer.nextDueIn(14_000)).toBe(500);
    expect(buffer.take(14_500)?.seq).toBe(1);
    expect(buffer.current?.seq).toBe(1);
    expect(buffer.nextDueIn(14_500)).toBeNull();
  });

  it('jumps to the newest due frame and keeps later ones waiting', () => {
    const buffer = new FrameBuffer();
    buffer.setDelay(2);
    buffer.push(frame(1), 0);
    buffer.push(frame(2), 500);
    buffer.push(frame(3), 3000);
    expect(buffer.take(2600)?.seq).toBe(2);
    expect(buffer.take(2700)).toBeNull();
    expect(buffer.nextDueIn(2700)).toBe(2300);
    expect(buffer.take(5000)?.seq).toBe(3);
  });

  it('applies a changed latency to frames already waiting', () => {
    const buffer = new FrameBuffer();
    buffer.setDelay(10);
    buffer.push(frame(1), 0);
    expect(buffer.take(3000)).toBeNull();
    buffer.setDelay(2);
    expect(buffer.take(3000)?.seq).toBe(1);
  });

  it('treats a missing or bad latency as none and caps a huge one', () => {
    const buffer = new FrameBuffer();
    buffer.setDelay(undefined);
    expect(buffer.delay).toBe(0);
    buffer.setDelay(Number.NaN);
    expect(buffer.delay).toBe(0);
    buffer.setDelay(-3);
    expect(buffer.delay).toBe(0);
    buffer.setDelay(1e9);
    expect(buffer.delay).toBe(120_000);
    buffer.setDelay(0);
    buffer.push(frame(1), 100);
    expect(buffer.take(100)?.seq).toBe(1);
  });

  it('a restart drops frames still waiting from before it', () => {
    const buffer = new FrameBuffer();
    buffer.setDelay(5);
    buffer.push(frame(40, 4000), 0);
    buffer.push(frame(1, 9000), 1000);
    expect(buffer.take(10_000)?.seq).toBe(1);
  });
});
