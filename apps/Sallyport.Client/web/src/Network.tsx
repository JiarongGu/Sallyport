import { t } from './i18n';
import { useId } from 'react';
import type { ChannelView, ClientState, Page } from './contract';
import { post } from './commands';
import { clock, countdown, duration } from './format';
import { RunDetails } from './Run';
import { ActionButton, Facts, PageHeader, StatusPill } from './ui/shared';
import { RoutingCard } from './Settings';

/** What is connected and what goes through it now: a card per channel, trouble first, then this computer's routing. */
export function Network({ state, now, go }: { state: ClientState; now: number; go: (page: Page) => void }) {
  const network = state.network;
  return <section className="page" aria-label={t('Network')}>
    <PageHeader title={t('Network')} subtitle={t('What is connected, and what goes through it now.')} />
    {!network.empty && <p className="network-summary"><StatusPill tone={network.tone}>{network.statusText}</StatusPill></p>}
    {network.empty && <div className="empty">
      <h2>{network.empty.title}</h2>
      <p className="secondary">{network.empty.text}</p>
      <div className="actions">
        <button type="button" onClick={() => go('apps')}>{t('Open Apps')}</button>
        <button type="button" onClick={() => go('profiles')}>{t('Open Profiles')}</button>
      </div>
    </div>}
    <div className="rows">
      {network.channels.map((channel) => <Channel key={channel.id} channel={channel} now={now} />)}
    </div>
    <section className="network-routing" aria-label={t('Routing on this computer')}>
      <h2>{t('Routing on this computer')}</h2>
      <RoutingCard routing={network.routing} manage={false} openSettings={() => go('settings')} />
    </section>
  </section>;
}

function Channel({ channel, now }: { channel: ChannelView; now: number }) {
  const headingId = useId();
  return <article className={`card channel tone-border-${channel.tone}`} aria-labelledby={headingId}>
    <div className="row-head">
      <div className="row-identity">
        <h3 id={headingId}>{channel.name}</h3>
        <div className="profile-status">
          <StatusPill tone={channel.tone} announce>{channel.statusText}</StatusPill>
          {channel.connectedSince && <span className="secondary duration">{t('Connected for {0}', duration(channel.connectedSince, now))}</span>}
        </div>
      </div>
      <div className="actions row-primary">
        <ActionButton action={channel.actions.disconnect} onClick={() => post('DISCONNECT', { id: channel.profileId })} />
      </div>
    </div>
    {channel.detailText && <p className="supporting secondary">{channel.detailText}</p>}
    {countdown(channel.nextAttemptAt, now) && <p className="supporting secondary countdown" role="timer">{countdown(channel.nextAttemptAt, now)}</p>}
    {channel.proxyText && <p className="channel-proxy secondary">{channel.proxyText}</p>}
    <Facts facts={channel.facts} />
    {channel.latency && <section className="latency" aria-label={t('Latency')}>
      <div className="latency-head">
        <p className={channel.latency.phase === 'unavailable' ? 'location-unavailable' : 'secondary'} role="status">{channel.latency.statusText}</p>
        <ActionButton action={channel.actions.checkLatency} onClick={() => post('CHECK_LATENCY', { id: channel.profileId })} />
      </div>
      <Facts facts={channel.latency.paths} />
      {channel.latency.checkedAt && <p className="metadata secondary">{t('Checked')} <time dateTime={channel.latency.checkedAt}>{clock(channel.latency.checkedAt)}</time></p>}
    </section>}
    {channel.selections.map((selection) => <section key={selection.id} className="selection" aria-label={selection.name}>
      <div className="row-head">
        <div className="row-identity">
          <h4>{selection.name}</h4>
          <div className="profile-status">
            <StatusPill tone={selection.run.tone}>{selection.run.statusText}</StatusPill>
            {selection.run.since && <span className="secondary duration">{t('Routed for {0}', duration(selection.run.since, now))}</span>}
          </div>
        </div>
        <ActionButton action={selection.stop} onClick={() => post('STOP_RUN', {})} />
      </div>
      <RunDetails run={selection.run} />
    </section>)}
  </article>;
}
