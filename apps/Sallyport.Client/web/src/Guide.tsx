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
      <li><h2>Add an app.</h2><p>Choose its program file and profile.</p></li>
      <li><h2>Launch here.</h2><p>Sallyport connects the profile first. Other apps keep their normal connection.</p></li>
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
      <p>Proxy mode carries TCP. UDP traffic, including some voice and game traffic, is not routed.</p>
      <p>Closing this window keeps connections running in the tray. Choose ‘Exit Sallyport’ there to disconnect and quit.</p>
    </aside>
  </section>;
}
