import { useId, type ReactNode } from 'react';
import type { ClientState, DriverView } from './contract';
import { post } from './commands';
import { t } from './i18n';
import { ActionButton, Disclosure, Facts, StatusPill } from './ui/shared';

/** How Sallyport is set up on this computer, and how to use it. */
export function Settings({ state, expanded, toggle, editorForm }: {
  state: ClientState; expanded: ReadonlySet<string>; toggle: (key: string) => void; editorForm: ReactNode;
}) {
  const settings = state.settings;
  const generalId = useId();
  const languageId = useId();
  return <section className="page settings" aria-label={t('Settings')}>
    {/* Settings that act the moment they change, as rows: what it is on the left, its control on the right. */}
    <section className="settings-group" aria-labelledby={generalId}>
      <h2 id={generalId}>{t('General')}</h2>
      <div className="card setting-rows">
        <div className="setting-row">
          <span className="setting-text">
            <span id={languageId}>{t('Language')}</span>
            <span>{t('The words of this window, the tray and setup.')}</span>
          </span>
          <select className="language-choice" aria-labelledby={languageId} value={settings.language.setting}
            onChange={(event) => post('SET_LANGUAGE', { language: event.target.value })}>
            {settings.language.choices.map((choice) => <option key={choice.id} value={choice.id}>{choice.label}</option>)}
          </select>
        </div>
        <label className="setting-row">
          <span className="setting-text"><span>{settings.startup.label}</span><span>{settings.startup.text}</span></span>
          <input type="checkbox" role="switch" className="switch" checked={settings.startup.on} disabled={!settings.startup.toggle.enabled}
            onChange={() => post('SET_START_WITH_WINDOWS', { enabled: !settings.startup.on })} />
        </label>
      </div>
    </section>

    <section className="settings-group" aria-label={t('Packet routing')}>
      <h2>{t('Packet routing')}</h2>
      <p className="secondary">{t('Routing a program’s every packet needs NT Kernel’s free Windows Packet Filter driver and the Sallyport service.')}</p>
      <RoutingCard routing={settings.routing} manage />
    </section>

    <section className="settings-group" aria-label={t('Defaults for new apps')}>
      <h2>{t('Defaults for new apps')}</h2>
      <p className="secondary">{t('What an app you add starts with. Apps added before keep their own.')}</p>
      <div className="card">
        <div className="row-head">
          <Facts facts={settings.defaults.facts} />
          <ActionButton action={settings.defaults.edit} focusKey="defaults" onClick={() => post('BEGIN_DEFAULTS_EDIT', {})} />
        </div>
        {editorForm}
      </div>
    </section>

    <section className="settings-group" aria-label={t('How to use')}>
      <h2>{t('How to use')}</h2>
      <Guide guide={settings.guide} manualOpen={expanded.has('manual')} toggleManual={() => toggle('manual')} />
    </section>

    <section className="settings-group" aria-label={t('About')}>
      <h2>{t('About')}</h2>
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
        </> : routing.phase !== 'ready' && openSettings && <button type="button" onClick={openSettings}>{t('Open Settings')}</button>}
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
      <li><h3>{t('Import a profile.')}</h3><p>{t('Choose the OpenVPN profile supplied by your VPN provider (Profiles).')}</p></li>
      <li><h3>{t('Add an app.')}</h3><p>{t('A game or program by its folder, routed by its packets; or a browser or proxy-aware program, launched with proxy settings (Apps).')}</p></li>
      <li><h3>{t('Run or launch it.')}</h3><p>{t('Sallyport connects the profile. Everything else on this computer keeps its own connection. Network shows what goes through it.')}</p></li>
    </ol>
    <div className="manual-setup">
      <Disclosure open={manualOpen} controls={manualId} onClick={toggleManual}>{t('Manual proxy setup')}</Disclosure>
      <div id={manualId} hidden={!manualOpen} className="manual-content">
        <p>{t('Choose SOCKS5, host 127.0.0.1, the profile’s port, and remote DNS where available.')}</p>
        <p><code>{guide.endpointText}</code></p>
        <pre>{guide.curlExample}</pre>
        <ActionButton action={guide.copyExample} onClick={() => post('COPY_GUIDE_EXAMPLE', {})} />
      </div>
    </div>
    <aside className="guide-notes">
      <p>{t('Programs routed by their packets go through the VPN from their first packet when routed before they start, TCP and UDP alike. Their IPv6 and their traffic to this computer’s own network are blocked, not routed. Programs launched with proxy settings carry TCP only.')}</p>
      <p>{t('Closing this window keeps connections and routing running in the notification area. Choose ‘Exit Sallyport’ there to stop routing, disconnect and quit.')}</p>
    </aside>
  </div>;
}
