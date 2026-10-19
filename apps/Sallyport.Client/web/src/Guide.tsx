import { useId } from 'react';
import type { ClientState } from './contract';
import { post } from './commands';
import { ActionButton, Disclosure, PageHeader } from './ui/shared';

export function Guide({ guide, manualOpen, toggleManual }: {
  guide: ClientState['guide']; manualOpen: boolean; toggleManual: () => void;
}) {
  const manualId = useId();
  return <section className="page guide" aria-label="How to use">
    <PageHeader title="How to use" />
    <ol className="steps">
      <li><h2>Import a profile.</h2><p>Choose the OpenVPN profile supplied by your VPN provider.</p></li>
      <li><h2>Add a game, or an app.</h2><p>A game by its folder: Sallyport lists its programs. An app by its program file, if it uses proxy settings.</p></li>
      <li><h2>Run the game, or launch the app.</h2><p>Sallyport connects the profile. Everything else on this computer keeps its normal connection.</p></li>
    </ol>
    <div className="manual-setup">
      <Disclosure open={manualOpen} controls={manualId} onClick={toggleManual}>Manual setup</Disclosure>
      <div id={manualId} hidden={!manualOpen} className="manual-content">
        <p>Choose SOCKS5, host 127.0.0.1, the profile’s port, and remote DNS where available.</p>
        <p><code>{guide.endpointText}</code></p>
        <pre>{guide.curlExample}</pre>
        <ActionButton action={guide.copyExample} onClick={() => post('COPY_GUIDE_EXAMPLE', {})} />
      </div>
    </div>
    <aside className="guide-notes">
      <p>Games are routed by their programs, TCP and UDP alike, from their first packet when run before the game starts. Their IPv6 and their traffic to this computer's own network are blocked, not routed. Apps launched here use proxy settings, which carry TCP only.</p>
      <p>Closing this window keeps connections and game routing running in the tray. Choose ‘Exit Sallyport’ there to stop routing, disconnect and quit.</p>
    </aside>
  </section>;
}
