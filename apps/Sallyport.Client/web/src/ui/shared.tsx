import { useId, type ReactNode, type Ref } from 'react';
import type { Action, ClientState, Empty, Fact, Tone } from '../contract';
import { post } from '../commands';

export function ActionButton({ action, onClick, primary = false, danger = false, type = 'button', buttonRef, focusKey }: {
  action: Action;
  onClick?: () => void;
  primary?: boolean;
  danger?: boolean;
  type?: 'button' | 'submit';
  buttonRef?: Ref<HTMLButtonElement>;
  focusKey?: string;
}) {
  const reasonId = useId();
  if (!action.visible) return null;
  return (
    <span className="action">
      <button
        ref={buttonRef}
        data-focus-key={focusKey}
        type={type}
        className={`${primary ? 'primary' : ''}${danger ? ' danger' : ''}`}
        disabled={!action.enabled || action.busy}
        aria-busy={action.busy || undefined}
        aria-describedby={action.reason ? reasonId : undefined}
        onClick={onClick}
      >
        {action.busy && <span className="spinner" aria-hidden="true" />}
        {action.label}
      </button>
      {action.reason && <span className="action-reason" id={reasonId}>{action.reason}</span>}
    </span>
  );
}

export function StatusPill({ tone, children, announce = false }: { tone: Tone; children: ReactNode; announce?: boolean }) {
  return (
    <span className={`status tone-${tone}`} role={announce ? 'status' : undefined} aria-atomic={announce || undefined}>
      <span className="status-symbol" aria-hidden="true">{tone === 'trouble' ? '!' : tone === 'busy' ? '◷' : '●'}</span>
      {children}
    </span>
  );
}

export function PageHeader({ title, subtitle, children }: { title: string; subtitle?: string; children?: ReactNode }) {
  return (
    <header className="page-head">
      <div><h1>{title}</h1>{subtitle && <p className="secondary">{subtitle}</p>}</div>
      {children}
    </header>
  );
}

export function Facts({ facts }: { facts: Fact[] }) {
  if (!facts.length) return null;
  return <dl className="facts">{facts.map((fact, index) => (
    <div key={`${fact.label}-${index}`}><dt>{fact.label}</dt><dd>{fact.value}</dd></div>
  ))}</dl>;
}

export function Disclosure({ open, controls, onClick, children = 'Details' }: {
  open: boolean; controls: string; onClick: () => void; children?: ReactNode;
}) {
  return <button type="button" className="disclosure" aria-expanded={open} aria-controls={controls} onClick={onClick}>
    <span aria-hidden="true">{open ? '⌄' : '›'}</span>{children}
  </button>;
}

export function EmptyView({ empty, actions }: { empty: Empty; actions: ClientState['actions'] }) {
  return <div className="empty">
    <h2>{empty.title}</h2><p className="secondary">{empty.text}</p>
    <ActionButton action={empty.command === 'IMPORT' ? actions.import : actions.addApp} focusKey={empty.command === 'ADD_APP' ? 'empty-add-app' : undefined} primary onClick={() => post(empty.command, {})} />
  </div>;
}
