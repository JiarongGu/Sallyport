import { useId, type ReactNode } from 'react';
import type { GameView, RunView } from './contract';
import { post } from './commands';
import { duration } from './format';
import { ActionButton, Disclosure, Facts, StatusPill } from './ui/shared';

/** A program or folder routed by its packets, on the Apps page. */
export function GameRow({ game, now, expanded, toggle, editorForm }: {
  game: GameView; now: number; expanded: boolean; toggle: () => void; editorForm: ReactNode;
}) {
  const detailsId = useId();
  const headingId = useId();
  return <article className={`card tone-border-${game.tone}${expanded ? ' expanded' : ''}`} aria-labelledby={headingId}>
    <div className="row-head">
      <div className="row-identity">
        <h3 id={headingId}>{game.name}</h3>
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
    {game.run && <RunDetails run={game.run} />}
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

/** What a run is doing: its detail, whether everything went through the VPN (three outcomes), and its counts. */
export function RunDetails({ run }: { run: RunView }) {
  return <section className="run" aria-label="Routing">
    {run.detailText && <p className="supporting secondary">{run.detailText}</p>}
    <p className={`coverage coverage-${run.coverage.result}`} role="status">
      <span className="status-symbol" aria-hidden="true">{run.coverage.result === 'holds' ? '✓' : run.coverage.result === 'failed' ? '!' : '?'}</span>
      {run.coverage.text}
    </p>
    <Facts facts={run.facts} />
  </section>;
}
