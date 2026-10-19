import { useId, type ReactNode } from 'react';
import type { ClientState, ProfileView } from './contract';
import { post } from './commands';
import { clock, duration } from './format';
import { ActionButton, Disclosure, EmptyView, Facts, PageHeader, StatusPill } from './ui/shared';

export function Profiles({ state, now, expanded, toggle, editorForm }: {
  state: ClientState; now: number; expanded: ReadonlySet<string>; toggle: (key: string) => void; editorForm: ReactNode;
}) {
  return <section className="page" aria-label="Profiles">
    <PageHeader title="Profiles" subtitle="Use an OpenVPN profile for a game, or connect it as a local SOCKS5 proxy for apps.">
      <ActionButton action={state.actions.import} primary onClick={() => post('IMPORT', {})} />
    </PageHeader>
    {state.profilesEmpty && <EmptyView empty={state.profilesEmpty} actions={state.actions} />}
    {editorForm && !state.profiles.some((profile) => profile.id === state.editor?.subjectId) && editorForm}
    <div className="rows">
      {state.profiles.map((profile) => <ProfileRow key={profile.id} profile={profile} now={now}
        expanded={expanded.has(`profile:${profile.id}`)} toggle={() => toggle(`profile:${profile.id}`)}
        editorForm={state.editor?.subjectId === profile.id ? editorForm : null} />)}
    </div>
  </section>;
}

function ProfileRow({ profile, now, expanded, toggle, editorForm }: {
  profile: ProfileView; now: number; expanded: boolean; toggle: () => void; editorForm: ReactNode;
}) {
  const detailsId = useId();
  const headingId = useId();
  return <article className={`card tone-border-${profile.tone}${expanded ? ' expanded' : ''}`} aria-labelledby={headingId}>
    <div className="row-head">
      <div className="row-identity">
        <h2 id={headingId}>{profile.name}</h2>
        <div className="profile-status">
          <StatusPill tone={profile.tone} announce>{profile.statusText}</StatusPill>
          {profile.connectedSince && <span className="secondary duration">Connected for {duration(profile.connectedSince, now)}</span>}
        </div>
      </div>
      <div className="actions row-primary">
        <ActionButton action={profile.actions.connect} primary onClick={() => post('CONNECT', { id: profile.id })} />
        <ActionButton action={profile.actions.disconnect} onClick={() => post('DISCONNECT', { id: profile.id })} />
      </div>
    </div>
    {profile.detailText && <p className="supporting secondary">{profile.detailText}</p>}
    <div className="row-foot">
      <span className="endpoint"><span className="secondary">SOCKS5 · </span><code>{profile.endpointText}</code></span>
      <ActionButton action={profile.actions.copy} onClick={() => post('COPY_ENDPOINT', { id: profile.id })} />
      <Disclosure open={expanded} controls={detailsId} onClick={toggle} />
    </div>
    <div id={detailsId} hidden={!expanded} className="row-details">
      <Facts facts={profile.facts} />
      <div className="actions">
        <ActionButton action={profile.actions.editPort} focusKey={`port:${profile.id}`} onClick={() => post('BEGIN_PORT_EDIT', { id: profile.id })} />
        <ActionButton action={profile.actions.settings} focusKey={`policy:${profile.id}`} onClick={() => post('BEGIN_POLICY_EDIT', { id: profile.id })} />
      </div>
      {/* Beside the button that opened it; the row's foot keeps it reachable if the details are folded away. */}
      {expanded && editorForm}
      <LocationResult profile={profile} />
      <div className="details-footer">
        <ActionButton action={profile.actions.remove} focusKey={`remove:${profile.id}`} danger onClick={() => post('REQUEST_REMOVE_PROFILE', { id: profile.id })} />
      </div>
    </div>
    {!expanded && editorForm}
  </article>;
}

function LocationResult({ profile }: { profile: ProfileView }) {
  const location = profile.location;
  return <section className="location" aria-label="Exit location">
    {location.previousResult && <p className="metadata secondary">Previous result</p>}
    <p className={location.phase === 'unavailable' ? 'location-unavailable' : ''} role="status">{location.text}</p>
    <Facts facts={location.facts} />
    {location.checkedAt && <p className="metadata secondary">Checked <time dateTime={location.checkedAt}>{clock(location.checkedAt)}</time></p>}
    <ActionButton action={profile.actions.locate} onClick={() => post('LOCATE', { id: profile.id })} />
  </section>;
}
