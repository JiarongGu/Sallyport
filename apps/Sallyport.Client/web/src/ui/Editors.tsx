import { useId, useLayoutEffect, useRef, useState, type RefObject } from 'react';
import type { AppEditValues, ClientState, Confirmation, Editor, EditorField, MoveValues, NewAppValues, NewProfileValues, PolicyValues, ProfileEditValues, ProgramEditValues, SignInView } from '../contract';
import { getFocusOrigin, post } from '../commands';
import { t } from '../i18n';
import { ActionButton, Disclosure, Facts, Icon } from './shared';
import { paintCaptionButtons } from './Chrome';

export type Draft =
  | { token: string; kind: 'app'; values: AppEditValues }
  | { token: string; kind: 'program'; values: ProgramEditValues }
  | { token: string; kind: 'new-app'; values: NewAppValues }
  | { token: string; kind: 'new-profile'; values: NewProfileValues }
  | { token: string; kind: 'profile-edit'; values: ProfileEditValues }
  | { token: string; kind: 'export' | 'import'; values: MoveValues };

export function seedDraft(editor: Editor): Draft {
  switch (editor.kind) {
    case 'app': return { token: editor.token, kind: 'app', values: { ...editor.values } };
    case 'program': return { token: editor.token, kind: 'program', values: { ...editor.values } };
    case 'new-app': return { token: editor.token, kind: 'new-app', values: { ...editor.values, included: [...editor.values.included] } };
    case 'new-profile': return { token: editor.token, kind: 'new-profile', values: { ...editor.values } };
    case 'profile-edit': return { token: editor.token, kind: 'profile-edit', values: { ...editor.values, policy: editor.values.policy && { ...editor.values.policy } } };
    case 'export': case 'import': return { token: editor.token, kind: editor.kind, values: { ...editor.values } };
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

export function EditorForm({ editor, draft, onChange, kindChoices, lossChoices }: {
  editor: Editor;
  draft: Draft;
  onChange: (draft: Draft) => void;
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

  return <form ref={form} className="editor" tabIndex={-1}
    aria-label={t(editor.kind === 'program' ? 'Program methods'
      : editor.kind === 'export' ? 'Export' : editor.kind === 'import' ? 'Import' : 'Edit app')}
    aria-describedby={editor.errors.form ? `${prefix}-form-error` : undefined}
    aria-busy={saving} noValidate onSubmit={(event) => {
      event.preventDefault();
      if (!editor.save.visible || !editor.save.enabled || editor.save.busy) return;
      switch (draft.kind) {
        case 'app': post('SAVE_APP', { token: editor.token, values: draft.values }); break;
        case 'program': post('SAVE_PROGRAM', { token: editor.token, values: draft.values }); break;
        case 'export': post('SAVE_EXPORT', { token: editor.token, values: draft.values }); break;
        case 'import': post('SAVE_IMPORT', { token: editor.token, values: draft.values }); break;
      }
    }}>
    {(draft.kind === 'export' || draft.kind === 'import') && <MoveFields editor={editor} draft={draft} onChange={onChange} prefix={prefix}
      saving={saving} error={error} description={description} />}
    {draft.kind === 'app' ? <div className="editor-fields">
      <div className="field">
        <label htmlFor={`${prefix}-name`}>{t('Name')}</label>
        <input id={`${prefix}-name`} value={draft.values.name} disabled={saving}
          aria-invalid={!!editor.errors.name} aria-describedby={description('name')}
          onChange={(event) => onChange({ ...draft, values: { ...draft.values, name: event.target.value } })} />
        {error('name')}
      </div>
      <IconChoices icons={editor.icons} chosen={draft.values.iconProgramId} name={`${prefix}-icon`} disabled={saving}
        onChoose={(id) => onChange({ ...draft, values: { ...draft.values, iconProgramId: id } })} />
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
    </div> : null}
    {editor.hint && <p className="secondary" id={`${prefix}-hint`}>{editor.hint}</p>}
    {error('form')}
    <div className="actions">
      <ActionButton action={editor.save} primary type="submit" />
      <ActionButton action={editor.cancel} onClick={() => post('CANCEL_EDIT', { token: editor.token })} />
    </div>
  </form>;
}

/** A profile's own connection settings, on its Edit page: the VPN protocol where it has servers of more than one, what
 *  it does when it cannot connect, and the finer settings folded until asked for or refused. */
function PolicyFields({ editor, values, onChange, prefix }: {
  editor: Editor;
  values: PolicyValues;
  onChange: (values: PolicyValues) => void;
  prefix: string;
}) {
  const [moreAsked, setMoreAsked] = useState(false);
  const moreOpen = moreAsked || (['retryWaitText', 'retryWaitMaxText', 'attemptTimeoutText', 'pingRestartText'] as const).some((f) => editor.errors[f]);
  const saving = editor.phase === 'saving';
  const error = (field: EditorField) => editor.errors[field]
    ? <p className="field-error" role="alert" id={`${prefix}-${field}-error`}>{editor.errors[field]}</p> : null;
  const text = (field: EditorField & keyof PolicyValues, label: string, unit: string) => <div className="field">
    <label htmlFor={`${prefix}-${field}`}>{t(label)}</label>
    <div className="unit-field">
      <input id={`${prefix}-${field}`} type="text" inputMode="numeric" autoComplete="off" spellCheck={false}
        value={values[field] as string} disabled={saving}
        aria-invalid={!!editor.errors[field]} aria-describedby={editor.errors[field] ? `${prefix}-${field}-error` : undefined}
        onChange={(event) => onChange({ ...values, [field]: event.target.value })} />
      <span className="secondary">{t(unit)}</span>
    </div>
    {error(field)}
  </div>;
  const check = (field: 'randomOrder' | 'retryAfterAuthFailure', label: string) => <label className="check-field">
    <input type="checkbox" checked={values[field]} disabled={saving} onChange={(event) => onChange({ ...values, [field]: event.target.checked })} />
    {t(label)}
  </label>;

  return <div className="editor-fields">
    {editor.options && <fieldset className="field full-field">
      <legend>{t('VPN protocol')}</legend>
      {editor.options.map((choice) => <label key={choice.id} className="radio-field">
        <input type="radio" name={`${prefix}-protocol`} value={choice.id} checked={values.protocol === choice.id} disabled={saving}
          onChange={() => onChange({ ...values, protocol: choice.id })} />
        <span><span>{choice.label}</span>{choice.help && <span className="secondary metadata">{choice.help}</span>}</span>
      </label>)}
      {error('protocol')}
    </fieldset>}
    <fieldset className="field full-field">
      <legend>{t('When it cannot connect')}</legend>
      <label className="radio-field">
        <input type="radio" name={`${prefix}-retry`} checked={values.keepTrying} disabled={saving} onChange={() => onChange({ ...values, keepTrying: true })} />
        <span><span>{t('Keep trying until I disconnect')}</span></span>
      </label>
      <label className="radio-field">
        <input type="radio" name={`${prefix}-retry`} checked={!values.keepTrying} disabled={saving} onChange={() => onChange({ ...values, keepTrying: false })} />
        <span><span>{t('Stop after a number of rounds')}</span><span className="secondary metadata">{t('A round tries every server once.')}</span></span>
      </label>
    </fieldset>
    {!values.keepTrying && text('roundsMaxText', 'Stop after', 'rounds')}
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
    </div>
  </div>;
}

/** "Add an app", a page of its own: first where it is — typed, pasted or browsed to, a folder or a program — and what
 *  was found there; then its name, VPN profile and what its traffic does when the VPN drops; then, for a folder, which
 *  of its programs go through the VPN. Nothing is kept until Add app. */
/** Export's and Import's fields: each part the file can hold, with how much of it, and the password the profiles are
 *  sealed with — twice for an export. */
function MoveFields({ editor, draft, onChange, prefix, saving, error, description }: {
  editor: Editor; draft: Extract<Draft, { kind: 'export' | 'import' }>; onChange: (draft: Draft) => void; prefix: string; saving: boolean;
  error: (field: EditorField) => React.ReactNode; description: (field: EditorField) => string | undefined;
}) {
  const set = (values: Partial<MoveValues>) => onChange({ ...draft, values: { ...draft.values, ...values } });
  const part = (id: 'profiles' | 'apps' | 'connection') => editor.options?.find((option) => option.id === id);
  return <div className="editor-fields">
    <fieldset className="field full-field">
      <legend>{t(draft.kind === 'export' ? 'What goes in the file' : 'What to take from the file')}</legend>
      {/* The parts the host offers, in its order: what this computer has, or what the file holds. */}
      {(editor.options ?? []).map((option) => {
        const id = option.id as 'profiles' | 'apps' | 'connection';
        return <label key={id} className="check-field">
          <input type="checkbox" checked={draft.values[id]} disabled={saving} onChange={(event) => set({ [id]: event.target.checked })} />
          <span><span>{option.label}</span>{option.help && <span className="secondary metadata">{option.help}</span>}</span>
        </label>;
      })}
    </fieldset>
    {draft.values.profiles && part('profiles') && draft.values.sealed && <>
      <div className="field">
        <label htmlFor={`${prefix}-password`}>{t(draft.kind === 'export' ? 'Password for the profiles (optional)' : 'The file\'s password')}</label>
        <input id={`${prefix}-password`} type="password" autoComplete={draft.kind === 'export' ? 'new-password' : 'current-password'}
          value={draft.values.password} disabled={saving} aria-invalid={!!editor.errors.password} aria-describedby={description('password')}
          onChange={(event) => set({ password: event.target.value })} />
        {error('password')}
      </div>
      {draft.kind === 'export' && <div className="field">
        <label htmlFor={`${prefix}-confirm`}>{t('The password again')}</label>
        <input id={`${prefix}-confirm`} type="password" autoComplete="new-password" value={draft.values.confirm} disabled={saving}
          aria-invalid={!!editor.errors.confirm} aria-describedby={description('confirm')} onChange={(event) => set({ confirm: event.target.value })} />
        {error('confirm')}
      </div>}
    </>}
  </div>;
}

/** An app's icon, picked by sight from its programs' own (the owner: "set app icon from app exe"), on Edit and on Add an
 *  app alike; the first stands for the one chosen when that is not among them. */
function IconChoices({ icons, chosen, name, disabled = false, onChoose }: {
  icons: Editor['icons']; chosen: string | null; name: string; disabled?: boolean; onChoose: (id: string) => void;
}) {
  if (!icons || icons.length === 0) return null;
  const shown = icons.some((icon) => icon.id === chosen) ? chosen : icons[0].id;
  return <fieldset className="field full-field icon-choices">
    <legend>{t('Icon')}</legend>
    {icons.map((icon) => <label key={icon.id} className="icon-choice" title={icon.label}>
      <input type="radio" name={name} value={icon.id} disabled={disabled} checked={icon.id === shown} onChange={() => onChoose(icon.id)} />
      <img src={icon.url} alt="" aria-hidden="true" draggable={false} />
      <span>{icon.label}</span>
    </label>)}
  </fieldset>;
}

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
  const [lossAsked, setLossAsked] = useState(false);
  if (draft.kind !== 'new-app' || editor.kind !== 'new-app') return null;
  const lossOpen = lossAsked || !!editor.errors.loss || !!editor.errors.directAfterText;
  const values = draft.values;
  const found = editor.save.enabled && !editor.errors.path;
  const update = (next: Partial<NewAppValues>) => onChange({ ...draft, values: { ...values, ...next } });
  // The host looks only when the place changed: leaving the field, Enter, or Browse.
  const locate = () => { if (values.path.trim() !== editor.values.path) post('NEW_APP_LOCATION', { token: editor.token, values }); };
  const error = (field: EditorField) => editor.errors[field]
    ? <p className="field-error" role="alert" id={`${prefix}-${field}-error`}>{editor.errors[field]}</p> : null;
  const included = new Set(values.included);
  const through = new Set(values.profileIds);

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

    {/* Which VPNs: many to many (D23-D24) — in each one ticked, its Use VPN on. The one it was opened from is ticked. */}
    {found && <section className="card setup-step" aria-labelledby={`${prefix}-through`}>
      <h2 id={`${prefix}-through`}>{t('Which VPN it goes through')}</h2>
      {profileChoices.length === 0
        ? <p className="secondary">{t('You have no VPN profile yet. Add the app now, and put it in one once you have imported it.')}</p>
        : <>
          <p className="secondary">{t('Its Use VPN is on in each one ticked: it goes through whichever of them is connected. You can change this on the Overview.')}</p>
          <div className="profile-picks" role="group" aria-labelledby={`${prefix}-through`}>
            {profileChoices.map((choice) => <label key={choice.id} className="check-field profile-pick">
              <input type="checkbox" checked={through.has(choice.id)} disabled={!choice.enabled}
                onChange={() => update({ profileIds: through.has(choice.id) ? values.profileIds.filter((id) => id !== choice.id) : [...values.profileIds, choice.id] })} />
              <span>{choice.label}</span>
            </label>)}
          </div>
        </>}
      {error('profileId')}
    </section>}

    {found && <section className="card setup-step" aria-labelledby={`${prefix}-about`}>
      <h2 id={`${prefix}-about`}>{t('About it')}</h2>
      <div className="editor-fields">
        <div className="field">
          <label htmlFor={`${prefix}-name`}>{t('Name')}</label>
          <input id={`${prefix}-name`} type="text" autoComplete="off" value={values.name} aria-invalid={!!editor.errors.name}
            onChange={(event) => update({ name: event.target.value })} />
          {error('name')}
        </div>
        <IconChoices icons={editor.icons} chosen={values.iconPath} name={`${prefix}-icon`} onChoose={(id) => update({ iconPath: id })} />
        {/* What its traffic does when the VPN drops: the safe choice is already made, so it is folded under what it is. */}
        <div className="field full-field more-toggle">
          <Disclosure open={lossOpen} controls={`${prefix}-loss`} onClick={() => setLossAsked(!lossOpen)}>
            {t('When the VPN drops: {0}', lossChoices.find((choice) => choice.value === values.loss)?.label ?? '')}
          </Disclosure>
        </div>
        <fieldset id={`${prefix}-loss`} hidden={!lossOpen} className="field full-field">
          <legend className="sr-only">{t('When the VPN drops')}</legend>
          {lossChoices.map((choice) => <label key={choice.value} className="radio-field">
            <input type="radio" name={`${prefix}-loss`} value={choice.value} checked={values.loss === choice.value}
              onChange={() => update({ loss: choice.value })} />
            <span><span>{choice.label}</span><span className="secondary metadata">{choice.help}</span></span>
          </label>)}
          {error('loss')}
        </fieldset>
        {lossOpen && values.loss === 'hold' && <div className="field">
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

/** "Import a VPN profile" (D29, docs/design/profile-flow.md): a file (chosen, or dropped anywhere on the window) or a
 *  link first; then one review in its place — the profile's servers, locked, where it came from, its name, and its
 *  sign-in when it has one — before anything is kept. */
export function NewProfileForm({ editor, draft, onChange }: { editor: Editor; draft: Draft; onChange: (draft: Draft) => void }) {
  const form = useRef<HTMLFormElement>(null);
  const prefix = useId();
  useEditorFocus(form);
  if (draft.kind !== 'new-profile' || editor.kind !== 'new-profile') return null;
  const values = draft.values;
  const read = editor.facts !== null;
  const fetching = editor.phase === 'fetching';
  const cannot = !!editor.hint;
  const update = (next: Partial<NewProfileValues>) => onChange({ ...draft, values: { ...values, ...next } });
  const fetch = () => { if (!fetching) post('PROFILE_IMPORT_LINK', { token: editor.token, values }); };
  const error = (field: EditorField) => editor.errors[field]
    ? <p className="field-error" role="alert" id={`${prefix}-${field}-error`}>{editor.errors[field]}</p> : null;
  const source = (value: NewProfileValues['source'], label: string) => <button type="button" role="radio" aria-checked={values.source === value}
    className={values.source === value ? 'selected' : ''} onClick={() => update({ source: value })}>{t(label)}</button>;
  const host = (() => { try { return new URL(values.link.includes('://') ? values.link : `https://${values.link}`).host; } catch { return values.link; } })();
  // A password is asked for only when it is about to be used: to remember it, or to connect now.
  const askPassword = values.needsLogin && (values.remember || values.connect);

  return <form ref={form} className="new-app new-profile" tabIndex={-1} aria-label={t('Import a VPN profile')} noValidate onSubmit={(event) => {
    event.preventDefault();
    if (read && editor.save.enabled) post('SAVE_PROFILE_IMPORT', { token: editor.token, values });
  }}>
    {!read ? <section className="card setup-step" aria-labelledby={`${prefix}-where`}>
      <h2 id={`${prefix}-where`}>{t('Where is your profile?')}</h2>
      <div className="segmented" role="radiogroup" aria-labelledby={`${prefix}-where`}>
        {source('file', 'A file')}
        {source('link', 'A link')}
      </div>
      {values.source === 'file' ? <>
        {/* Dropping works on the whole window; here is where it is said. */}
        <div className="drop-zone">
          <Icon name="down" />
          <p>{t('Drop your .ovpn file here')}</p>
          <button type="button" onClick={() => post('PROFILE_IMPORT_BROWSE', { token: editor.token, values })}>{t('Choose a file…')}</button>
        </div>
        {error('form')}
      </> : <>
        <p className="secondary">{t('Enter your provider’s server address or a direct link to your profile.')}</p>
        <div className="path-field">
          <input id={`${prefix}-link`} type="url" autoComplete="url" spellCheck={false} value={values.link} aria-label={t('Address or link')}
            placeholder="https://vpn.example.com" aria-invalid={!!editor.errors.link}
            aria-describedby={editor.errors.link ? `${prefix}-link-error` : undefined}
            onChange={(event) => update({ link: event.target.value })}
            onKeyDown={(event) => { if (event.key === 'Enter') { event.preventDefault(); fetch(); } }} />
          {!values.signIn && <button type="button" disabled={fetching} aria-busy={fetching} onClick={fetch}>{fetching ? t('Getting profile…') : t('Get the profile')}</button>}
        </div>
        {error('link')}
        {/* The provider's own sign-in, to download the profile: not the VPN's. */}
        {values.signIn && <fieldset className="inset sign-in">
          <legend>{t('Sign in to {0}', host)}</legend>
          <p className="secondary metadata">{t('This sign-in downloads your profile. It does not connect the VPN.')}</p>
          <div className="editor-fields">
            <div className="field">
              <label htmlFor={`${prefix}-provider-user`}>{t('Username')}</label>
              <input id={`${prefix}-provider-user`} type="text" autoComplete="username" spellCheck={false} value={values.username}
                onChange={(event) => update({ username: event.target.value })} />
            </div>
            <div className="field">
              <label htmlFor={`${prefix}-provider-password`}>{t('Password')}</label>
              <input id={`${prefix}-provider-password`} type="password" autoComplete="current-password" value={values.password}
                aria-invalid={!!editor.errors.password}
                onChange={(event) => update({ password: event.target.value })}
                onKeyDown={(event) => { if (event.key === 'Enter') { event.preventDefault(); fetch(); } }} />
              {error('password')}
            </div>
          </div>
          <div className="actions">
            <button type="button" className="primary" disabled={fetching} aria-busy={fetching} onClick={fetch}>{fetching ? t('Getting profile…') : t('Sign in and get profile')}</button>
          </div>
        </fieldset>}
        {error('form')}
      </>}
    </section> : <section className="card setup-step" aria-labelledby={`${prefix}-read`}>
      <h2 id={`${prefix}-read`}>{t('Review profile')}</h2>
      <Facts facts={editor.facts!} />
      <div className="editor-fields">
        <div className="field">
          <label htmlFor={`${prefix}-name`}>{t('Profile name')}</label>
          <input id={`${prefix}-name`} type="text" autoComplete="off" value={values.name} aria-invalid={!!editor.errors.name}
            onChange={(event) => update({ name: event.target.value })} />
          {error('name')}
        </div>
      </div>
      {cannot ? <p className="found cannot"><Icon name="warning" /><span>{editor.hint}</span></p> : <>
        {values.needsLogin && <fieldset className="inset sign-in">
          <legend>{t('VPN sign-in')}</legend>
          <div className="editor-fields">
            <div className="field">
              <label htmlFor={`${prefix}-user`}>{t('Username')}</label>
              <input id={`${prefix}-user`} type="text" autoComplete="username" spellCheck={false} value={values.username}
                aria-invalid={!!editor.errors.username} onChange={(event) => update({ username: event.target.value })} />
              {error('username')}
            </div>
            {askPassword && <div className="field">
              <label htmlFor={`${prefix}-password`}>{t('Password')}</label>
              <input id={`${prefix}-password`} type="password" autoComplete="current-password" value={values.password}
                aria-invalid={!!editor.errors.password} onChange={(event) => update({ password: event.target.value })} />
              {error('password')}
            </div>}
          </div>
          <label className="check-field">
            <input type="checkbox" checked={values.remember} onChange={(event) => update({ remember: event.target.checked })} />
            {t('Remember password')}
          </label>
          {!askPassword && <p className="secondary metadata">{t('You can sign in when you connect.')}</p>}
        </fieldset>}
        <label className="check-field">
          <input type="checkbox" checked={values.connect} onChange={(event) => update({ connect: event.target.checked })} />
          {t('Connect after import')}
        </label>
      </>}
    </section>}

    {read && error('form')}
    <div className="actions setup-actions">
      {read && <ActionButton action={values.connect && !cannot ? { ...editor.save, label: t('Import and connect') } : editor.save} primary type="submit" />}
      <ActionButton action={editor.cancel} onClick={() => post('CANCEL_EDIT', { token: editor.token })} />
      {/* Back to the file or link, as it was before anything was read. */}
      {read && <button type="button" className="link change-source" onClick={() => post('IMPORT', {})}>{t('Change source…')}</button>}
    </div>
  </form>;
}

/** "Edit {name}" (D29): its name, and for one that signs in, its username and whether its password is kept — never the
 *  password itself; Change password… takes a new one, Forget password lets the kept one go. */
export function ProfileEditForm({ editor, draft, onChange }: { editor: Editor; draft: Draft; onChange: (draft: Draft) => void }) {
  const form = useRef<HTMLFormElement>(null);
  const prefix = useId();
  useEditorFocus(form);
  if (draft.kind !== 'profile-edit' || editor.kind !== 'profile-edit') return null;
  const values = draft.values;
  const update = (next: Partial<ProfileEditValues>) => onChange({ ...draft, values: { ...values, ...next } });
  const error = (field: EditorField) => editor.errors[field]
    ? <p className="field-error" role="alert" id={`${prefix}-${field}-error`}>{editor.errors[field]}</p> : null;
  const usernameChanged = values.username.trim() !== editor.values.username;

  return <form ref={form} className="new-app" tabIndex={-1} aria-label={t('Edit profile')} noValidate onSubmit={(event) => {
    event.preventDefault();
    post('SAVE_PROFILE_EDIT', { token: editor.token, values });
  }}>
    <section className="card setup-step">
      {editor.facts && <Facts facts={editor.facts} />}
      <div className="editor-fields">
        <div className="field">
          <label htmlFor={`${prefix}-name`}>{t('Profile name')}</label>
          <input id={`${prefix}-name`} type="text" autoComplete="off" value={values.name} aria-invalid={!!editor.errors.name}
            onChange={(event) => update({ name: event.target.value })} />
          {error('name')}
        </div>
      </div>
      {values.needsLogin && <fieldset className="inset sign-in">
        <legend>{t('VPN sign-in')}</legend>
        <div className="editor-fields">
          <div className="field">
            <label htmlFor={`${prefix}-user`}>{t('Username')}</label>
            <input id={`${prefix}-user`} type="text" autoComplete="username" spellCheck={false} value={values.username}
              aria-invalid={!!editor.errors.username} onChange={(event) => update({ username: event.target.value })} />
            {error('username')}
          </div>
          <div className="field">
            <span className="secondary metadata">{t('Password')}</span>
            <span>{values.forgetPassword ? t('Will be forgotten') : values.passwordSaved ? t('Saved') : t('Not saved')}</span>
          </div>
        </div>
        {values.changePassword ? <div className="field">
          <label htmlFor={`${prefix}-password`}>{t('New password')}</label>
          <input id={`${prefix}-password`} type="password" autoComplete="new-password" value={values.password}
            aria-invalid={!!editor.errors.password} onChange={(event) => update({ password: event.target.value })} />
          {error('password')}
        </div> : <div className="actions">
          <button type="button" onClick={() => update({ changePassword: true, forgetPassword: false })}>{t('Change password…')}</button>
          {values.passwordSaved && !values.forgetPassword && <button type="button" onClick={() => update({ forgetPassword: true })}>{t('Forget password')}</button>}
        </div>}
        {usernameChanged && values.passwordSaved && !values.changePassword && <p className="secondary metadata">{t('Changing the username also forgets its saved password.')}</p>}
      </fieldset>}
      {/* How it connects: as Settings says, which most never change, or its own, over Settings' choices. */}
      {values.policy && <fieldset className="inset connection-choice">
        <legend>{t('How it connects')}</legend>
        <label className="radio-field">
          <input type="radio" name={`${prefix}-own`} checked={!values.ownSettings} onChange={() => update({ ownSettings: false })} />
          <span><span>{t('As in Settings')}</span>{editor.hint && <span className="secondary metadata">{editor.hint}</span>}</span>
        </label>
        <label className="radio-field">
          <input type="radio" name={`${prefix}-own`} checked={values.ownSettings} onChange={() => update({ ownSettings: true })} />
          <span><span>{t('Its own settings')}</span><span className="secondary metadata">{t('Only for this profile. What is left as it is follows Settings.')}</span></span>
        </label>
        {values.ownSettings && <PolicyFields editor={editor} values={values.policy} prefix={`${prefix}-policy`}
          onChange={(policy) => update({ policy })} />}
      </fieldset>}
    </section>
    {error('form')}
    <div className="actions setup-actions">
      <ActionButton action={editor.save} primary type="submit" />
      <ActionButton action={editor.cancel} onClick={() => post('CANCEL_EDIT', { token: editor.token })} />
    </div>
  </form>;
}

/** The sign-in a connect waits for (D29), as the confirmation dialog looks: its server, the username kept, the password,
 *  Remember password. A saved password is never put back in the field. */
export function SignInDialog({ signIn }: { signIn: SignInView }) {
  const dialog = useRef<HTMLDialogElement>(null);
  const first = useRef<HTMLInputElement>(null);
  const prefix = useId();
  const [username, setUsername] = useState(signIn.username);
  const [password, setPassword] = useState('');
  const [remember, setRemember] = useState(signIn.passwordSaved);
  useLayoutEffect(() => {
    const trigger = getFocusOrigin() ?? (document.activeElement instanceof HTMLElement ? document.activeElement : null);
    const element = dialog.current;
    element?.showModal();
    paintCaptionButtons(true);
    first.current?.focus();
    return () => {
      element?.close();
      paintCaptionButtons(false);
      queueMicrotask(() => {
        if (!element?.isConnected) (getFocusOrigin() ?? (trigger?.isConnected ? trigger : null))?.focus();
      });
    };
  }, []);

  return <dialog ref={dialog} className="confirmation sign-in-dialog" tabIndex={-1} aria-labelledby={`${prefix}-title`} onCancel={(event) => {
    event.preventDefault();
    post('CANCEL_SIGN_IN', { token: signIn.token });
  }}>
    <form noValidate onSubmit={(event) => {
      event.preventDefault();
      post('SIGN_IN', { token: signIn.token, values: { username, password, remember } });
      setPassword('');
    }}>
      <h2 id={`${prefix}-title`}>{signIn.title}</h2>
      {signIn.server && <Facts facts={[{ label: t('Server'), value: signIn.server, figure: null }]} />}
      {signIn.error && <p className="field-error" role="alert">{signIn.error}</p>}
      <div className="field">
        <label htmlFor={`${prefix}-user`}>{t('Username')}</label>
        <input ref={signIn.username ? undefined : first} id={`${prefix}-user`} type="text" autoComplete="username" spellCheck={false}
          value={username} onChange={(event) => setUsername(event.target.value)} />
      </div>
      <div className="field">
        <label htmlFor={`${prefix}-password`}>{t('Password')}</label>
        <input ref={signIn.username ? first : undefined} id={`${prefix}-password`} type="password" autoComplete="current-password"
          value={password} onChange={(event) => setPassword(event.target.value)} />
      </div>
      <label className="check-field">
        <input type="checkbox" checked={remember} onChange={(event) => setRemember(event.target.checked)} />
        {t('Remember password')}
      </label>
      <div className="actions">
        <ActionButton action={signIn.cancel} onClick={() => post('CANCEL_SIGN_IN', { token: signIn.token })} />
        <ActionButton action={signIn.connect} primary type="submit" />
      </div>
    </form>
  </dialog>;
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
