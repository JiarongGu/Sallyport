import { useId, type ReactNode } from 'react';
import type { ClientState, DriverView, GameView, RunView } from './contract';
import { post } from './commands';
import { duration } from './format';
import { ActionButton, Disclosure, EmptyView, Facts, PageHeader, StatusPill } from './ui/shared';

export function Games({ state, now, expanded, toggle, editorForm }: {
  state: ClientState; now: number; expanded: ReadonlySet<string>; toggle: (key: string) => void; editorForm: ReactNode;
}) {
  return <section className="page" aria-label="Games">
    <PageHeader title="Games" subtitle="Run a game through a VPN profile: everything its programs send goes through the VPN.">
      <ActionButton action={state.actions.addGame} focusKey="add-game" primary onClick={() => post('ADD_GAME', {})} />
    </PageHeader>
    <Driver driver={state.driver} />
    {state.gamesEmpty && <EmptyView empty={state.gamesEmpty} actions={state.actions} />}
    {editorForm && !state.games.some((game) => game.id === state.editor?.subjectId) && editorForm}
    <div className="rows">
      {state.games.map((game) => <GameRow key={game.id} game={game} now={now}
        expanded={expanded.has(`game:${game.id}`)} toggle={() => toggle(`game:${game.id}`)}
        editorForm={state.editor?.subjectId === game.id ? editorForm : null} />)}
    </div>
  </section>;
}

function Driver({ driver }: { driver: DriverView }) {
  // Said only when game routing is not simply ready: a ready driver needs no card of its own.
  if (driver.phase === 'ready') return null;
  return <section className={`card driver tone-border-${driver.tone}`} aria-label="Game routing on this computer">
    <div className="row-head">
      <div className="row-identity">
        <StatusPill tone={driver.tone} announce>{driver.statusText}</StatusPill>
        {driver.detailText && <p className="supporting secondary">{driver.detailText}</p>}
      </div>
      <div className="actions row-primary">
        <ActionButton action={driver.install} focusKey="driver" primary onClick={() => post('REQUEST_DRIVER_INSTALL', {})} />
        <ActionButton action={driver.remove} focusKey="driver" onClick={() => post('REQUEST_DRIVER_REMOVE', {})} />
      </div>
    </div>
    <Facts facts={driver.facts} />
  </section>;
}

function GameRow({ game, now, expanded, toggle, editorForm }: {
  game: GameView; now: number; expanded: boolean; toggle: () => void; editorForm: ReactNode;
}) {
  const detailsId = useId();
  const headingId = useId();
  return <article className={`card tone-border-${game.tone}${expanded ? ' expanded' : ''}`} aria-labelledby={headingId}>
    <div className="row-head">
      <div className="row-identity">
        <h2 id={headingId}>{game.name}</h2>
        <div className="profile-status">
          <StatusPill tone={game.tone} announce>{game.statusText}</StatusPill>
          {game.run?.since && <span className="secondary duration">Routed for {duration(game.run.since, now)}</span>}
        </div>
      </div>
      <div className="actions row-primary">
        <ActionButton action={game.actions.run} primary onClick={() => post('RUN_GAME', { id: game.id })} />
        <ActionButton action={game.actions.stop} onClick={() => post('STOP_RUN', {})} />
      </div>
    </div>
    {game.run && <Run run={game.run} />}
    <div className="row-foot">
      <span className="secondary through">{game.throughText} · {game.lossText}</span>
      <Disclosure open={expanded} controls={detailsId} onClick={toggle} />
    </div>
    <div id={detailsId} hidden={!expanded} className="row-details">
      {game.folder && <Facts facts={[{ label: 'Folder', value: game.folder }]} />}
      <div className="actions">
        <ActionButton action={game.actions.edit} focusKey={`game:${game.id}`} onClick={() => post('BEGIN_GAME_EDIT', { id: game.id })} />
        <ActionButton action={game.actions.rescan} onClick={() => post('RESCAN_GAME', { id: game.id })} />
      </div>
      {expanded && editorForm}
      <section className="programs" aria-label="Programs">
        <p className="secondary">{game.programsText}</p>
        <ul>
          {game.programs.map((program) => <li key={program.path} className={program.included ? 'included' : 'left-out'}>
            <div className="program-name">
              <span>{program.name}</span>
              <span className="metadata secondary">{program.included ? 'Routed' : `Left out — ${program.reason}`}</span>
              <span className="metadata secondary program-path">{program.path}</span>
            </div>
            <ActionButton action={program.toggle} onClick={() => post('SET_GAME_PROGRAM', { id: game.id, path: program.path, included: !program.included })} />
          </li>)}
        </ul>
      </section>
      <div className="details-footer">
        <ActionButton action={game.actions.remove} focusKey={`remove-game:${game.id}`} danger onClick={() => post('REQUEST_REMOVE_GAME', { id: game.id })} />
      </div>
    </div>
    {!expanded && editorForm}
  </article>;
}

function Run({ run }: { run: RunView }) {
  return <section className="run" aria-label="Routing">
    {run.detailText && <p className="supporting secondary">{run.detailText}</p>}
    <p className={`coverage coverage-${run.coverage.result}`} role="status">
      <span className="status-symbol" aria-hidden="true">{run.coverage.result === 'holds' ? '✓' : run.coverage.result === 'failed' ? '!' : '?'}</span>
      {run.coverage.text}
    </p>
    <Facts facts={run.facts} />
  </section>;
}
