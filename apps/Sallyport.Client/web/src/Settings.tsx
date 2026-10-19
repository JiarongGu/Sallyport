import { useId, type ReactNode } from 'react';
import type { ClientState, DriverView } from './contract';
import { post } from './commands';
import { ActionButton, Disclosure, Facts, PageHeader, StatusPill } from './ui/shared';

/** How Sallyport is set up on this computer, and how to use it. */
export function Settings({ state, expanded, toggle, editorForm }: {
  state: ClientState; expanded: ReadonlySet<string>; toggle: (key: string) => void; editorForm: ReactNode;
}) {
  const settings = state.settings;
  const startupId = useId();
  return <section className="page settings" aria-label="Settings">
    <PageHeader title="Settings" />

    <section className="settings-group" aria-label="Packet routing">
      <h2>Packet routing</h2>
      <p className="secondary metadata">Routing a program’s every packet needs NT Kernel’s free Windows Packet Filter driver and the Sallyport service.</p>
      <RoutingCard routing={settings.routing} manage />
    </section>

    <section className="settings-group" aria-labelledby={startupId}>
      <h2 id={startupId}>Startup</h2>
      <label className="check-field card">
        <input type="checkbox" checked={settings.startup.on} disabled={!settings.startup.toggle.enabled}
          onChange={() => post('SET_START_WITH_WINDOWS', { enabled: !settings.startup.on })} />
        <span><span>{settings.startup.label}</span><span className="secondary metadata">{settings.startup.text}</span></span>
      </label>
    </section>

    <section className="settings-group" aria-label="Defaults for new apps">
      <h2>Defaults for new apps</h2>
      <p className="secondary metadata">What an app you add starts with. Apps added before keep their own.</p>
      <div className="card">
        <div className="row-head">
          <Facts facts={settings.defaults.facts} />
          <ActionButton action={settings.defaults.edit} focusKey="defaults" onClick={() => post('BEGIN_DEFAULTS_EDIT', {})} />
        </div>
        {editorForm}
      </div>
    </section>

    <section className="settings-group" aria-label="How to use">
      <h2>How to use</h2>
      <Guide guide={settings.guide} manualOpen={expanded.has('manual')} toggleManual={() => toggle('manual')} />
    </section>

    <section className="settings-group" aria-label="About">
      <h2>About</h2>
      <div className="card"><Facts facts={settings.about} /></div>
    </section>
  </section>;
}

/** The driver and the service as they are; with `manage`, installing and removing the driver too. */
export function RoutingCard({ routing, manage, openSettings }: { routing: DriverView; manage: boolean; openSettings?: () => void }) {
  return <div className={`card driver tone-border-${routing.tone}`}>
    <div className="row-head">
      <div className="row-identity">
        <StatusPill tone={routing.tone} announce={manage}>{routing.statusText}</StatusPill>
        {routing.detailText && <p className="supporting secondary">{routing.detailText}</p>}
      </div>
      <div className="actions row-primary">
        {manage ? <>
          <ActionButton action={routing.install} focusKey="driver" primary onClick={() => post('REQUEST_DRIVER_INSTALL', {})} />
          <ActionButton action={routing.remove} focusKey="driver" onClick={() => post('REQUEST_DRIVER_REMOVE', {})} />
        </> : routing.phase !== 'ready' && openSettings && <button type="button" onClick={openSettings}>Open Settings</button>}
      </div>
    </div>
    <Facts facts={routing.facts} />
  </div>;
}

function Guide({ guide, manualOpen, toggleManual }: {
  guide: ClientState['settings']['guide']; manualOpen: boolean; toggleManual: () => void;
}) {
  const manualId = useId();
  return <div className="guide">
    <ol className="steps">
      <li><h3>Import a profile.</h3><p>Choose the OpenVPN profile supplied by your VPN provider (Profiles).</p></li>
      <li><h3>Add an app.</h3><p>A game or program by its folder, routed by its packets; or a browser or proxy-aware program, launched with proxy settings (Apps).</p></li>
      <li><h3>Run or launch it.</h3><p>Sallyport connects the profile. Everything else on this computer keeps its own connection. Network shows what goes through it.</p></li>
    </ol>
    <div className="manual-setup">
      <Disclosure open={manualOpen} controls={manualId} onClick={toggleManual}>Manual proxy setup</Disclosure>
      <div id={manualId} hidden={!manualOpen} className="manual-content">
        <p>Choose SOCKS5, host 127.0.0.1, the profile’s port, and remote DNS where available.</p>
        <p><code>{guide.endpointText}</code></p>
        <pre>{guide.curlExample}</pre>
        <ActionButton action={guide.copyExample} onClick={() => post('COPY_GUIDE_EXAMPLE', {})} />
      </div>
    </div>
    <aside className="guide-notes">
      <p>Programs routed by their packets go through the VPN from their first packet when routed before they start, TCP and UDP alike. Their IPv6 and their traffic to this computer’s own network are blocked, not routed. Programs launched with proxy settings carry TCP only.</p>
      <p>Closing this window keeps connections and routing running in the notification area. Choose ‘Exit Sallyport’ there to stop routing, disconnect and quit.</p>
    </aside>
  </div>;
}
