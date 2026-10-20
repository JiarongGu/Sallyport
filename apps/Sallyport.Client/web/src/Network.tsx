import { t } from './i18n';
import { useId } from 'react';
import type { AppItemView, ChannelView, ClientState, Page, ProfileView, TrafficView } from './contract';
import { post } from './commands';
import { clock, countdown, duration } from './format';
import { RunDetails } from './Run';
import { ActionButton, EmptyView, Facts, Icon, Monogram, StatusPill, Switch } from './ui/shared';
import { RoutingCard } from './Settings';
import { featuredProfile } from './ui/Chrome';

/**
 * What is connected and what goes through it, as OpenVPN Connect shows a connection: one profile first, with its
 * switch, its time, its traffic and its round trips, the apps that go through it beneath; the others after it.
 */
export function Network({ state, now, go, featured, feature }: {
  state: ClientState; now: number; go: (page: Page) => void; featured: string | null; feature: (id: string) => void;
}) {
  const network = state.network;
  const profiles = state.profiles;
  const first = featuredProfile(state, featured);
  const others = network.channels.filter((c) => c.profileId !== first?.id);
  return <section className="page network" aria-label={t('Overview')}>
    <p className="page-intro secondary">{t('What is connected, and what goes through it now.')}</p>
    {!first ? state.profilesEmpty && <EmptyView empty={state.profilesEmpty} actions={state.actions} /> :
      <Hero profile={first} channel={network.channels.find((c) => c.profileId === first.id) ?? null} profiles={profiles}
        apps={state.apps.filter((a) => a.profileId === first.id)} now={now} feature={feature} go={go} />}
    {others.length > 0 && <section className="also" aria-labelledby="also-connected">
      <h2 id="also-connected">{t('Also connected')}</h2>
      <div className="rows">{others.map((channel) => <OtherChannel key={channel.id} channel={channel} now={now} feature={feature} />)}</div>
    </section>}
    {/* Said here only when it needs doing something: all is well is Settings' to show. */}
    {network.routing.phase !== 'ready' && <section className="network-routing" aria-label={t('App VPN on this computer')}>
      <h2>{t('App VPN on this computer')}</h2>
      <RoutingCard routing={network.routing} manage={false} openSettings={() => go('settings')} />
    </section>}
  </section>;
}

