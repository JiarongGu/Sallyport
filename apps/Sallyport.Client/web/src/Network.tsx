import { t } from './i18n';
import { useId, useState } from 'react';
import type { AppSectionView, ClientState, Page, SectionView, TrafficView } from './contract';
import { post } from './commands';
import { clock, countdown, duration } from './format';
import { RunDetails } from './Run';
import { ActionButton, AppIcon, Disclosure, EmptyView, Facts, Icon, Monogram, StatusPill, Switch } from './ui/shared';
import { RoutingCard } from './Settings';

/**
 * Each VPN with the apps that go through it (D24), as OpenVPN Connect shows a connection: a profile's section with its
 * switch, its time, its traffic and its round trips, and beneath it the apps in it, each with a switch of its own. An app
 * may be in several sections; a profile holds several apps.
 */
export function Network({ state, now, go }: { state: ClientState; now: number; go: (page: Page) => void }) {
  const network = state.network;
  return <section className="page network" aria-label={t('Overview')}>
    <p className="page-intro secondary">{t('Each VPN and the apps that go through it. Connect one, and the apps switched on in it go through it; everything else keeps your own connection.')}</p>
    {network.sections.length === 0 ? state.profilesEmpty && <EmptyView empty={state.profilesEmpty} actions={state.actions} />
      : <div className="sections">{network.sections.map((section) => <Section key={section.profile.id} section={section} now={now} go={go} />)}</div>}
    {/* Said here only when it needs doing something: all is well is Settings' to show. */}
    {network.routing.phase !== 'ready' && <section className="network-routing" aria-label={t('App VPN on this computer')}>
      <h2>{t('App VPN on this computer')}</h2>
      <RoutingCard routing={network.routing} manage={false} openSettings={() => go('settings')} />
    </section>}
  </section>;
}

function Section({ section, now, go }: { section: SectionView; now: number; go: (page: Page) => void }) {
  const profile = section.profile;
  const channel = section.channel;
  const detailsId = useId();
  const appsId = useId();
  const [detailsOpen, setDetailsOpen] = useState(false);
  const on = profile.actions.disconnect.visible;
  const since = profile.connectedSince ?? channel?.connectedSince;
  const wait = countdown(profile.nextAttemptAt ?? channel?.nextAttemptAt ?? null, now);
  return <article className={`hero hero-${profile.tone}`} aria-label={profile.name}>
    <div className="hero-head">
      <Monogram name={profile.name} />
      <div className="hero-identity">
        <h2>{profile.name}</h2>
        <p className={`hero-status tone-text-${profile.tone}`} role="status">{profile.statusText}</p>
        {since ? <p className="secondary metadata">{t('Connected for {0}', duration(since, now))}</p>
          : !on && section.apps.some((a) => a.on) && <p className="secondary metadata">{t('Connect, and the apps switched on below go through it.')}</p>}
      </div>
      <Switch large on={on} action={on ? profile.actions.disconnect : profile.actions.connect}
        onFlip={() => post(on ? 'DISCONNECT' : 'CONNECT', { id: profile.id })} />
    </div>
    {(channel?.detailText ?? profile.detailText) && <p className="supporting secondary">{channel?.detailText ?? profile.detailText}</p>}
    {wait && <p className="supporting secondary countdown" role="timer">{wait}</p>}

    {channel?.traffic && <Traffic traffic={channel.traffic} />}

    {channel?.latency && <section className="latency" aria-label={t('Ping')}>
      <div className="latency-head">
        <p className={channel.latency.phase === 'unavailable' ? 'location-unavailable' : 'secondary'} role="status">{channel.latency.statusText}</p>
        <ActionButton action={channel.actions.checkLatency} glyph="refresh" onClick={() => post('CHECK_LATENCY', { id: channel.profileId })} />
      </div>
      <Facts facts={channel.latency.paths} figures />
      {channel.latency.checkedAt && <p className="metadata secondary">{t('Checked')} <time dateTime={channel.latency.checkedAt}>{clock(channel.latency.checkedAt)}</time></p>}
    </section>}

    {/* How it is connected, for those who look: folded, so the section says what a person needs at a glance. */}
    {channel && (channel.facts.length > 0 || channel.proxyText) && <div className="hero-details">
      <Disclosure open={detailsOpen} controls={detailsId} onClick={() => setDetailsOpen(!detailsOpen)}>{t('Details')}</Disclosure>
      <div id={detailsId} hidden={!detailsOpen}>
        {channel.proxyText && <p className="secondary metadata">{channel.proxyText}</p>}
        <Facts facts={channel.facts} />
      </div>
    </div>}

    {/* Its apps, each with its own switch here; the apps not in it, to put in. */}
    <section className="hero-apps" aria-labelledby={appsId}>
      <div className="hero-apps-head">
        <h3 id={appsId}>{t('Apps through {0}', profile.name)}</h3>
        {section.add.visible && <select className="add-to-section" aria-label={section.add.label} value=""
          onChange={(event) => event.target.value && post('ADD_APP_TO_PROFILE', { id: event.target.value, profileId: profile.id })}>
          <option value="">{section.add.label}</option>
          {section.addable.map((choice) => <option key={choice.id} value={choice.id}>{choice.label}</option>)}
        </select>}
      </div>
      {section.hintText && <p className="secondary metadata section-hint">{section.hintText}
        {section.addable.length === 0 && <> <button type="button" className="link" onClick={() => go('apps')}>{t('Open Apps')}</button></>}</p>}
      {section.apps.length > 0 && <ul>{section.apps.map((app) => <SectionApp key={app.appId} app={app} now={now} />)}</ul>}
    </section>
  </article>;
}

