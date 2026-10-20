import { getBridge, isShenoraAvailable, useShenoraEvent, WindowCommands } from '@shenora/react';
import { useCallback, useEffect, useRef, useState } from 'react';
import type { ClientState, Notice, Page } from './contract';
import { setLanguage, t } from './i18n';
import { MODULE } from './contract';
import { AppPage, Apps } from './Apps';
import { Network } from './Network';
import { ProfileSettings, Profiles } from './Profiles';
import { Settings } from './Settings';
import { EditorForm, RemovalConfirmation, seedDraft, type Draft } from './ui/Editors';
import { Notices } from './ui/Notices';
import { MainHeader, pages, Sidebar } from './ui/Chrome';
import { ActionButton } from './ui/shared';
import { post } from './commands';

export function App() {
  const [state, setState] = useState<ClientState>();
  // Null until the first state says where a first look opens; the person's own choice after that.
  const [page, setPage] = useState<Page | null>(null);
  // The app whose own page Apps shows, if any.
  const [appPage, setAppPage] = useState<string | null>(null);
  const [expanded, setExpanded] = useState<ReadonlySet<string>>(() => new Set());
  const [earlierOpen, setEarlierOpen] = useState(false);
  const [draft, setDraft] = useState<Draft | null>(null);
  const [now, setNow] = useState(Date.now);
  // Visual state only: which profile the Overview puts first.
  const [featured, setFeatured] = useState<string | null>(() => { try { return localStorage.getItem('featured-profile'); } catch { return null; } });
  const feature = (id: string) => { setFeatured(id); try { localStorage.setItem('featured-profile', id); } catch { /* kept for this session only */ } };
  const polite = useRef<HTMLDivElement>(null);
  const alert = useRef<HTMLDivElement>(null);
  const editorToken = useRef<string | null>(null);
  const navigationRequest = useRef<number | null>(null);
  const take = useCallback((next: ClientState) => {
    setState((current) => !current || next.sequence > current.sequence ? next : current);
  }, []);
  const announce = useCallback((notice: Notice) => {
    const region = notice.kind === 'error' ? alert.current : polite.current;
    const message = document.createElement('span');
    message.textContent = notice.text;
    region?.replaceChildren(message);
  }, []);

  // Both subscriptions must precede the handshake: it releases the host's initial whole state.
  useShenoraEvent<ClientState>(MODULE, 'STATE', take);
  useShenoraEvent<Notice>(MODULE, 'NOTICE', announce);
  useEffect(() => {
    getBridge().notifyReady().catch(() => console.error('Sallyport ready handshake failed.'));
  }, []);

  // The host's splash stays until the first state has been painted: lifted sooner, it would give way to a bare page.
  const splashLifted = useRef(false);
  useEffect(() => {
    if (!state || splashLifted.current || !isShenoraAvailable()) return;
    splashLifted.current = true;
    requestAnimationFrame(() => requestAnimationFrame(() => {
      void new WindowCommands().closeSplash().catch(() => console.error('Sallyport splash could not be lifted.'));
    }));
  }, [state]);

  useEffect(() => {
    const timer = window.setInterval(() => setNow(Date.now()), 1000);
    return () => window.clearInterval(timer);
  }, []);

  // The host's request to show a page (the tray's Open Network) is followed once; a request already seen when the
  // window loaded is not, so a reload does not move the person.
  useEffect(() => {
    if (!state) return;
    const request = state.navigation?.request ?? 0;
    if (navigationRequest.current === null) {
      navigationRequest.current = request;
      setPage((current) => current ?? state.navigation?.page ?? state.initialPage);
    } else if (request !== navigationRequest.current && state.navigation) {
      navigationRequest.current = request;
      setPage(state.navigation.page);
    }
  }, [state]);

  useEffect(() => {
    const editor = state?.editor;
    if (editor && editor.token !== editorToken.current) {
      setPage(editor.kind === 'policy' ? 'profiles' : editor.kind === 'defaults' ? 'settings' : 'apps');
      // An app's editors open on its page.
      if (editor.kind === 'app' || editor.kind === 'program') setAppPage(editor.subjectId.split('/')[0]);
    }
    editorToken.current = editor?.token ?? null;
  }, [state?.editor?.token]);

  // Reconcile before rendering fields so a new token cannot submit the previous draft for a frame.
  let currentDraft = draft;
  if (state?.editor && draft?.token !== state.editor.token) {
    currentDraft = seedDraft(state.editor);
    setDraft(currentDraft);
  } else if (!state?.editor && draft) {
    currentDraft = null;
    setDraft(null);
  }

  const toggle = (key: string) => setExpanded((current) => {
    const next = new Set(current);
    if (next.has(key)) next.delete(key); else next.add(key);
    return next;
  });
  const editorForm = state?.editor && currentDraft ? <EditorForm key={state.editor.token}
    editor={state.editor} draft={currentDraft} onChange={setDraft}
    profileChoices={state.profileChoices} kindChoices={state.kindChoices} lossChoices={state.lossChoices} /> : null;
  const shown = page ?? state?.initialPage ?? 'apps';
  // A profile's own connection settings take the Profiles page while they are open; back is Cancel.
  const policyEditor = shown === 'profiles' && state?.editor?.kind === 'policy' ? state.editor : null;
  const policyName = policyEditor ? state?.profiles.find((profile) => profile.id === policyEditor.subjectId)?.name ?? '' : '';
  const openApp = shown === 'apps' && appPage ? state?.apps.find((app) => app.id === appPage) ?? null : null;
  // Leaving a view inside a page leaves what was being edited there.
  const leaveViews = () => {
    const editor = state?.editor;
    if (editor && (editor.kind === 'policy' || ((editor.kind === 'app' || editor.kind === 'program') && appPage))) post('CANCEL_EDIT', { token: editor.token });
    setAppPage(null);
  };
  const go = (next: Page) => { leaveViews(); setPage(next); };
  // Before anything renders: the page's own words follow the host's language from the same state.
  setLanguage(state?.language ?? 'en');

  return <div className="shell">
    <Sidebar state={state} shown={shown} go={go} />
    <div className="main">
      <MainHeader title={policyEditor ? t('Connection settings for {0}', policyName) : openApp ? openApp.name : t(pages.find(([value]) => value === shown)?.[1] ?? 'Apps')}
        back={policyEditor ? { label: t('Back to Profiles'), onClick: () => post('CANCEL_EDIT', { token: policyEditor.token }) }
          : openApp ? { label: t('Back to Apps'), onClick: leaveViews } : undefined}>
        {state && shown === 'apps' && !openApp && <>
          <ActionButton action={state.actions.addApp} glyph="plus" focusKey="add-app" onClick={() => post('ADD_APP', {})} />
        </>}
        {state && shown === 'profiles' && !policyEditor && <ActionButton action={state.actions.import} glyph="plus" onClick={() => post('IMPORT', {})} />}
      </MainHeader>
      <Notices notices={state?.notices ?? []} earlierOpen={earlierOpen} toggleEarlier={() => setEarlierOpen((open) => !open)} />
      <main className="content" id="main-content">
        {!state ? <div className="loading" role="status"><span className="spinner" aria-hidden="true" />{t('Loading…')}</div> :
          openApp ? <AppPage app={openApp} state={state} now={now}
            editorForm={state.editor?.kind === 'program' || state.editor?.kind === 'app' ? editorForm : null} /> :
          shown === 'apps' ? <Apps state={state} now={now} open={setAppPage}
            editorForm={state.editor?.kind === 'program' || state.editor?.kind === 'app' ? editorForm : null} /> :
          shown === 'network' ? <Network state={state} now={now} go={setPage} featured={featured} feature={feature} /> :
          policyEditor ? <ProfileSettings name={policyName} editorForm={editorForm} /> :
          shown === 'profiles' ? <Profiles state={state} now={now} expanded={expanded} toggle={toggle} /> :
          <Settings state={state} expanded={expanded} toggle={toggle} editorForm={state.editor?.kind === 'defaults' ? editorForm : null} />}
      </main>
    </div>
    <div className="sr-only" ref={polite} role="status" aria-live="polite" aria-atomic="true" />
    <div className="sr-only" ref={alert} role="alert" aria-atomic="true" />
    {state?.confirmation && <RemovalConfirmation key={state.confirmation.token} confirmation={state.confirmation} />}
  </div>;
}
