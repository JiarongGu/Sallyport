import { useId, type ReactNode } from 'react';
import type { AppView, ClientState } from './contract';
import { post } from './commands';
import { ActionButton, Disclosure, EmptyView, Facts, PageHeader } from './ui/shared';

export function Apps({ state, expanded, toggle, editorForm }: {
  state: ClientState; expanded: ReadonlySet<string>; toggle: (key: string) => void; editorForm: ReactNode;
}) {
  return <section className="page" aria-label="Apps">
    <PageHeader title="Apps" subtitle="Launch apps with a profile’s proxy settings.">
      <ActionButton action={state.actions.addApp} focusKey="add-app" primary onClick={() => post('ADD_APP', {})} />
    </PageHeader>
    <p className="page-note metadata secondary">{state.appsNote}</p>
    {state.appsEmpty && <EmptyView empty={state.appsEmpty} actions={state.actions} />}
    {editorForm && !state.apps.some((app) => app.id === state.editor?.subjectId) && editorForm}
    <div className="rows">
      {state.apps.map((app) => <AppRow key={app.id} app={app} expanded={expanded.has(`app:${app.id}`)}
        toggle={() => toggle(`app:${app.id}`)} editorForm={state.editor?.subjectId === app.id ? editorForm : null} />)}
    </div>
  </section>;
}

function AppRow({ app, expanded, toggle, editorForm }: {
  app: AppView; expanded: boolean; toggle: () => void; editorForm: ReactNode;
}) {
  const detailsId = useId();
  const settingsId = useId();
  const headingId = useId();
  return <article className={`card${app.phase === 'blocked' ? ' blocked' : ''}${expanded ? ' expanded' : ''}`} aria-labelledby={headingId}>
    <div className="row-head">
      <div className="row-identity"><h2 id={headingId}>{app.name}</h2><p className="metadata secondary">{app.kindLabel}</p></div>
      <ActionButton action={app.actions.launch} primary onClick={() => post('LAUNCH_APP', { id: app.id })} />
    </div>
    <div className="row-foot"><span className="secondary through">{app.throughText}</span><Disclosure open={expanded} controls={detailsId} onClick={toggle} /></div>
    <p className={`app-status${app.phase === 'blocked' ? ' tone-trouble' : ' secondary'}`} role="status">{app.statusText}</p>
    <div id={detailsId} hidden={!expanded} className="row-details">
      <Facts facts={[{ label: 'Path', value: app.path }]} />
      <p className="secondary kind-help">{app.kindHelp}</p>
      <ActionButton action={app.actions.edit} focusKey={`app:${app.id}`} onClick={() => post('BEGIN_APP_EDIT', { id: app.id })} />
      <details className="startup-settings">
        <summary aria-controls={settingsId}>Startup settings</summary>
        <pre id={settingsId}>{app.startupSettings.join('\n')}</pre>
      </details>
      <div className="details-footer"><ActionButton action={app.actions.remove} danger onClick={() => post('REMOVE_APP', { id: app.id })} /></div>
    </div>
    {editorForm}
  </article>;
}