/** An app in a section: its icon and name, what it is doing through this profile, its switch here, and its route's
 *  detail while this profile carries it. */
function SectionApp({ app, now }: { app: AppSectionView; now: number }) {
  return <li className={app.run ? 'carried' : undefined}>
    <div className="section-app">
      <AppIcon name={app.name} url={app.iconUrl} />
      <span className="app-line">
        <span className="app-name">{app.name}</span>
        <StatusPill tone={app.tone}>{app.statusText}</StatusPill>
        {app.run?.since && <span className="duration">{t('Through the VPN for {0}', duration(app.run.since, now))}</span>}
      </span>
      <ActionButton action={app.launch} onClick={() => post('LAUNCH_APP', { id: app.appId, profileId: app.profileId })} />
      {app.vpn.visible && <span className="switch-caption" aria-hidden="true">{t('Use VPN')}</span>}
      <Switch on={app.on} action={app.vpn} onFlip={() => post('SET_APP_VPN', { id: app.appId, profileId: app.profileId, on: !app.on })} />
      {app.remove.visible && <button type="button" className="subtle icon remove-from-section" aria-label={app.remove.label} title={app.remove.label}
        onClick={() => post('REMOVE_APP_FROM_PROFILE', { id: app.appId, profileId: app.profileId })}><Icon name="dismiss" /></button>}
    </div>
    {app.detailText && !app.run && <p className="metadata secondary section-app-detail">{app.detailText}</p>}
    {app.run && <RunDetails run={app.run} counts={false} />}
  </li>;
}

/** Received and sent, as figures with the rate now, over a minute's graph of the two. */
export function Traffic({ traffic }: { traffic: TrafficView }) {
  return <section className="traffic" aria-label={t('Traffic')}>
    <div className="traffic-figures">
      <TrafficFigure icon="down" label={traffic.received.label} figure={traffic.received.figure} rate={traffic.received.value} kind="in" />
      <TrafficFigure icon="up" label={traffic.sent.label} figure={traffic.sent.figure} rate={traffic.sent.value} kind="out" />
    </div>
    <Graph inRates={traffic.inPerSecond} outRates={traffic.outPerSecond} />
  </section>;
}

function TrafficFigure({ icon, label, figure, rate, kind }: { icon: 'down' | 'up'; label: string; figure?: string | null; rate: string; kind: 'in' | 'out' }) {
  return <div className={`traffic-figure ${kind}`}>
    <span className="traffic-label"><Icon name={icon} />{label}</span>
    <span className="figure">{figure}</span>
    <span className="metadata secondary">{rate}</span>
  </div>;
}

/** The two rates as areas over the last minute, the newest at the right; scaled to the larger, so its shape reads. */
function Graph({ inRates, outRates }: { inRates: number[]; outRates: number[] }) {
  const slots = 60;
  const top = Math.max(1, ...inRates, ...outRates);
  const line = (rates: number[]) => rates.slice(-slots).map((rate, i, all) => `${slots - all.length + i},${40 - (rate / top) * 36}`).join(' ');
  const area = (rates: number[]) => rates.length < 2 ? '' : `${slots - Math.min(rates.length, slots)},40 ${line(rates)} ${slots - 1},40`;
  return <svg className="graph" viewBox={`0 0 ${slots - 1} 40`} preserveAspectRatio="none" aria-hidden="true">
    {/* The minute's floor and its middle, so a graph of a few seconds still reads as one. */}
    <line className="grid" x1="0" y1="40" x2={slots - 1} y2="40" /><line className="grid faint" x1="0" y1="22" x2={slots - 1} y2="22" />
    {inRates.length > 1 && <><polygon className="area in" points={area(inRates)} /><polyline className="line in" points={line(inRates)} /></>}
    {outRates.length > 1 && <><polygon className="area out" points={area(outRates)} /><polyline className="line out" points={line(outRates)} /></>}
  </svg>;
}
