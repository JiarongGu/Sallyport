import { useId, type ReactNode } from 'react';
import type { ClientState, DriverView } from './contract';
import { post } from './commands';
import { t } from './i18n';
import { ActionButton, Disclosure, Facts, StatusPill } from './ui/shared';
import { HelpButton, HelpHeading } from './ui/Help';

/** How Sallyport is set up on this computer, and how to use it: each group its heading and its controls, what it is for
 *  behind the "?" beside the heading (the owner: "a proper helper modal instead putting all in the display"). */
export function Settings({ state, expanded, toggle, editorForm, moveForm }: {
  state: ClientState; expanded: ReadonlySet<string>; toggle: (key: string) => void; editorForm: ReactNode; moveForm: ReactNode;
}) {
  const settings = state.settings;
  const generalId = useId();
  const languageId = useId();
  const connectionId = useId();
  const placesId = useId();
  const protocolId = useId();
  const retryId = useId();
  const connection = settings.connection;
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

    <section className="settings-group" aria-labelledby={connectionId}>
      <HelpHeading id={connectionId} title={t('Connection')}>
        <p>{t('For every profile, from its next connection. A profile can be changed on its own under Profiles.')}</p>
        <h3>{t('VPN protocol')}</h3>
        <p>{t('How Sallyport talks to your VPN server; your apps’ traffic goes through it either way.')}</p>
        <dl>{connection.protocol.choices.map((choice) => <div key={choice.id}><dt>{choice.label}</dt><dd>{choice.help}</dd></div>)}</dl>
        <h3>{t('If it cannot connect')}</h3>
        <p>{t('A round tries every server of the profile once, then waits a little longer each time.')}</p>
      </HelpHeading>
      <div className="card setting-rows">
        <div className="setting-row">
          <span className="setting-text"><span id={protocolId}>{t('VPN protocol')}</span></span>
          <select className="setting-choice" aria-labelledby={protocolId} value={connection.protocol.setting}
            onChange={(event) => post('SET_PROTOCOL', { protocol: event.target.value })}>
            {connection.protocol.choices.map((choice) => <option key={choice.id} value={choice.id}>{choice.label}</option>)}
          </select>
        </div>
        <div className="setting-row">
          <span className="setting-text"><span id={retryId}>{t('If it cannot connect')}</span></span>
          <select className="setting-choice" aria-labelledby={retryId} value={connection.retry.setting}
            onChange={(event) => post('SET_RETRY', { rounds: Number(event.target.value) })}>
            {connection.retry.choices.map((choice) => <option key={choice.id} value={choice.id}>{choice.label}</option>)}
          </select>
        </div>
      </div>
    </section>

    <section className="settings-group" aria-label={t('Driver')}>
      <HelpHeading title={t('Driver')}>
        <p>{t('Sending a whole app through your VPN needs a free driver (Windows Packet Filter, by NT Kernel) and the Sallyport service.')}</p>
        <p>{t('Without it, a profile still works as a SOCKS5 proxy on this computer, for the programs set to use one.')}</p>
      </HelpHeading>
      <RoutingCard routing={settings.routing} manage />
    </section>

    <section className="settings-group" aria-label={t('Defaults for new apps')}>
      <HelpHeading title={t('Defaults for new apps')}>
        <p>{t('What an app you add starts with. Apps added before keep their own.')}</p>
      </HelpHeading>
      <div className="card">
        <div className="row-head">
          <Facts facts={settings.defaults.facts} />
          <ActionButton action={settings.defaults.edit} focusKey="defaults" onClick={() => post('BEGIN_DEFAULTS_EDIT', {})} />
        </div>
        {editorForm}
      </div>
    </section>

    {settings.move && <section className="settings-group" aria-label={t('Move to another computer')}>
      <HelpHeading title={t('Move to another computer')}><MoveHelp /></HelpHeading>
      <div className="card">
        <div className="row-head move-row">
          <span className="secondary">{t('Your VPN profiles, apps and settings in one file, for here or another computer.')}</span>
          {(settings.move.export.visible || settings.move.import.visible) && <div className="actions">
            <ActionButton action={settings.move.export} focusKey="export" onClick={() => post('BEGIN_EXPORT', {})} />
            <ActionButton action={settings.move.import} focusKey="import" onClick={() => post('BEGIN_IMPORT', {})} />
          </div>}
        </div>
        {moveForm}
      </div>
    </section>}

    <section className="settings-group" aria-label={t('About')}>
      <h2>{t('About')}</h2>
      <div className="card about">
        {/* How to use is help, not a group of its own (the owner: "why you need a sperated session for 使用方法?"). */}
        <div className="row-head">
          <Facts facts={settings.about} />
          <div className="actions">
            <HelpButton title={t('How to use')} label={t('How to use…')}>
              <Guide guide={settings.guide} manualOpen={expanded.has('manual')} toggleManual={() => toggle('manual')} />
            </HelpButton>
          </div>
        </div>
        {settings.uninstall?.visible && <div className="row-head uninstall">
          <p className="secondary">{t('Removes Sallyport from this computer. You choose whether your profiles, apps and settings stay.')}</p>
          <div className="actions"><ActionButton action={settings.uninstall} danger onClick={() => post('UNINSTALL', {})} /></div>
        </div>}
        {settings.places && <div className="manual-setup">
          <Disclosure open={expanded.has('places')} controls={placesId} onClick={() => toggle('places')}>{t('Technical details')}</Disclosure>
          <div id={placesId} hidden={!expanded.has('places')} className="manual-content">
            {settings.places.map((fact) => <div key={fact.label} className="wide-fact"><span className="secondary metadata">{fact.label}</span><span className="program-path">{fact.value}</span></div>)}
          </div>
        </div>}
      </div>
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

/** Export and Import, all of it: what each part of the file holds, the password, and taking it in on another computer. */
function MoveHelp() {
  return <>
    <p>{t('Export your VPN profiles, apps and connection settings to one file, and import them here or on another computer.')}</p>
    <h3>{t('What goes in the file')}</h3>
    <dl>
      <div><dt>{t('VPN profiles')}</dt><dd>{t('Their keys included; the .ovpn files themselves are not needed.')}</dd></div>
      <div><dt>{t('Apps')}</dt><dd>{t('Their programs, switches and the VPNs they go through. The games themselves are not copied.')}</dd></div>
      <div><dt>{t('Connection settings')}</dt><dd>{t('VPN protocol, retries, each profile’s own changes, and the defaults for new apps.')}</dd></div>
    </dl>
    <h3>{t('Password')}</h3>
    <p>{t('With a password, the profiles in the file open only with that password; without one, anyone who has the file can use them. Keep the password: it cannot be recovered.')}</p>
    <h3>{t('Import')}</h3>
    <p>{t('Choose the file, then what to take from it. An app installed somewhere else on this computer says so on its page: choose Locate… there and show Sallyport where it is.')}</p>
  </>;
}

function Guide({ guide, manualOpen, toggleManual }: {
  guide: ClientState['settings']['guide']; manualOpen: boolean; toggleManual: () => void;
}) {
  const manualId = useId();
  return <div className="guide">
    <ol className="steps">
      <li><h3>{t('Import your VPN profile.')}</h3><p>{t('The .ovpn file you were given: Profiles, then Import profile.')}</p></li>
      <li><h3>{t('Add your game or program.')}</h3><p>{t('Apps, then Add an app: choose its folder or its .exe.')}</p></li>
      <li><h3>{t('Put it in a VPN and connect.')}</h3><p>{t('On the Overview, add it to a VPN, turn on Use VPN, and connect. Start it as you always do: only what is switched on goes through your VPN.')}</p></li>
    </ol>
    <div className="manual-setup">
      <Disclosure open={manualOpen} controls={manualId} onClick={toggleManual}>{t('For experts')}</Disclosure>
      <div id={manualId} hidden={!manualOpen} className="manual-content">
        <p>{t('Programs routed by their packets go through the VPN from their first packet when routed before they start, TCP and UDP alike. Their IPv6 and their traffic to this computer’s own network are blocked, not routed. Programs launched with proxy settings carry TCP only.')}</p>
        <h3>{t('Manual proxy setup')}</h3>
        <p>{t('Choose SOCKS5, host 127.0.0.1, the profile’s port, and remote DNS where available.')}</p>
        <p><code>{guide.endpointText}</code></p>
        <pre>{guide.curlExample}</pre>
        <ActionButton action={guide.copyExample} onClick={() => post('COPY_GUIDE_EXAMPLE', {})} />
      </div>
    </div>
    <aside className="guide-notes">
      <p>{t('Closing this window keeps Sallyport running beside the clock, and what is connected stays connected. To stop everything, right-click its icon there and choose Exit Sallyport.')}</p>
    </aside>
  </div>;
}
