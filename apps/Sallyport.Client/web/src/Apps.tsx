import { t } from './i18n';
import { useId, type ReactNode } from 'react';
import type { AppItemView, ClientState, ProgramView } from './contract';
import { post } from './commands';
import { duration } from './format';
import { RunDetails } from './Run';
import { ActionButton, Disclosure, EmptyView, Facts, Icon, Monogram, StatusPill, Switch } from './ui/shared';
import { Traffic } from './Network';

/** Everything the person routes, one card per app — a program or a folder. Each opens a page of its own. */
export function Apps({ state, now, open, editorForm }: {
  state: ClientState; now: number; open: (id: string) => void; editorForm: ReactNode;
}) {
  const editing = state.editor;
  const subject = editing?.kind === 'program' ? editing.subjectId.split('/')[0] : editing?.subjectId;
  return <section className="page" aria-label={t('Apps')}>
    <p className="page-intro secondary">{t('Programs and games routed through a VPN profile, by all their traffic or by proxy settings at launch. Everything else keeps your own connection.')}</p>
    {state.appsEmpty && <EmptyView empty={state.appsEmpty} actions={state.actions} />}
    {editorForm && !state.apps.some((app) => app.id === subject) && editorForm}
    <div className="rows">
      {state.apps.map((app) => <AppCard key={app.id} app={app} now={now} open={() => open(app.id)} />)}
    </div>
  </section>;
}

function AppHead({ app, now, headingId, children }: { app: AppItemView; now: number; headingId?: string; children?: ReactNode }) {
  return <div className="row-head">
    <Monogram name={app.name} />
    <div className="row-identity">
      {headingId && <h3 id={headingId}>{app.name}</h3>}
      <div className="profile-status">
        <StatusPill tone={app.tone} announce>{app.statusText}</StatusPill>
        {app.run?.since && <span className="duration">{t('Routed for {0}', duration(app.run.since, now))}</span>}
        <span className="summary">{[app.throughText, app.methodText, app.lossText].filter(Boolean).join(' · ')}</span>
      </div>
    </div>
    <div className="actions row-primary">
      <ActionButton action={app.actions.launch} primary onClick={() => post('LAUNCH_APP', { id: app.id })} />
      {/* Routed or not, as a switch: Route turns it on, Stop routing off. */}
      <Switch on={app.actions.stop.visible} action={app.actions.stop.visible ? app.actions.stop : app.actions.route}
        onFlip={() => app.actions.stop.visible ? post('STOP_RUN', {}) : post('ROUTE_APP', { id: app.id })} />
    </div>
    {children}
  </div>;
}

function AppCard({ app, now, open }: { app: AppItemView; now: number; open: () => void }) {
  const headingId = useId();
  return <article className={`card tone-border-${app.tone}`} aria-labelledby={headingId}>
    <AppHead app={app} now={now} headingId={headingId}>
      <button type="button" className="subtle icon open-page" data-focus-key={`open-app:${app.id}`} aria-label={t('Open {0}', app.name)}
        title={t('Open {0}', app.name)} onClick={open}><Icon name="chevron" /></button>
    </AppHead>
    {app.run && <RunDetails run={app.run} counts={false} />}
  </article>;
}

/** One app's page: what it is doing now, how it is set up, and each of its programs with what that program is doing —
 *  the setup and monitoring a card has no room for. Its editors open here, beside what they change. */
