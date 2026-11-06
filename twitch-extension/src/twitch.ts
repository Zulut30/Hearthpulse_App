// The part of the Twitch Extension Helper (window.Twitch.ext) these views use.
// https://dev.twitch.tv/docs/extensions/reference/#javascript-helper

export interface TwitchAuth {
  token: string;
  channelId: string;
  clientId: string;
  userId: string;
}

export interface TwitchContext {
  theme?: 'light' | 'dark';
  language?: string;
  hlsLatencyBroadcaster?: number;
  isFullScreen?: boolean;
}

export interface TwitchExt {
  onAuthorized(callback: (auth: TwitchAuth) => void): void;
  onContext(callback: (context: Partial<TwitchContext>, changed: string[]) => void): void;
  listen(target: string, callback: (target: string, contentType: string, message: string) => void): void;
}

declare global {
  interface Window {
    Twitch?: { ext?: TwitchExt };
  }
}

/** Twitch.ext, or the dev mock when the dev server is opened with ?mock (never in a build). */
export async function resolveExt(): Promise<TwitchExt | null> {
  if (import.meta.env.DEV && new URLSearchParams(location.search).has('mock')) {
    const { installMock } = await import('./dev/mock');
    return installMock();
  }
  return window.Twitch?.ext ?? null;
}

const BUILD_API_BASE = (import.meta.env.VITE_API_BASE as string | undefined)?.replace(/\/+$/, '') || 'https://hearthpulse.net';

/**
 * API base for the cold-start snapshot: VITE_API_BASE at build time (Local Test against another server),
 * https://hearthpulse.net otherwise; the dev mock asks the dev server itself (vite.config.ts serves a sample).
 */
export function apiBase(): string {
  if (import.meta.env.DEV && new URLSearchParams(location.search).has('mock')) return location.origin;
  return BUILD_API_BASE;
}
