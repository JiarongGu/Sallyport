import { t } from './i18n';
import { useId, type ReactNode } from 'react';
import type { AppItemView, ClientState, ProgramView } from './contract';
import { post } from './commands';
import { duration } from './format';
import { RunDetails } from './Run';
import { ActionButton, Disclosure, EmptyView, Facts, Monogram, StatusPill, Switch } from './ui/shared';

/** Everything the person routes, one card per app — a program or a folder — each program with its methods. */
export function Apps({ state, now, expanded, toggle, editorForm }: {
  state: ClientState; now: number; expanded: ReadonlySet<string>; toggle: (key: string) => void; editorForm: ReactNode;
}) {
  const editing = state.editor;
  const subject = editing?.kind === 'program' ? editing.subjectId.split('/')[0] : editing?.subjectId;
  return <section className="page" aria-label={t('Apps')}>
    <p className="page-intro secondary">{t('Programs and games routed through a VPN profile, by all their traffic or by proxy settings at launch. Everything else keeps your own connection.')}</p>
    {state.appsEmpty && <EmptyView empty={state.appsEmpty} actions={state.actions} />}
    {editorForm && !state.apps.some((app) => app.id === subject) && editorForm}
    <div className="rows">
      {state.apps.map((app) => <AppCard key={app.id} app={app} now={now}
        expanded={expanded.has(`app:${app.id}`)} toggle={() => toggle(`app:${app.id}`)}
        editing={subject === app.id ? editing : null} editorForm={subject === app.id ? editorForm : null} />)}
    </div>
  </section>;
}

function AppCard({ app, now, expanded, toggle, editing, editorForm }: {
  app: AppItemView; now: number; expanded: boolean; toggle: () => void; editing: ClientState['editor']; editorForm: ReactNode;
}) {
  const detailsId = useId();
  const headingId = useId();
  // The app's own editor sits with its details; a program's beside its row.
  const appEditor = editing?.kind === 'app' ? editorForm : null;
  const programEditing = editing?.kind === 'program' ? editing.subjectId.split('/')[1] : null;
  return <article className={`card tone-border-${app.tone}${expanded ? ' expanded' : ''}`} aria-labelledby={headingId}>
    <div className="row-head">
      <Monogram name={app.name} />
      <div className="row-identity">
        <h3 id={headingId}>{app.name}</h3>
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
      <Disclosure open={expanded} controls={detailsId} onClick={toggle} icon />
    </div>
    {app.run && <RunDetails run={app.run} />}
    <div id={detailsId} hidden={!expanded} className="row-details">
      <Facts facts={[
        ...(app.folder ? [{ label: t('Folder'), value: app.folder }] : []),
        ...(app.launchText ? [{ label: t('Launch'), value: app.launchText }] : []),
      ]} />
      <div className="actions">
        <ActionButton action={app.actions.edit} focusKey={`app:${app.id}`} onClick={() => post('BEGIN_APP_EDIT', { id: app.id })} />
        <ActionButton action={app.actions.rescan} onClick={() => post('RESCAN_APP', { id: app.id })} />
        <ActionButton action={app.actions.addProgram} onClick={() => post('ADD_APP_PROGRAM', { id: app.id })} />
      </div>
      {expanded && appEditor}
      <section className="programs" aria-label={t('Programs')}>
        {app.programsText && <p className="secondary">{app.programsText}</p>}
        <ul>
          {app.programs.map((program) => <ProgramRow key={program.id} app={app} program={program}
            editorForm={programEditing === program.id ? editorForm : null} />)}
        </ul>
      </section>
      <div className="details-footer">
        <ActionButton action={app.actions.remove} focusKey={`remove-app:${app.id}`} danger onClick={() => post('REQUEST_REMOVE_APP', { id: app.id })} />
      </div>
    </div>
    {!expanded && (appEditor ?? (programEditing ? editorForm : null))}
  </article>;
}

function ProgramRow({ app, program, editorForm }: { app: AppItemView; program: ProgramView; editorForm: ReactNode }) {
  const settingsId = useId();
  return <li className={program.included ? 'included' : 'left-out'}>
    <div className="program-line">
      <div className="program-name">
        <span>{program.name}{program.launchTarget && <span className="metadata secondary">{t(' · Launch starts this')}</span>}</span>
        <span className={`metadata ${program.missing ? 'tone-trouble' : 'secondary'}`}>{program.missing ? t('Not found where it was added from') : program.statusText}</span>
        <span className="metadata secondary program-path">{program.path}</span>
      </div>
      <div className="actions">
        <ActionButton action={program.actions.launch} onClick={() => post('LAUNCH_APP', { id: app.id, programId: program.id })} />
        <ActionButton action={program.actions.edit} subtle focusKey={`program:${program.id}`} onClick={() => post('BEGIN_PROGRAM_EDIT', { id: app.id, programId: program.id })} />
        <ActionButton action={program.actions.toggle} subtle onClick={() => post('SET_APP_PROGRAM', { id: app.id, programId: program.id, included: !program.included })} />
      </div>
    </div>
    {program.startupSettings.length > 0 && <details className="startup-settings">
      <summary aria-controls={settingsId}>{t('Startup settings')}</summary>
      <pre id={settingsId}>{program.startupSettings.join('\n')}</pre>
    </details>}
    {editorForm}
  </li>;
}
