import { useEffect, useState } from 'preact/hooks';
import { startFeed, type FeedUpdate } from '../feed';
import { initialLang, langFrom, type Lang } from '../i18n';
import { apiBase, resolveExt, type TwitchExt } from '../twitch';

export type Theme = 'light' | 'dark';

export interface ExtensionView extends FeedUpdate {
  theme: Theme;
  lang: Lang;
}

/** Theme and language from Twitch.ext.onContext (URL locale first), plus the deck feed when `feed` is set. */
export function useExtension(feed: boolean): ExtensionView {
  const [view, setView] = useState<ExtensionView>({ status: 'loading', state: null, theme: 'dark', lang: initialLang() });

  useEffect(() => {
    let ext: TwitchExt | null = null;
    let cancelled = false;
    resolveExt().then((resolved) => {
      if (cancelled) return;
      ext = resolved;
      if (!ext) {
        // Opened outside Twitch: nothing will ever arrive.
        setView((v) => ({ ...v, status: 'ready' }));
        return;
      }
      const deckFeed = feed ? startFeed(ext, apiBase(), (update) => setView((v) => ({ ...v, ...update }))) : null;
      // One onContext callback per page: the Helper keeps a single one.
      ext.onContext((context) => {
        if (deckFeed && 'hlsLatencyBroadcaster' in context) deckFeed.setLatency(context.hlsLatencyBroadcaster);
        setView((v) => ({
          ...v,
          theme: context.theme === 'light' ? 'light' : context.theme === 'dark' ? 'dark' : v.theme,
          lang: context.language ? langFrom(context.language) : v.lang,
        }));
      });
    });
    return () => {
      cancelled = true;
    };
  }, [feed]);

  useEffect(() => {
    document.documentElement.dataset.theme = view.theme;
    document.documentElement.lang = view.lang;
  }, [view.theme, view.lang]);

  return view;
}