function Hero({ profile, channel, profiles, apps, now, feature, go }: {
  profile: ProfileView; channel: ChannelView | null; profiles: ProfileView[]; apps: AppItemView[]; now: number;
  feature: (id: string) => void; go: (page: Page) => void;
}) {
  const pickerId = useId();
  const on = profile.actions.disconnect.visible;
  const since = profile.connectedSince ?? channel?.connectedSince;
  const wait = countdown(profile.nextAttemptAt ?? channel?.nextAttemptAt ?? null, now);
  return <article className={`hero hero-${profile.tone}`} aria-label={profile.name}>
    <div className="hero-head">
      <Monogram name={profile.name} />
      <div className="hero-identity">
        {profiles.length > 1
          ? <><label className="sr-only" htmlFor={pickerId}>{t('Profile')}</label>
            <select id={pickerId} className="profile-picker" value={profile.id} onChange={(event) => feature(event.target.value)}>
              {profiles.map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}
            </select></>
          : <h2>{profile.name}</h2>}
        <p className={`hero-status tone-text-${profile.tone}`} role="status">{profile.statusText}</p>
        {on || since ? <p className="secondary metadata">{[since && t('Connected for {0}', duration(since, now)), profile.port !== null && channel?.proxyText]
          .filter(Boolean).join(' · ')}</p>
          : apps.length > 0 && <p className="secondary metadata">{t('Turn on an app below and this connects by itself.')}</p>}
      </div>
      <Switch large on={on} action={on ? profile.actions.disconnect : profile.actions.connect}
        onFlip={() => post(on ? 'DISCONNECT' : 'CONNECT', { id: profile.id })} />
    </div>
    {(channel?.detailText ?? profile.detailText) && <p className="supporting secondary">{channel?.detailText ?? profile.detailText}</p>}
    {wait && <p className="supporting secondary countdown" role="timer">{wait}</p>}

    {channel?.traffic && <Traffic traffic={channel.traffic} />}

    {channel?.latency && <section className="latency" aria-label={t('Latency')}>
      <div className="latency-head">
        <p className={channel.latency.phase === 'unavailable' ? 'location-unavailable' : 'secondary'} role="status">{channel.latency.statusText}</p>
        <ActionButton action={channel.actions.checkLatency} glyph="refresh" onClick={() => post('CHECK_LATENCY', { id: channel.profileId })} />
      </div>
      <Facts facts={channel.latency.paths} figures />
      {channel.latency.checkedAt && <p className="metadata secondary">{t('Checked')} <time dateTime={channel.latency.checkedAt}>{clock(channel.latency.checkedAt)}</time></p>}
    </section>}

    {channel && channel.facts.length > 0 && <Facts facts={channel.facts} />}

    {channel?.selections.map((selection) => <section key={selection.id} className="selection" aria-label={selection.name}>
      <div className="row-head">
        <div className="row-identity">
          <h3>{selection.name}</h3>
          <div className="profile-status">
            <StatusPill tone={selection.run.tone}>{selection.run.statusText}</StatusPill>
            {selection.run.since && <span className="duration">{t('Routed for {0}', duration(selection.run.since, now))}</span>}
          </div>
        </div>
        <ActionButton action={selection.stop} onClick={() => post('STOP_RUN', {})} />
      </div>
      <RunDetails run={selection.run} />
    </section>)}

    {/* The apps through this profile not being routed now, to route or launch from here. */}
    {apps.some((a) => !channel?.selections.some((s) => s.id === a.id)) && <section className="hero-apps" aria-label={t('Apps')}>
      <div className="hero-apps-head"><h3>{t('Apps through it')}</h3><button type="button" onClick={() => go('apps')}>{t('Open Apps')}</button></div>
      <ul>{apps.filter((a) => !channel?.selections.some((s) => s.id === a.id)).map((app) => <li key={app.id}>
        <Monogram name={app.name} />
        <span className="app-line"><span className="app-name">{app.name}</span><StatusPill tone={app.tone}>{app.statusText}</StatusPill></span>
        <ActionButton action={app.actions.launch} onClick={() => post('LAUNCH_APP', { id: app.id })} />
        {(app.actions.stop.visible || app.actions.route.visible) && <span className="switch-caption" aria-hidden="true">{t('Use VPN')}</span>}
        <Switch on={app.actions.stop.visible} action={app.actions.stop.visible ? app.actions.stop : app.actions.route}
          onFlip={() => app.actions.stop.visible ? post('STOP_RUN', {}) : post('ROUTE_APP', { id: app.id })} />
      </li>)}</ul>
    </section>}
  </article>;
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

function OtherChannel({ channel, now, feature }: { channel: ChannelView; now: number; feature: (id: string) => void }) {
  return <article className={`card channel tone-border-${channel.tone}`} aria-label={channel.name}>
    <div className="row-head">
      <Monogram name={channel.name} />
      <div className="row-identity">
        <h3>{channel.name}</h3>
        <div className="profile-status">
          <StatusPill tone={channel.tone} announce>{channel.statusText}</StatusPill>
          {channel.connectedSince && <span className="duration">{t('Connected for {0}', duration(channel.connectedSince, now))}</span>}
          {channel.traffic && <span className="summary">↓ {channel.traffic.received.figure} · ↑ {channel.traffic.sent.figure}</span>}
        </div>
      </div>
      <button type="button" className="subtle" onClick={() => feature(channel.profileId)}>{t('Show')}</button>
      <Switch on action={channel.actions.disconnect} onFlip={() => post('DISCONNECT', { id: channel.profileId })} />
    </div>
  </article>;
}
