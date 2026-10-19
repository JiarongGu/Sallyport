import { useId, type ReactNode } from 'react';
import type { AppView, ClientState } from './contract';
import { post } from './commands';
import { GameRow } from './Games';
import type { Empty } from './contract';
import { ActionButton, Disclosure, Facts, PageHeader } from './ui/shared';

/** Everything the person routes, by how its traffic reaches the VPN: by its packets, or by proxy settings at launch. */
export function Apps({ state, now, expanded, toggle, editorForm }: {
  state: ClientState; now: number; expanded: ReadonlySet<string>; toggle: (key: string) => void; editorForm: ReactNode;
}) {
  const packetsId = useId();
  const proxyId = useId();
  const gameEditor = state.editor?.kind === 'game' ? editorForm : null;
  const appEditor = state.editor?.kind === 'app' ? editorForm : null;
  return <section className="page" aria-label="Apps">
    <PageHeader title="Apps" subtitle="Choose what goes through a VPN profile. Everything else keeps your own connection." />

    <section className="app-section" aria-labelledby={packetsId}>
      <header className="section-head">
        <div>
          <h2 id={packetsId}>All traffic — packet driver</h2>
          <p className="secondary metadata">Everything a game or program sends, TCP and UDP, goes through the VPN. Added by its folder.</p>
        </div>
        <ActionButton action={state.actions.addGame} focusKey="add-game" primary onClick={() => post('ADD_GAME', {})} />
      </header>
      {state.gamesEmpty && <SectionEmpty empty={state.gamesEmpty} state={state} />}
      {gameEditor && !state.games.some((game) => game.id === state.editor?.subjectId) && gameEditor}
      <div className="rows">
        {state.games.map((game) => <GameRow key={game.id} game={game} now={now}
          expanded={expanded.has(`game:${game.id}`)} toggle={() => toggle(`game:${game.id}`)}
          editorForm={state.editor?.subjectId === game.id ? gameEditor : null} />)}
      </div>
    </section>

    <section className="app-section" aria-labelledby={proxyId}>
      <header className="section-head">
        <div>
          <h2 id={proxyId}>Proxy settings when launched here</h2>
          <p className="secondary metadata">Browsers and proxy-aware programs started from Sallyport use the profile’s proxy. TCP only.</p>
        </div>
        <ActionButton action={state.actions.addApp} focusKey="add-app" onClick={() => post('ADD_APP', {})} />
      </header>
      {state.appsEmpty && <SectionEmpty empty={state.appsEmpty} state={state} />}
      {appEditor && !state.apps.some((app) => app.id === state.editor?.subjectId) && appEditor}
      <div className="rows">
        {state.apps.map((app) => <AppRow key={app.id} app={app} expanded={expanded.has(`app:${app.id}`)}
          toggle={() => toggle(`app:${app.id}`)} editorForm={state.editor?.subjectId === app.id ? appEditor : null} />)}
      </div>
    </section>
  </section>;
}

/** A section with nothing in it yet: its own button is in its head, so only an import it cannot offer is offered here. */
function SectionEmpty({ empty, state }: { empty: Empty; state: ClientState }) {
  return <div className="section-empty">
    <p><strong>{empty.title}</strong></p>
    <p className="secondary">{empty.text}</p>
    {empty.command === 'IMPORT' && <ActionButton action={state.actions.import} primary onClick={() => post('IMPORT', {})} />}
  </div>;
}

function AppRow({ app, expanded, toggle, editorForm }: {
  app: AppView; expanded: boolean; toggle: () => void; editorForm: ReactNode;
}) {
  const detailsId = useId();
  const settingsId = useId();
  const headingId = useId();
  return <article className={`card${app.phase === 'blocked' ? ' blocked' : ''}${expanded ? ' expanded' : ''}`} aria-labelledby={headingId}>
    <div className="row-head">
      <div className="row-identity"><h3 id={headingId}>{app.name}</h3><p className="metadata secondary">{app.kindLabel}</p></div>
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
