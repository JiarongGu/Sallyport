import { t } from '../i18n';
import { useId, type ReactNode, type Ref } from 'react';
import type { Action, ClientState, Empty, Fact, Tone } from '../contract';
import { post } from '../commands';

export type IconName = 'chevron' | 'copy' | 'dismiss' | 'check' | 'info' | 'warning' | 'error' | 'question' | 'menu' | 'pulse' | 'apps'
  | 'key' | 'gear' | 'plus' | 'folder' | 'down' | 'up' | 'refresh' | 'overview';

// 16px strokes in the text's colour: a text glyph (● › ⌄ ✓ △) comes from whichever font has it, larger and higher
// in Microsoft YaHei than in Segoe UI.
const glyphs: Record<IconName, ReactNode> = {
  chevron: <path d="M4 6l4 4 4-4" />,
  copy: <path d="M5.5 5.5V3.5a1 1 0 0 1 1-1h6a1 1 0 0 1 1 1v6a1 1 0 0 1-1 1h-2M3.5 5.5h6a1 1 0 0 1 1 1v6a1 1 0 0 1-1 1h-6a1 1 0 0 1-1-1v-6a1 1 0 0 1 1-1z" />,
  dismiss: <path d="M4 4l8 8M12 4l-8 8" />,
  check: <path d="M3.5 8.5l3 3 6-6" />,
  info: <><circle cx="8" cy="8" r="6.5" /><path d="M8 7.5v4M8 5v.25" /></>,
  warning: <><path d="M8 2.5l6 11H2z" /><path d="M8 7v3M8 12v.25" /></>,
  error: <><circle cx="8" cy="8" r="6.5" /><path d="M5.5 5.5l5 5M10.5 5.5l-5 5" /></>,
  question: <><circle cx="8" cy="8" r="6.5" /><path d="M6.2 6.6a1.8 1.8 0 1 1 2.6 1.6c-.6.3-.8.6-.8 1.3M8 11.5v.25" /></>,
  menu: <path d="M2.5 4h11M2.5 8h11M2.5 12h11" />,
  pulse: <path d="M1.5 8.5h3l2-5 3 9 2-4h3" />,
  apps: <path d="M3 2.5h3a.5.5 0 0 1 .5.5v3a.5.5 0 0 1-.5.5H3a.5.5 0 0 1-.5-.5V3a.5.5 0 0 1 .5-.5zM10 2.5h3a.5.5 0 0 1 .5.5v3a.5.5 0 0 1-.5.5h-3a.5.5 0 0 1-.5-.5V3a.5.5 0 0 1 .5-.5zM3 9.5h3a.5.5 0 0 1 .5.5v3a.5.5 0 0 1-.5.5H3a.5.5 0 0 1-.5-.5v-3a.5.5 0 0 1 .5-.5zM10 9.5h3a.5.5 0 0 1 .5.5v3a.5.5 0 0 1-.5.5h-3a.5.5 0 0 1-.5-.5v-3a.5.5 0 0 1 .5-.5z" />,
  key: <><circle cx="5.5" cy="10.5" r="3" /><path d="M7.6 8.4l5.9-5.9M11 5l2 2M12.5 3.5L14 5" /></>,
  gear: <><circle cx="8" cy="8" r="2.25" /><path d="M8 1.5v2M8 12.5v2M1.5 8h2M12.5 8h2M3.4 3.4l1.4 1.4M11.2 11.2l1.4 1.4M3.4 12.6l1.4-1.4M11.2 4.8l1.4-1.4" /></>,
  plus: <path d="M8 3v10M3 8h10" />,
  folder: <path d="M2 4.5a1 1 0 0 1 1-1h3l1.5 1.5H13a1 1 0 0 1 1 1V12a1 1 0 0 1-1 1H3a1 1 0 0 1-1-1z" />,
  down: <path d="M8 2.5v11M4 9.5l4 4 4-4" />,
  up: <path d="M8 13.5v-11M4 6.5l4-4 4 4" />,
  refresh: <path d="M13 8a5 5 0 1 1-1.5-3.6M13 2.5v2.5h-2.5" />,
  overview: <><path d="M2.5 11.5a5.5 5.5 0 0 1 11 0" /><path d="M8 11.5l2.75-3.75" /><circle cx="8" cy="11.5" r=".75" /></>,
};

export function Icon({ name, className }: { name: IconName; className?: string }) {
  return <svg className={className ? `glyph ${className}` : 'glyph'} viewBox="0 0 16 16" width="16" height="16" aria-hidden="true">{glyphs[name]}</svg>;
}

/** A host action as a button. With `icon` it is drawn as that icon alone, the host's label its name and tooltip; with
 *  `glyph`, the icon leads its label. */
