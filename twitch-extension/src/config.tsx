import { render } from 'preact';
import { strings } from './i18n';
import { useExtension } from './ui/useExtension';
import './styles.css';

/** Broadcaster config page: how to connect. Nothing to store - the app and hearthpulse.net hold the settings. */
function Config() {
  const view = useExtension(false);
  const t = strings(view.lang);
  return (
    <main class="config">
      <h1>{t.configTitle}</h1>
      <p>{t.configIntro}</p>
      <ol>
        {t.configSteps.map((step) => (
          <li key={step}>{step}</li>
        ))}
      </ol>
      <p class="config-note">{t.configNote}</p>
    </main>
  );
}

render(<Config />, document.getElementById('app')!);