export function AppPage({ app, state, now, expanded, toggle, editorForm }: {
  app: AppItemView; state: ClientState; now: number; expanded: ReadonlySet<string>; toggle: (key: string) => void; editorForm: ReactNode;
}) {
  const monitorId = useId();
  const setupId = useId();
  const programsId = useId();
  const editing = state.editor;
  const appEditor = editing?.kind === 'app' && editing.subjectId === app.id ? editorForm : null;
  const programEditing = editing?.kind === 'program' && editing.subjectId.split('/')[0] === app.id ? editing.subjectId.split('/')[1] : null;
  // The tunnel's traffic while the app is routed through it: all of it, this app's and whatever else goes that way.
  const channel = app.profileId && app.run && app.run.phase !== 'ended'
    ? state.network.channels.find((candidate) => candidate.profileId === app.profileId) : undefined;
  return <section className="page app-page" aria-label={app.name}>
    <article className={`card tone-border-${app.tone}`}><AppHead app={app} now={now} /></article>

    <section className="settings-group" aria-labelledby={monitorId}>
      <h2 id={monitorId}>{t('Monitoring')}</h2>
      <div className="card">
        {app.run ? <RunDetails run={app.run} /> : <p className="secondary">{t('Not routed since Sallyport started. Its routing shows here while it is on.')}</p>}
        {channel?.traffic && <div className="app-traffic">
          <p className="secondary">{t('Through {0}: everything routed through it, this app’s and any other’s.', channel.name)}</p>
          <Traffic traffic={channel.traffic} />
        </div>}
      </div>
    </section>

    <section className="settings-group" aria-labelledby={programsId}>
      <div className="group-head">
        <h2 id={programsId}>{t('Programs')}</h2>
        <div className="actions">
          <ActionButton action={app.actions.addProgram} subtle glyph="plus" onClick={() => post('ADD_APP_PROGRAM', { id: app.id })} />
          <ActionButton action={app.actions.rescan} subtle glyph="refresh" onClick={() => post('RESCAN_APP', { id: app.id })} />
        </div>
      </div>
      {app.programsText && <p className="secondary">{app.programsText}</p>}
      <div className="card program-card">
        <div className="program-list-head" aria-hidden="true"><span>{t('Program')}</span><span>{t('Through the VPN')}</span></div>
        <ul className="program-list">
          {app.programs.map((program) => <ProgramRow key={program.id} app={app} program={program}
            expanded={expanded.has(`program:${program.id}`) || programEditing === program.id} toggle={() => toggle(`program:${program.id}`)}
            editorForm={programEditing === program.id ? editorForm : null} />)}
        </ul>
      </div>
    </section>

    <section className="settings-group" aria-labelledby={setupId}>
      <div className="group-head">
        <h2 id={setupId}>{t('Setup')}</h2>
        <div className="actions"><ActionButton action={app.actions.edit} focusKey={`app:${app.id}`} onClick={() => post('BEGIN_APP_EDIT', { id: app.id })} /></div>
      </div>
      <div className="card">
        <Facts facts={app.setup} />
        {appEditor}
      </div>
    </section>

    <div className="details-footer">
      <ActionButton action={app.actions.remove} focusKey={`remove-app:${app.id}`} danger onClick={() => post('REQUEST_REMOVE_APP', { id: app.id })} />
    </div>
  </section>;
}

/** One program of an app: its name, what it is doing now, whether it goes through the VPN (the switch), and folded
 *  under it, its details and what can be changed. */
function ProgramRow({ app, program, expanded, toggle, editorForm }: {
  app: AppItemView; program: ProgramView; expanded: boolean; toggle: () => void; editorForm: ReactNode;
}) {
  const settingsId = useId();
  const detailsId = useId();
  return <li className={program.included ? 'included' : 'left-out'}>
    <div className="program-head">
      <div className="program-name">
        <span className="program-title">{program.name}{program.launchTarget && <span className="metadata secondary">{t(' · Launch starts this')}</span>}</span>
        {program.missing ? <span className="metadata tone-trouble">{t('Not found where it was added from')}</span>
          : program.activity ? <span className={`metadata activity tone-${program.activity.tone}`}>{program.activity.text}</span>
          : <span className="metadata secondary">{program.statusText}</span>}
      </div>
      <Switch on={program.included} action={program.actions.toggle}
        onFlip={() => post('SET_APP_PROGRAM', { id: app.id, programId: program.id, included: !program.included })} />
      <Disclosure open={expanded} controls={detailsId} onClick={toggle} icon />
    </div>
    <div id={detailsId} hidden={!expanded} className="program-details">
      {program.facts && <Facts facts={program.facts} />}
      <p className="metadata secondary program-path">{program.path}</p>
      {(program.actions.edit.visible || program.actions.launch.visible) && <div className="actions">
        <ActionButton action={program.actions.edit} focusKey={`program:${program.id}`} onClick={() => post('BEGIN_PROGRAM_EDIT', { id: app.id, programId: program.id })} />
        <ActionButton action={program.actions.launch} onClick={() => post('LAUNCH_APP', { id: app.id, programId: program.id })} />
      </div>}
      {program.startupSettings.length > 0 && <details className="startup-settings">
        <summary aria-controls={settingsId}>{t('Startup settings')}</summary>
        <pre id={settingsId}>{program.startupSettings.join('\n')}</pre>
      </details>}
      {editorForm}
    </div>
  </li>;
}
