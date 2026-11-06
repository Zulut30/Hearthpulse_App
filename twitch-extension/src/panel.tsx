import { render } from 'preact';
import { useState } from 'preact/hooks';
import { strings } from './i18n';
import { CardPreview, DeckHeader, Disclaimer, DeckRows, EmptyState, type Hover } from './ui/Deck';
import { useExtension } from './ui/useExtension';
import './styles.css';

/** Panel under the video (318 px wide, up to 496 high): the same deck, the list scrolls inside the panel. */
function Panel() {
  const view = useExtension(true);
  const t = strings(view.lang);
  const [hover, setHover] = useState<Hover | null>(null);
  const deck = view.state?.mode === 'constructed' ? view.state.deck : null;
  return (
    <main class="panel" aria-label={t.title}>
      {deck ? (
        <>
          <DeckHeader deck={deck} t={t} />
          <div class="panel-scroll" onScroll={() => setHover(null)}>
            <DeckRows deck={deck} t={t} onHover={setHover} />
          </div>
        </>
      ) : (
        <EmptyState t={t} loading={view.status === 'loading'} />
      )}
      <Disclaimer t={t} />
      <CardPreview hover={hover} lang={view.lang} side="center" />
    </main>
  );
}

render(<Panel />, document.getElementById('app')!);
