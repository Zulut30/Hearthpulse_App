import { render } from 'preact';
import { useState } from 'preact/hooks';
import { strings } from './i18n';
import { CardPreview, DeckHeader, Disclaimer, DeckRows, EmptyState, type Hover } from './ui/Deck';
import { useExtension } from './ui/useExtension';
import './styles.css';

const COLLAPSED_KEY = 'hearthpulse.overlay.collapsed';

function readCollapsed(): boolean {
  try {
    return localStorage.getItem(COLLAPSED_KEY) === '1';
  } catch {
    return false;
  }
}

function writeCollapsed(value: boolean): void {
  try {
    localStorage.setItem(COLLAPSED_KEY, value ? '1' : '0');
  } catch {
    // storage blocked: the choice lasts for this page only
  }
}

function CardsIcon() {
  return (
    <svg viewBox="0 0 24 24" width="18" height="18" aria-hidden="true">
      <path
        fill="currentColor"
        d="M11.19 2.25c.66-.27 1.42.04 1.69.7l4.95 11.95c.27.66-.04 1.42-.7 1.69l-7.37 3.06c-.66.27-1.42-.04-1.69-.7L3.12 7c-.27-.66.04-1.42.7-1.69zM14.3 3.5h1.45a1.5 1.5 0 0 1 1.5 1.5v4.9zm4.5 1.62 1.34.56c.66.27.97 1.03.7 1.69l-2.04 4.92z"
      />
    </svg>
  );
}

/**
 * Video overlay: the deck on the right edge of the video, kept out of the player's top title bar and bottom controls
 * (no clicks reach the overlay there). It collapses into a round button that keeps the number of cards left.
 */
function Overlay() {
  const view = useExtension(true);
  const t = strings(view.lang);
  const [collapsed, setCollapsed] = useState(readCollapsed);
  const [hover, setHover] = useState<Hover | null>(null);
  const deck = view.state?.mode === 'constructed' ? view.state.deck : null;

  const toggle = () => {
    const next = !collapsed;
    setCollapsed(next);
    writeCollapsed(next);
    setHover(null);
  };

  return (
    <div class="overlay-zone">
      <div class="overlay-dock">
        <button
          class="dock-button"
          type="button"
          aria-expanded={!collapsed}
          aria-label={collapsed ? t.expand : t.collapse}
          title={collapsed ? t.expand : t.collapse}
          onClick={toggle}
        >
          <CardsIcon />
          {deck && <span class="dock-count">{deck.left}</span>}
        </button>
        {!collapsed && (
          <section class="card overlay-card" aria-label={t.title}>
            {deck ? (
              <>
                <DeckHeader deck={deck} t={t} />
                <DeckRows deck={deck} t={t} onHover={setHover} />
              </>
            ) : (
              <EmptyState t={t} loading={view.status === 'loading'} />
            )}
            <Disclaimer t={t} />
          </section>
        )}
      </div>
      {!collapsed && <CardPreview hover={hover} lang={view.lang} side="left" />}
    </div>
  );
}

render(<Overlay />, document.getElementById('app')!);
