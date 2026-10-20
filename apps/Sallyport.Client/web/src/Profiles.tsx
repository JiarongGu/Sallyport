import { t } from './i18n';
import { useId, type ReactNode } from 'react';
import type { ClientState, ProfileView } from './contract';
import { post } from './commands';
import { clock, countdown, duration } from './format';
import { ActionButton, Disclosure, EmptyView, Facts, StatusPill, Switch } from './ui/shared';

export function Profiles({ state, now, expanded, toggle }: {
  state: ClientState; now: number; expanded: ReadonlySet<string>; toggle: (key: string) => void;
}) {
  return <section className="page" aria-label={t('Profiles')}>
    <p className="page-intro secondary">{t('Your VPN connections, from the .ovpn files you import. Connect one here or on Overview; the apps you put in it go through it.')}</p>
    {state.profilesEmpty && <EmptyView empty={state.profilesEmpty} actions={state.actions} />}
    <div className="rows">
      {state.profiles.map((profile) => <ProfileRow key={profile.id} profile={profile} now={now}
        expanded={expanded.has(`profile:${profile.id}`)} toggle={() => toggle(`profile:${profile.id}`)} />)}
    </div>
  </section>;
}

/** One profile's connection settings over Settings' — the advanced case, so a view of its own rather than a form in
 *  its card. Leaving it cancels the edit. */
export function ProfileSettings({ name, editorForm }: { name: string; editorForm: ReactNode }) {
  return <section className="page" aria-label={t('Connection settings for {0}', name)}>
    <p className="page-intro secondary">{t('Only for {0}, over the choices in Settings. Anything left as it is follows Settings and the profile.', name)}</p>
    <div className="card own-settings">{editorForm}</div>
  </section>;
}

function ProfileRow({ profile, now, expanded, toggle }: {
  profile: ProfileView; now: number; expanded: boolean; toggle: () => void;
}) {
  const detailsId = useId();
  const headingId = useId();
  return <article className={`card tone-border-${profile.tone}${expanded ? ' expanded' : ''}`} aria-labelledby={headingId}>
    <div className="row-head">
      {/* On or off at once, as Connect's profile list has it. */}
      <Switch on={profile.actions.disconnect.visible} action={profile.actions.disconnect.visible ? profile.actions.disconnect : profile.actions.connect}
        onFlip={() => post(profile.actions.disconnect.visible ? 'DISCONNECT' : 'CONNECT', { id: profile.id })} />
      <div className="row-identity">
        <h2 id={headingId}>{profile.name}</h2>
        <div className="profile-status">
          <StatusPill tone={profile.tone} announce>{profile.statusText}</StatusPill>
          {profile.connectedSince && <span className="duration">{t('Connected for {0}', duration(profile.connectedSince, now))}</span>}
        </div>
        {/* The proxy for other programs, once it has a port: nothing to say of it before. */}
        {profile.port !== null && <span className="endpoint">
          <span className="metadata secondary">{t('Proxy for other programs')}</span>
          <code>{profile.endpointText}</code>
          <ActionButton action={profile.actions.copy} icon="copy" onClick={() => post('COPY_ENDPOINT', { id: profile.id })} />
        </span>}
      </div>
      <Disclosure open={expanded} controls={detailsId} onClick={toggle} icon />
    </div>
    {profile.detailText && <p className="supporting secondary">{profile.detailText}</p>}
    {countdown(profile.nextAttemptAt, now) && <p className="supporting secondary countdown" role="timer">{countdown(profile.nextAttemptAt, now)}</p>}
    <div id={detailsId} hidden={!expanded} className="row-details">
      <Facts facts={profile.facts} />
      <div className="actions">
        <ActionButton action={profile.actions.settings} focusKey={`policy:${profile.id}`} onClick={() => post('BEGIN_POLICY_EDIT', { id: profile.id })} />
      </div>
      <LocationResult profile={profile} />
      <div className="details-footer">
        <ActionButton action={profile.actions.remove} focusKey={`remove:${profile.id}`} danger onClick={() => post('REQUEST_REMOVE_PROFILE', { id: profile.id })} />
      </div>
    </div>
  </article>;
}

function LocationResult({ profile }: { profile: ProfileView }) {
  const location = profile.location;
  return <section className="location" aria-label={t('Exit location')}>
    {location.previousResult && <p className="metadata secondary">{t('Previous result')}</p>}
    <p className={location.phase === 'unavailable' ? 'location-unavailable' : ''} role="status">{location.text}</p>
    <Facts facts={location.facts} />
    {location.checkedAt && <p className="metadata secondary">{t('Checked')} <time dateTime={location.checkedAt}>{clock(location.checkedAt)}</time></p>}
    <ActionButton action={profile.actions.locate} onClick={() => post('LOCATE', { id: profile.id })} />
  </section>;
}