export function ActionButton({ action, onClick, primary = false, danger = false, subtle = false, icon, glyph, type = 'button', buttonRef, focusKey }: {
  action: Action;
  onClick?: () => void;
  primary?: boolean;
  danger?: boolean;
  subtle?: boolean;
  icon?: IconName;
  glyph?: IconName;
  type?: 'button' | 'submit';
  buttonRef?: Ref<HTMLButtonElement>;
  focusKey?: string;
}) {
  const reasonId = useId();
  if (!action.visible) return null;
  const classes = [primary && 'primary', danger && 'danger', (subtle || icon) && 'subtle', icon && 'icon'].filter(Boolean).join(' ');
  return (
    <span className="action">
      <button
        ref={buttonRef}
        data-focus-key={focusKey}
        type={type}
        className={classes || undefined}
        disabled={!action.enabled || action.busy}
        aria-busy={action.busy || undefined}
        aria-describedby={action.reason ? reasonId : undefined}
        aria-label={icon ? action.label : undefined}
        title={icon ? action.label : undefined}
        onClick={onClick}
      >
        {action.busy ? <span className="spinner" aria-hidden="true" /> : (icon ?? glyph) && <Icon name={(icon ?? glyph)!} />}
        {!icon && action.label}
      </button>
      {action.reason && <span className="action-reason" id={reasonId}>{action.reason}</span>}
    </span>
  );
}

/** The tone as a dot drawn by the stylesheet, the same size in every font; the words say the state. */
export function StatusPill({ tone, children, announce = false }: { tone: Tone; children: ReactNode; announce?: boolean }) {
  return <span className={`status tone-${tone}`} role={announce ? 'status' : undefined} aria-atomic={announce || undefined}>{children}</span>;
}

/** Labelled values. A fact with a figure shows it large, its value beneath as what qualifies it. */
export function Facts({ facts, figures = false }: { facts: Fact[]; figures?: boolean }) {
  if (!facts.length) return null;
  return <dl className={figures ? 'facts figures' : 'facts'}>{facts.map((fact, index) => (
    <div key={`${fact.label}-${index}`}>
      <dt>{fact.label}</dt>
      {fact.figure && <dd className="figure">{fact.figure}</dd>}
      <dd className={fact.figure ? 'qualifier' : undefined}>{fact.value}</dd>
    </div>
  ))}</dl>;
}

/** Shows or hides what `controls` names. With `icon`, a chevron alone, its words the button's name and tooltip. */
export function Disclosure({ open, controls, onClick, children, icon = false }: {
  open: boolean; controls: string; onClick: () => void; children?: string; icon?: boolean;
}) {
  const label = children ?? t('Details');
  return <button type="button" className={`disclosure subtle${icon ? ' icon' : ''}`} aria-expanded={open} aria-controls={controls}
    aria-label={icon ? label : undefined} title={icon ? label : undefined} onClick={onClick}>
    {!icon && label}<Icon name="chevron" className={open ? 'open' : undefined} />
  </button>;
}

export function EmptyView({ empty, actions }: { empty: Empty; actions: ClientState['actions'] }) {
  return <div className="empty">
    <h2>{empty.title}</h2><p className="secondary">{empty.text}</p>
    {empty.command === 'IMPORT'
      ? <ActionButton action={actions.import} primary onClick={() => post('IMPORT', {})} />
      : <ActionButton action={empty.command === 'ADD_FOLDER' ? actions.addFolder : actions.addProgram} focusKey="empty-add" primary
          onClick={() => post('ADD_APP', { source: empty.command === 'ADD_FOLDER' ? 'folder' : 'program' })} />}
  </div>;
}

/** On or off at once, as Connect's toggles: a profile connected, an app routed. `action` is what flipping it does now
 *  (Connect or Disconnect, Route or Stop routing) — its label names the switch, its state says whether it may. */
export function Switch({ on, action, onFlip, large = false }: { on: boolean; action: Action; onFlip: () => void; large?: boolean }) {
  const reasonId = useId();
  if (!action.visible) return null;
  return <span className="action">
    <button type="button" role="switch" aria-checked={on} className={`switch${large ? ' large' : ''}${action.busy ? ' busy' : ''}`}
      aria-label={action.label} title={action.reason ?? action.label} disabled={!action.enabled || action.busy}
      aria-describedby={action.reason ? reasonId : undefined} onClick={onFlip}>
      <span className="knob" aria-hidden="true">{action.busy && <span className="spinner" />}</span>
    </button>
    {action.reason && <span className="sr-only" id={reasonId}>{action.reason}</span>}
  </span>;
}

/** An app or a profile by its first letter, in a tile of a colour its name picks: a list reads by shape before words. */
export function Monogram({ name }: { name: string }) {
  let hash = 0;
  for (const c of name) hash = (hash * 31 + c.charCodeAt(0)) >>> 0;
  return <span className={`monogram hue-${hash % 6}`} aria-hidden="true">{[...name.trim()][0]?.toUpperCase() ?? '?'}</span>;
}
