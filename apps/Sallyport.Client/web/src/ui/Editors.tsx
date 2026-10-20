import { useId, useLayoutEffect, useRef, useState, type RefObject } from 'react';
import type { AppEditValues, ClientState, Confirmation, DefaultsValues, Editor, EditorField, NewAppValues, PolicyValues, ProgramEditValues } from '../contract';
import { getFocusOrigin, post } from '../commands';
import { t } from '../i18n';
import { ActionButton, Disclosure, Icon } from './shared';
import { paintCaptionButtons } from './Chrome';

export type Draft =
  | { token: string; kind: 'app'; values: AppEditValues }
  | { token: string; kind: 'program'; values: ProgramEditValues }
  | { token: string; kind: 'defaults'; values: DefaultsValues }
  | { token: string; kind: 'policy'; values: PolicyValues }
  | { token: string; kind: 'new-app'; values: NewAppValues };

export function seedDraft(editor: Editor): Draft {
  switch (editor.kind) {
    case 'app': return { token: editor.token, kind: 'app', values: { ...editor.values } };
    case 'program': return { token: editor.token, kind: 'program', values: { ...editor.values } };
    case 'defaults': return { token: editor.token, kind: 'defaults', values: { ...editor.values } };
    case 'policy': return { token: editor.token, kind: 'policy', values: { ...editor.values } };
    case 'new-app': return { token: editor.token, kind: 'new-app', values: { ...editor.values, included: [...editor.values.included] } };
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
  // The finer settings stay folded until asked for, or until one of them is what a save refused.
  const [moreAsked, setMoreAsked] = useState(false);
  const moreOpen = moreAsked || (['retryWaitText', 'retryWaitMaxText', 'attemptTimeoutText', 'pingRestartText'] as const).some((f) => editor.errors[f]);
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
    aria-label={t(editor.kind === 'policy' ? 'Connection settings' : editor.kind === 'program' ? 'Program methods'
      : editor.kind === 'defaults' ? 'Defaults for new apps' : 'Edit app')}
    aria-describedby={editor.errors.form ? `${prefix}-form-error` : undefined}
    aria-busy={saving} noValidate onSubmit={(event) => {
      event.preventDefault();
      if (!editor.save.visible || !editor.save.enabled || editor.save.busy) return;
      switch (draft.kind) {
        case 'app': post('SAVE_APP', { token: editor.token, values: draft.values }); break;
        case 'program': post('SAVE_PROGRAM', { token: editor.token, values: draft.values }); break;
        case 'defaults': post('SAVE_DEFAULTS', { token: editor.token, values: draft.values }); break;
        case 'policy': post('SAVE_POLICY', { token: editor.token, values: draft.values }); break;
      }
    }}>
    {draft.kind === 'app' ? <div className="editor-fields">
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
          <span><span>{t('All traffic — packet driver')}</span><span className="secondary metadata">{t('Everything it sends, TCP and UDP, while the app’s VPN is on.')}</span></span>
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
      {editor.options && <fieldset className="field full-field" aria-describedby={description('protocol')}>
        <legend>{t('VPN protocol')}</legend>
        {editor.options.map((choice) => <label key={choice.id} className="radio-field">
          <input type="radio" name={`${prefix}-protocol`} value={choice.id} checked={draft.values.protocol === choice.id} disabled={saving}
            onChange={() => onChange({ ...draft, values: { ...draft.values, protocol: choice.id } })} />
          <span><span>{choice.label}</span>{choice.help && <span className="secondary metadata">{choice.help}</span>}</span>
        </label>)}
        {error('protocol')}
      </fieldset>}
      <fieldset className="field full-field">
        <legend>{t('When it cannot connect')}</legend>
        <label className="radio-field">
          <input type="radio" name={`${prefix}-retry`} checked={draft.values.keepTrying} disabled={saving}
            onChange={() => onChange({ ...draft, values: { ...draft.values, keepTrying: true } })} />
          <span><span>{t('Keep trying until I disconnect')}</span></span>
        </label>
        <label className="radio-field">
          <input type="radio" name={`${prefix}-retry`} checked={!draft.values.keepTrying} disabled={saving}
            onChange={() => onChange({ ...draft, values: { ...draft.values, keepTrying: false } })} />
          <span><span>{t('Stop after a number of rounds')}</span><span className="secondary metadata">{t('A round tries every server once.')}</span></span>
        </label>
      </fieldset>
      {!draft.values.keepTrying && text('roundsMaxText', 'Stop after', 'rounds')}
      <div className="full-field more-toggle">
        <Disclosure open={moreOpen} controls={`${prefix}-more`} onClick={() => setMoreAsked(!moreOpen)}>{t('More settings')}</Disclosure>
      </div>
      <div id={`${prefix}-more`} hidden={!moreOpen} className="editor-fields full-field">
        {text('retryWaitText', 'Wait between attempts', 'seconds')}
        {text('retryWaitMaxText', 'Longest wait', 'seconds')}
        {text('attemptTimeoutText', 'Time for a server to answer', 'seconds')}
        {text('pingRestartText', 'Lost after silence of', 'seconds')}
        <div className="field full-field">
          {check('randomOrder', 'Try the servers in random order')}
          {check('retryAfterAuthFailure', 'Keep trying after the server refuses the login')}
        </div>
        {editor.hint && <p className="secondary full-field" id={`${prefix}-hint`}>{editor.hint}</p>}
      </div>
    </div> : null}
    {editor.hint && editor.kind !== 'policy' && <p className="secondary" id={`${prefix}-hint`}>{editor.hint}</p>}
    {error('form')}
    <div className="actions">
      <ActionButton action={editor.save} primary type="submit" />
      <ActionButton action={editor.cancel} onClick={() => post('CANCEL_EDIT', { token: editor.token })} />
      <ActionButton action={editor.reset} onClick={() => post('RESET_POLICY', { token: editor.token })} />
    </div>
  </form>;
}

/** "Add an app", a page of its own: first where it is — typed, pasted or browsed to, a folder or a program — and what
 *  was found there; then its name, VPN profile and what its traffic does when the VPN drops; then, for a folder, which
 *  of its programs go through the VPN. Nothing is kept until Add app. */
export function NewAppForm({ editor, draft, onChange, profileChoices, lossChoices }: {
  editor: Editor;
  draft: Draft;
  onChange: (draft: Draft) => void;
  profileChoices: ClientState['profileChoices'];
  lossChoices: ClientState['lossChoices'];
}) {
  const form = useRef<HTMLFormElement>(null);
  const prefix = useId();
  useEditorFocus(form);
  if (draft.kind !== 'new-app' || editor.kind !== 'new-app') return null;
  const values = draft.values;
  const found = editor.save.enabled && !editor.errors.path;
  const update = (next: Partial<NewAppValues>) => onChange({ ...draft, values: { ...values, ...next } });
  // The host looks only when the place changed: leaving the field, Enter, or Browse.
  const locate = () => { if (values.path.trim() !== editor.values.path) post('NEW_APP_LOCATION', { token: editor.token, values }); };
  const error = (field: EditorField) => editor.errors[field]
    ? <p className="field-error" role="alert" id={`${prefix}-${field}-error`}>{editor.errors[field]}</p> : null;
  const included = new Set(values.included);

  return <form ref={form} className="new-app" tabIndex={-1} aria-label={t('Add an app')} noValidate onSubmit={(event) => {
    event.preventDefault();
    if (editor.save.enabled) post('SAVE_NEW_APP', { token: editor.token, values });
  }}>
    <section className="card setup-step" aria-labelledby={`${prefix}-where`}>
      <h2 id={`${prefix}-where`}>{t('Choose your game or program')}</h2>
      <p className="secondary">{t('Its folder or its .exe. For a folder, Sallyport picks which of its programs use the VPN; you can change that below.')}</p>
      <div className="path-field">
        <input id={`${prefix}-path`} type="text" autoComplete="off" spellCheck={false} value={values.path} aria-label={t('Choose your game or program')}
          placeholder={t('For example D:\\Games\\MyGame')} aria-invalid={!!editor.errors.path}
          aria-describedby={editor.errors.path ? `${prefix}-path-error` : undefined}
          onChange={(event) => update({ path: event.target.value })} onBlur={locate}
          onKeyDown={(event) => { if (event.key === 'Enter') { event.preventDefault(); locate(); } }} />
        {/* Pressed without taking the focus: leaving the field would look at the place typed and renew the form under it. */}
        <button type="button" onMouseDown={(event) => event.preventDefault()}
          onClick={() => post('NEW_APP_BROWSE', { token: editor.token, values })}>{t('Choose…')}</button>
      </div>
      {error('path')}
      {found && editor.hint && <p className="found"><Icon name="check" /><span>{editor.hint}</span></p>}
    </section>

    {found && <section className="card setup-step" aria-labelledby={`${prefix}-about`}>
      <h2 id={`${prefix}-about`}>{t('About it')}</h2>
      <div className="editor-fields">
        <div className="field">
          <label htmlFor={`${prefix}-name`}>{t('Name')}</label>
          <input id={`${prefix}-name`} type="text" autoComplete="off" value={values.name} aria-invalid={!!editor.errors.name}
            onChange={(event) => update({ name: event.target.value })} />
          {error('name')}
        </div>
        <div className="field">
          <label htmlFor={`${prefix}-profile`}>{t('VPN profile')}</label>
          <select id={`${prefix}-profile`} value={values.profileId ?? ''} aria-invalid={!!editor.errors.profileId}
            onChange={(event) => update({ profileId: event.target.value || null })}>
            <option value="">{t('Choose later')}</option>
            {profileChoices.map((choice) => <option key={choice.id} value={choice.id} disabled={!choice.enabled}>{choice.label}</option>)}
          </select>
          {error('profileId')}
        </div>
        <fieldset className="field full-field">
          <legend>{t('When the VPN drops')}</legend>
          {lossChoices.map((choice) => <label key={choice.value} className="radio-field">
            <input type="radio" name={`${prefix}-loss`} value={choice.value} checked={values.loss === choice.value}
              onChange={() => update({ loss: choice.value })} />
            <span><span>{choice.label}</span><span className="secondary metadata">{choice.help}</span></span>
          </label>)}
          {error('loss')}
        </fieldset>
        {values.loss === 'hold' && <div className="field">
          <label htmlFor={`${prefix}-after`}>{t('Use my own connection after')}</label>
          <div className="unit-field">
            <input id={`${prefix}-after`} type="text" inputMode="numeric" autoComplete="off" spellCheck={false} placeholder={t('never')}
              value={values.directAfterText} aria-invalid={!!editor.errors.directAfterText}
              onChange={(event) => update({ directAfterText: event.target.value })} />
            <span className="secondary">{t('seconds')}</span>
          </div>
          {error('directAfterText')}
        </div>}
      </div>
    </section>}

    {found && editor.options && <section className="card setup-step" aria-labelledby={`${prefix}-programs`}>
      <h2 id={`${prefix}-programs`}>{t('Its programs')}</h2>
      <p className="secondary">{t('On: its traffic goes through your VPN while the app’s VPN is on. Off: it keeps your own connection.')}</p>
      <div className="program-list-head" aria-hidden="true"><span>{t('Program')}</span><span>{t('Use VPN')}</span></div>
      <ul className="program-list">
        {editor.options.map((option) => <li key={option.id} className={included.has(option.id) ? 'included' : 'left-out'}>
          <div className="program-head setup-program">
            <div className="program-name">
              <span className="program-title">{option.label}</span>
              {option.help && <span className="metadata secondary">{option.help}</span>}
            </div>
            <button type="button" role="switch" aria-checked={included.has(option.id)} className="switch" aria-label={option.label}
              onClick={() => update({ included: included.has(option.id) ? values.included.filter((path) => path !== option.id) : [...values.included, option.id] })}>
              <span className="knob" aria-hidden="true" />
            </button>
          </div>
        </li>)}
      </ul>
    </section>}

    {error('form')}
    <div className="actions setup-actions">
      <ActionButton action={editor.save} primary type="submit" />
      <ActionButton action={editor.cancel} onClick={() => post('CANCEL_EDIT', { token: editor.token })} />
    </div>
    {/* Why Add app waits, under the pair rather than between them. */}
    {!editor.save.enabled && editor.save.reason && <p className="secondary metadata">{editor.save.reason}</p>}
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
