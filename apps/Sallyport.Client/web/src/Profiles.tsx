import { t } from './i18n';
import { useId, type ReactNode } from 'react';
import type { ClientState, ProfileView } from './contract';
import { post } from './commands';
import { clock, countdown, duration } from './format';
import { ActionButton, EmptyView, Facts, StatusPill } from './ui/shared';

export function Profiles({ state, now }: { state: ClientState; now: number }) {
  return <section className="page" aria-label={t('Profiles')}>
    <p className="page-intro secondary">{t('Your VPN connections, from the .ovpn files you import, and how each one connects. Connect them on Overview.')}</p>
    {state.profilesEmpty && <EmptyView empty={state.profilesEmpty} actions={state.actions} />}
    <div className="rows">
      {state.profiles.map((profile) => <ProfileRow key={profile.id} profile={profile} now={now} />)}
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

/** One profile: its name and state, how it connects and its two buttons, nothing folded (the owner: "remove the
 *  dropdown for this and display some detail in the row"). Connecting is Overview's and the tray's: "we kind dont need
 *  the connect in profile page anymore". */
function ProfileRow({ profile, now }: { profile: ProfileView; now: number }) {
  const headingId = useId();
  return <article className={`card tone-border-${profile.tone}`} aria-labelledby={headingId}>
    <div className="row-head">
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
      <div className="actions">
        <ActionButton action={profile.actions.settings} focusKey={`policy:${profile.id}`} onClick={() => post('BEGIN_POLICY_EDIT', { id: profile.id })} />
        <ActionButton action={profile.actions.remove} focusKey={`remove:${profile.id}`} danger onClick={() => post('REQUEST_REMOVE_PROFILE', { id: profile.id })} />
      </div>
    </div>
    {profile.detailText && <p className="supporting secondary">{profile.detailText}</p>}
    {countdown(profile.nextAttemptAt, now) && <p className="supporting secondary countdown" role="timer">{countdown(profile.nextAttemptAt, now)}</p>}
    {profile.details && <div className="profile-details"><Facts facts={profile.details} /></div>}
    {/* Where it comes out, once there is something to check: nothing said of it while it is not connected. */}
    {(profile.actions.locate.visible || profile.location.previousResult) && <LocationResult profile={profile} />}
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
