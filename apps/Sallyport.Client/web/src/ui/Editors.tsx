import { useId, useLayoutEffect, useRef, type RefObject } from 'react';
import type { AppValues, ClientState, Confirmation, Editor, EditorField, PortValues } from '../contract';
import { getFocusOrigin, post } from '../commands';
import { ActionButton } from './shared';

export type Draft =
  | { token: string; kind: 'port'; values: PortValues }
  | { token: string; kind: 'app'; values: AppValues };

export function seedDraft(editor: Editor): Draft {
  return editor.kind === 'port'
    ? { token: editor.token, kind: 'port', values: { ...editor.values } }
    : { token: editor.token, kind: 'app', values: { ...editor.values } };
}

function useEditorFocus(ref: RefObject<HTMLFormElement | null>) {
  useLayoutEffect(() => {
    const trigger = getFocusOrigin() ?? (document.activeElement instanceof HTMLElement ? document.activeElement : null);
    const form = ref.current;
    const first = form?.querySelector<HTMLElement>('input:not(:disabled), select:not(:disabled), textarea:not(:disabled), button:not(:disabled)');
    (first ?? form)?.focus();
    // Wait for the host's closing state to restore a trigger it temporarily hid.
    return () => queueMicrotask(() => {
      if (!form?.isConnected) (getFocusOrigin() ?? (trigger?.isConnected ? trigger : null))?.focus();
    });
  }, [ref]);
}

export function EditorForm({ editor, draft, onChange, profileChoices, kindChoices }: {
  editor: Editor;
  draft: Draft;
  onChange: (draft: Draft) => void;
  profileChoices: ClientState['profileChoices'];
  kindChoices: ClientState['kindChoices'];
}) {
  const form = useRef<HTMLFormElement>(null);
  const prefix = useId();
  useEditorFocus(form);
  const saving = editor.phase === 'saving';
  const description = (field: EditorField) =>
    [editor.hint ? `${prefix}-hint` : '', editor.errors[field] ? `${prefix}-${field}-error` : ''].filter(Boolean).join(' ') || undefined;
  const error = (field: EditorField) => editor.errors[field]
    ? <p className="field-error" role="alert" id={`${prefix}-${field}-error`}>{editor.errors[field]}</p>
    : null;

  return <form ref={form} className="editor" tabIndex={-1} aria-label={editor.kind === 'port' ? 'SOCKS5 port' : 'Edit'}
    aria-describedby={editor.errors.form ? `${prefix}-form-error` : undefined}
    aria-busy={saving} noValidate onSubmit={(event) => {
      event.preventDefault();
      if (!editor.save.visible || !editor.save.enabled || editor.save.busy) return;
      if (draft.kind === 'port') post('SET_PORT', { token: editor.token, portText: draft.values.portText });
      else post('UPDATE_APP', { token: editor.token, values: draft.values });
    }}>
    {editor.kind === 'port' && draft.kind === 'port' ? <>
      <label htmlFor={`${prefix}-port`}>SOCKS5 port</label>
      <div className="port-field">
        <code>127.0.0.1:</code>
        <input id={`${prefix}-port`} type="text" inputMode="numeric" autoComplete="off" spellCheck={false}
          value={draft.values.portText} disabled={saving}
          aria-invalid={!!editor.errors.portText} aria-describedby={description('portText')}
          onChange={(event) => onChange({ ...draft, values: { portText: event.target.value } })} />
      </div>
      {error('portText')}
    </> : draft.kind === 'app' ? <div className="editor-fields">
      <div className="field">
        <label htmlFor={`${prefix}-name`}>Name</label>
        <input id={`${prefix}-name`} value={draft.values.name} disabled={saving}
          aria-invalid={!!editor.errors.name} aria-describedby={description('name')}
          onChange={(event) => onChange({ ...draft, values: { ...draft.values, name: event.target.value } })} />
        {error('name')}
      </div>
      <div className="field">
        <label htmlFor={`${prefix}-profile`}>Profile</label>
        <select id={`${prefix}-profile`} value={draft.values.profileId ?? ''} disabled={saving}
          aria-invalid={!!editor.errors.profileId} aria-describedby={description('profileId')}
          onChange={(event) => onChange({ ...draft, values: { ...draft.values, profileId: event.target.value || null } })}>
          <option value="" hidden disabled />
          {profileChoices.map((choice) => <option key={choice.id} value={choice.id} disabled={!choice.enabled}>{choice.label}</option>)}
        </select>
        {error('profileId')}
      </div>
      <div className="field">
        <label htmlFor={`${prefix}-kind`}>App kind</label>
        <select id={`${prefix}-kind`} value={draft.values.kind} disabled={saving}
          aria-invalid={!!editor.errors.kind} aria-describedby={description('kind')}
          onChange={(event) => onChange({ ...draft, values: { ...draft.values, kind: event.target.value as AppValues['kind'] } })}>
          {kindChoices.map((choice) => <option key={choice.value} value={choice.value}>{choice.label}</option>)}
        </select>
        <p className="secondary kind-help">{kindChoices.find((choice) => choice.value === draft.values.kind)?.help}</p>
        {error('kind')}
      </div>
      <div className="field full-field">
        <label htmlFor={`${prefix}-arguments`}>Extra arguments</label>
        <textarea id={`${prefix}-arguments`} rows={2} value={draft.values.arguments} disabled={saving} spellCheck={false}
          aria-invalid={!!editor.errors.arguments} aria-describedby={description('arguments')}
          onChange={(event) => onChange({ ...draft, values: { ...draft.values, arguments: event.target.value } })} />
        {error('arguments')}
      </div>
    </div> : null}
    {editor.hint && <p className="secondary" id={`${prefix}-hint`}>{editor.hint}</p>}
    {error('form')}
    <div className="actions">
      <ActionButton action={editor.save} primary type="submit" />
      <ActionButton action={editor.cancel} onClick={() => post('CANCEL_EDIT', { token: editor.token })} />
    </div>
  </form>;
}

export function RemovalConfirmation({ confirmation }: { confirmation: Confirmation }) {
  const dialog = useRef<HTMLDialogElement>(null);
  const cancel = useRef<HTMLButtonElement>(null);
  const prefix = useId();
  useLayoutEffect(() => {
    const trigger = getFocusOrigin() ?? (document.activeElement instanceof HTMLElement ? document.activeElement : null);
    const element = dialog.current;
    element?.showModal();
    if (cancel.current && !cancel.current.disabled) cancel.current.focus();
    else element?.focus();
    return () => {
      element?.close();
      queueMicrotask(() => {
        if (!element?.isConnected) (getFocusOrigin() ?? (trigger?.isConnected ? trigger : null))?.focus();
      });
    };
  }, []);

  return <dialog ref={dialog} className="confirmation" tabIndex={-1} aria-labelledby={`${prefix}-title`}
    aria-describedby={`${prefix}-text`} onCancel={(event) => {
      event.preventDefault();
      if (confirmation.cancel.visible && confirmation.cancel.enabled && !confirmation.cancel.busy)
        post('CANCEL_CONFIRMATION', { token: confirmation.token });
    }}>
    <h2 id={`${prefix}-title`}>{confirmation.title}</h2>
    <p id={`${prefix}-text`}>{confirmation.text}</p>
    <div className="actions">
      <ActionButton buttonRef={cancel} action={confirmation.cancel} onClick={() => post('CANCEL_CONFIRMATION', { token: confirmation.token })} />
      <ActionButton action={confirmation.confirm} danger onClick={() => post('CONFIRM_REMOVE_PROFILE', { token: confirmation.token })} />
    </div>
  </dialog>;
}
