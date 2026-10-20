import { useId, useLayoutEffect, useRef, type RefObject } from 'react';
import type { AppEditValues, ClientState, Confirmation, DefaultsValues, Editor, EditorField, PolicyValues, PortValues, ProgramEditValues } from '../contract';
import { getFocusOrigin, post } from '../commands';
import { t } from '../i18n';
import { ActionButton } from './shared';
import { paintCaptionButtons } from './Chrome';

export type Draft =
  | { token: string; kind: 'port'; values: PortValues }
  | { token: string; kind: 'app'; values: AppEditValues }
  | { token: string; kind: 'program'; values: ProgramEditValues }
  | { token: string; kind: 'defaults'; values: DefaultsValues }
  | { token: string; kind: 'policy'; values: PolicyValues };

export function seedDraft(editor: Editor): Draft {
  switch (editor.kind) {
    case 'port': return { token: editor.token, kind: 'port', values: { ...editor.values } };
    case 'app': return { token: editor.token, kind: 'app', values: { ...editor.values } };
    case 'program': return { token: editor.token, kind: 'program', values: { ...editor.values } };
    case 'defaults': return { token: editor.token, kind: 'defaults', values: { ...editor.values } };
    case 'policy': return { token: editor.token, kind: 'policy', values: { ...editor.values } };
  }
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

export function EditorForm({ editor, draft, onChange, profileChoices, kindChoices, lossChoices }: {
  editor: Editor;
  draft: Draft;
  onChange: (draft: Draft) => void;
  profileChoices: ClientState['profileChoices'];
  kindChoices: ClientState['kindChoices'];
  lossChoices: ClientState['lossChoices'];
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

  const text = (field: EditorField & keyof PolicyValues, label: string, unit: string) => draft.kind === 'policy' ? <div className="field">
    <label htmlFor={`${prefix}-${field}`}>{t(label)}</label>
    <div className="unit-field">
      <input id={`${prefix}-${field}`} type="text" inputMode="numeric" autoComplete="off" spellCheck={false}
        value={draft.values[field] as string} disabled={saving}
        aria-invalid={!!editor.errors[field]} aria-describedby={description(field)}
        onChange={(event) => onChange({ ...draft, values: { ...draft.values, [field]: event.target.value } })} />
      <span className="secondary">{t(unit)}</span>
    </div>
    {error(field)}
  </div> : null;
  const check = (field: 'randomOrder' | 'retryAfterAuthFailure', label: string) => draft.kind === 'policy' ? <label className="check-field">
    <input type="checkbox" checked={draft.values[field]} disabled={saving}
      onChange={(event) => onChange({ ...draft, values: { ...draft.values, [field]: event.target.checked } })} />
    {t(label)}
  </label> : null;

  return <form ref={form} className="editor" tabIndex={-1}
    aria-label={t(editor.kind === 'port' ? 'SOCKS5 port' : editor.kind === 'policy' ? 'Connection settings' : editor.kind === 'program' ? 'Program methods'
      : editor.kind === 'defaults' ? 'Defaults for new apps' : 'Edit app')}
    aria-describedby={editor.errors.form ? `${prefix}-form-error` : undefined}
    aria-busy={saving} noValidate onSubmit={(event) => {
      event.preventDefault();
      if (!editor.save.visible || !editor.save.enabled || editor.save.busy) return;
      switch (draft.kind) {
        case 'port': post('SET_PORT', { token: editor.token, portText: draft.values.portText }); break;
        case 'app': post('SAVE_APP', { token: editor.token, values: draft.values }); break;
        case 'program': post('SAVE_PROGRAM', { token: editor.token, values: draft.values }); break;
        case 'defaults': post('SAVE_DEFAULTS', { token: editor.token, values: draft.values }); break;
        case 'policy': post('SAVE_POLICY', { token: editor.token, values: draft.values }); break;
      }
    }}>
    {editor.kind === 'port' && draft.kind === 'port' ? <>
      <label htmlFor={`${prefix}-port`}>{t('SOCKS5 port')}</label>
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
        <label htmlFor={`${prefix}-name`}>{t('Name')}</label>
        <input id={`${prefix}-name`} value={draft.values.name} disabled={saving}
          aria-invalid={!!editor.errors.name} aria-describedby={description('name')}
          onChange={(event) => onChange({ ...draft, values: { ...draft.values, name: event.target.value } })} />
        {error('name')}
      </div>
      <div className="field">
        <label htmlFor={`${prefix}-profile`}>{t('VPN profile')}</label>
        <select id={`${prefix}-profile`} value={draft.values.profileId ?? ''} disabled={saving}
          aria-invalid={!!editor.errors.profileId} aria-describedby={description('profileId')}
          onChange={(event) => onChange({ ...draft, values: { ...draft.values, profileId: event.target.value || null } })}>
          <option value="" hidden disabled />
          {profileChoices.map((choice) => <option key={choice.id} value={choice.id} disabled={!choice.enabled}>{choice.label}</option>)}
        </select>
        {error('profileId')}
      </div>
      {editor.options && editor.options.length > 0 && <div className="field">
        <label htmlFor={`${prefix}-launch`}>{t('Launch starts')}</label>
        <select id={`${prefix}-launch`} value={draft.values.launchProgramId ?? ''} disabled={saving}
          aria-invalid={!!editor.errors.launchProgramId} aria-describedby={description('launchProgramId')}
          onChange={(event) => onChange({ ...draft, values: { ...draft.values, launchProgramId: event.target.value || null } })}>
          <option value="">{t('Nothing chosen')}</option>
          {editor.options.map((option) => <option key={option.id} value={option.id}>{option.label}</option>)}
        </select>
        {error('launchProgramId')}
      </div>}
      <fieldset className="field full-field loss-choices" aria-describedby={description('loss')}>
        <legend>{t('When the VPN drops (all-traffic programs)')}</legend>
        {lossChoices.map((choice) => <label key={choice.value} className="radio-field">
          <input type="radio" name={`${prefix}-loss`} value={choice.value} checked={draft.values.loss === choice.value} disabled={saving}
            onChange={() => onChange({ ...draft, values: { ...draft.values, loss: choice.value } })} />
          <span><span>{choice.label}</span><span className="secondary metadata">{choice.help}</span></span>
        </label>)}
        {error('loss')}
      </fieldset>
      {draft.values.loss === 'hold' && <div className="field">
        <label htmlFor={`${prefix}-after`}>{t('Use my own connection after')}</label>
        <div className="unit-field">
          <input id={`${prefix}-after`} type="text" inputMode="numeric" autoComplete="off" spellCheck={false}
            value={draft.values.directAfterText} disabled={saving} placeholder={t('never')}
            aria-invalid={!!editor.errors.directAfterText} aria-describedby={description('directAfterText')}
            onChange={(event) => onChange({ ...draft, values: { ...draft.values, directAfterText: event.target.value } })} />
          <span className="secondary">{t('seconds')}</span>
        </div>
        {error('directAfterText')}
      </div>}
    </div> : draft.kind === 'program' ? <div className="editor-fields">
      <fieldset className="field full-field">
        <legend>{t('How its traffic reaches the VPN')}</legend>
        <label className="check-field">
          <input type="checkbox" checked={draft.values.packets} disabled={saving}
            onChange={(event) => onChange({ ...draft, values: { ...draft.values, packets: event.target.checked } })} />
          <span><span>{t('All traffic — packet driver')}</span><span className="secondary metadata">{t('Everything it sends, TCP and UDP, while the app is routed.')}</span></span>
        </label>
        <label className="check-field">
          <input type="checkbox" checked={draft.values.proxy} disabled={saving}
            onChange={(event) => onChange({ ...draft, values: { ...draft.values, proxy: event.target.checked } })} />
          <span><span>{t('Proxy settings when launched here')}</span><span className="secondary metadata">{t('Started from Sallyport with the profile’s proxy. TCP only.')}</span></span>
        </label>
      </fieldset>
      {draft.values.proxy && <>
        <div className="field">
          <label htmlFor={`${prefix}-kind`}>{t('How it is told about the proxy')}</label>
          <select id={`${prefix}-kind`} value={draft.values.kind} disabled={saving}
            aria-invalid={!!editor.errors.kind} aria-describedby={description('kind')}
            onChange={(event) => onChange({ ...draft, values: { ...draft.values, kind: event.target.value as ProgramEditValues['kind'] } })}>
            {kindChoices.map((choice) => <option key={choice.value} value={choice.value}>{choice.label}</option>)}
          </select>
          <p className="secondary kind-help">{kindChoices.find((choice) => choice.value === draft.values.kind)?.help}</p>
          {error('kind')}
        </div>
      </>}
      <div className="field full-field">
        <label htmlFor={`${prefix}-arguments`}>{t('Extra arguments when launched')}</label>
        <textarea id={`${prefix}-arguments`} rows={2} value={draft.values.arguments} disabled={saving} spellCheck={false}
          aria-invalid={!!editor.errors.arguments} aria-describedby={description('arguments')}
          onChange={(event) => onChange({ ...draft, values: { ...draft.values, arguments: event.target.value } })} />
        {error('arguments')}
      </div>
    </div> : draft.kind === 'defaults' ? <div className="editor-fields">
      <div className="field">
        <label htmlFor={`${prefix}-profile`}>{t('VPN profile')}</label>
        <select id={`${prefix}-profile`} value={draft.values.profileId ?? ''} disabled={saving}
          aria-invalid={!!editor.errors.profileId} aria-describedby={description('profileId')}
          onChange={(event) => onChange({ ...draft, values: { ...draft.values, profileId: event.target.value || null } })}>
          <option value="">{t('The first that can connect')}</option>
          {profileChoices.map((choice) => <option key={choice.id} value={choice.id} disabled={!choice.enabled}>{choice.label}</option>)}
        </select>
        {error('profileId')}
      </div>
      <fieldset className="field full-field loss-choices" aria-describedby={description('loss')}>
        <legend>{t('When the VPN drops')}</legend>
        {lossChoices.map((choice) => <label key={choice.value} className="radio-field">
          <input type="radio" name={`${prefix}-loss`} value={choice.value} checked={draft.values.loss === choice.value} disabled={saving}
            onChange={() => onChange({ ...draft, values: { ...draft.values, loss: choice.value } })} />
          <span><span>{choice.label}</span><span className="secondary metadata">{choice.help}</span></span>
        </label>)}
        {error('loss')}
      </fieldset>
      {draft.values.loss === 'hold' && <div className="field">
        <label htmlFor={`${prefix}-after`}>{t('Use my own connection after')}</label>
        <div className="unit-field">
          <input id={`${prefix}-after`} type="text" inputMode="numeric" autoComplete="off" spellCheck={false}
            value={draft.values.directAfterText} disabled={saving} placeholder={t('never')}
            aria-invalid={!!editor.errors.directAfterText} aria-describedby={description('directAfterText')}
            onChange={(event) => onChange({ ...draft, values: { ...draft.values, directAfterText: event.target.value } })} />
          <span className="secondary">{t('seconds')}</span>
        </div>
        {error('directAfterText')}
      </div>}
    </div> : draft.kind === 'policy' ? <div className="editor-fields">
      {text('retryWaitText', 'Wait between attempts', 'seconds')}
      {text('retryWaitMaxText', 'Longest wait', 'seconds')}
      {text('roundsMaxText', 'Give up after (0: never)', 'rounds')}
      {text('attemptTimeoutText', 'Time for a server to answer', 'seconds')}
      {text('pingRestartText', 'Lost after silence of', 'seconds')}
      <div className="field checks">
        {check('randomOrder', 'Try the servers in random order')}
        {check('retryAfterAuthFailure', 'Keep trying after the server refuses the login')}
      </div>
    </div> : null}
    {editor.hint && <p className="secondary" id={`${prefix}-hint`}>{editor.hint}</p>}
    {error('form')}
    <div className="actions">
      <ActionButton action={editor.save} primary type="submit" />
      <ActionButton action={editor.cancel} onClick={() => post('CANCEL_EDIT', { token: editor.token })} />
      <ActionButton action={editor.reset} onClick={() => post('RESET_POLICY', { token: editor.token })} />
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
    paintCaptionButtons(true);
    if (cancel.current && !cancel.current.disabled) cancel.current.focus();
    else element?.focus();
    return () => {
      element?.close();
      paintCaptionButtons(false);
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
      <ActionButton action={confirmation.confirm} primary onClick={() => post('CONFIRM', { token: confirmation.token })} />
    </div>
  </dialog>;
}
