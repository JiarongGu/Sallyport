import { getBridge, isShenoraAvailable, useShenoraEvent, WindowCommands } from '@shenora/react';
import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react';
import type { ClientState, Notice, Page } from './contract';
import { setLanguage, t } from './i18n';
import { MODULE } from './contract';
import { AppPage, Apps } from './Apps';
import { Network } from './Network';
import { ProfileEdit, Profiles } from './Profiles';
import { Settings } from './Settings';
import { EditorForm, NewAppForm, NewProfileForm, ProfileEditForm, RemovalConfirmation, seedDraft, SignInDialog, type Draft } from './ui/Editors';
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
  // A file being dragged over the window: one target for all of it, whatever page is shown (D29).
  const [dropping, setDropping] = useState(false);
  // Visual state only: which profile the Overview puts first.
  const polite = useRef<HTMLDivElement>(null);
  const alert = useRef<HTMLDivElement>(null);
  const editorToken = useRef<string | null>(null);
  const navigationRequest = useRef<number | null>(null);
  // A profile dropped anywhere on the window goes to the host as the file itself — WebView2 hands it the path — and never
  // as its text; the host decides what it is. Without this the window would open the file as a page.
  useEffect(() => {
    let depth = 0;
    const files = (event: DragEvent) => event.dataTransfer?.types.includes('Files') ?? false;
    const enter = (event: DragEvent) => { if (files(event)) { depth++; setDropping(true); } };
    const over = (event: DragEvent) => { if (files(event)) { event.preventDefault(); event.dataTransfer!.dropEffect = 'copy'; } };
    const leave = (event: DragEvent) => { if (files(event) && --depth <= 0) { depth = 0; setDropping(false); } };
    const drop = (event: DragEvent) => {
      if (!files(event)) return;
      event.preventDefault();
      depth = 0;
      setDropping(false);
      const dropped = [...(event.dataTransfer?.files ?? [])];
      const webview = (window as unknown as { chrome?: { webview?: { postMessageWithAdditionalObjects?: (message: unknown, objects: File[]) => void } } }).chrome?.webview;
      if (dropped.length > 0) webview?.postMessageWithAdditionalObjects?.({ kind: 'dropped-files' }, dropped);
    };
    window.addEventListener('dragenter', enter);
    window.addEventListener('dragover', over);
    window.addEventListener('dragleave', leave);
    window.addEventListener('drop', drop);
    return () => {
      window.removeEventListener('dragenter', enter);
      window.removeEventListener('dragover', over);
      window.removeEventListener('dragleave', leave);
      window.removeEventListener('drop', drop);
    };
  }, []);
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

  // The root's size follows Windows' Text size (the host's textScale), and every size is in rem of it. The two narrower
  // arrangements follow the window's width in rem as well, so larger text arranges the page as a narrower window would:
  // a media query's width is in CSS pixels, which text size does not change.
  const textScale = state?.textScale ?? 1;
  useLayoutEffect(() => {
    const root = document.documentElement;
    root.style.setProperty('--text-scale', String(textScale));
    const arrange = () => {
      const rems = window.innerWidth / (16 * textScale);
      root.classList.toggle('narrow', rems <= 53.75);
      root.classList.toggle('compact', rems <= 37.5);
    };
    arrange();
    window.addEventListener('resize', arrange);
    return () => window.removeEventListener('resize', arrange);
  }, [textScale]);

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
      // A new app opens on its own page: what to turn on, and what it is doing, are there.
      setAppPage(state.navigation.app);
    }
  }, [state]);

  useEffect(() => {
    const editor = state?.editor;
    if (editor && editor.token !== editorToken.current) {
      setPage(editor.kind === 'new-profile' || editor.kind === 'profile-edit' ? 'profiles'
        : editor.kind === 'export' || editor.kind === 'import' ? 'settings' : 'apps');
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
    kindChoices={state.kindChoices} lossChoices={state.lossChoices} /> : null;
  const shown = page ?? state?.initialPage ?? 'apps';
  const newApp = shown === 'apps' && state?.editor?.kind === 'new-app' ? state.editor : null;
  const newProfile = shown === 'profiles' && state?.editor?.kind === 'new-profile' ? state.editor : null;
  const profileEdit = shown === 'profiles' && state?.editor?.kind === 'profile-edit' ? state.editor : null;
  const editedProfile = profileEdit ? state?.profiles.find((profile) => profile.id === profileEdit.subjectId) ?? null : null;
  const openApp = shown === 'apps' && !newApp && appPage ? state?.apps.find((app) => app.id === appPage) ?? null : null;
  // Leaving a view inside a page leaves what was being edited there.
  const leaveViews = () => {
    const editor = state?.editor;
    if (editor && (editor.kind === 'new-app' || editor.kind === 'new-profile' || editor.kind === 'profile-edit'
      || ((editor.kind === 'app' || editor.kind === 'program') && appPage)))
      post('CANCEL_EDIT', { token: editor.token });
    setAppPage(null);
  };
  const go = (next: Page) => { leaveViews(); setPage(next); };
  // The host looks at the open app's programs while its page is shown, and stops when it is not.
  const watched = useRef<string | null>(null);
  const watching = openApp?.id ?? null;
  useEffect(() => {
    if (!state || watched.current === watching) return;
    watched.current = watching;
    post('WATCH_APP', { id: watching });
  }, [watching, state]);
  // Before anything renders: the page's own words follow the host's language from the same state.
  setLanguage(state?.language ?? 'en');

  return <div className="shell">
    <Sidebar state={state} shown={shown} go={go} />
    <div className="main">
      <MainHeader title={newApp ? t('Add an app') : newProfile ? t('Import a VPN profile')
          : editedProfile ? t('Edit {0}', editedProfile.name) : openApp ? openApp.name
          : t(pages.find(([value]) => value === shown)?.[1] ?? 'Apps')}
        back={newApp ? { label: t('Back to Apps'), onClick: () => post('CANCEL_EDIT', { token: newApp.token }) }
          : newProfile ? { label: t('Back to Profiles'), onClick: () => post('CANCEL_EDIT', { token: newProfile.token }) }
          : profileEdit ? { label: t('Back to Profiles'), onClick: () => post('CANCEL_EDIT', { token: profileEdit.token }) }
          : openApp ? { label: t('Back to Apps'), onClick: leaveViews } : undefined}>
        {state && shown === 'apps' && !openApp && !newApp && <>
          <ActionButton action={state.actions.addApp} glyph="plus" focusKey="add-app" onClick={() => post('ADD_APP', {})} />
        </>}
        {state && shown === 'profiles' && !newProfile && !profileEdit && <ActionButton action={state.actions.import} glyph="plus" onClick={() => post('IMPORT', {})} />}
      </MainHeader>
      <Notices notices={state?.notices ?? []} earlierOpen={earlierOpen} toggleEarlier={() => setEarlierOpen((open) => !open)} />
      <main className="content" id="main-content">
        {!state ? <div className="loading" role="status"><span className="spinner" aria-hidden="true" />{t('Loading…')}</div> :
          newApp && currentDraft ? <section className="page" aria-label={t('Add an app')}>
            <p className="page-intro secondary">{t('Pick a game or a program. When its VPN is on, only it goes through your VPN; everything else keeps your own connection.')}</p>
            <NewAppForm key={newApp.token} editor={newApp} draft={currentDraft} onChange={setDraft}
              profileChoices={state.profileChoices} lossChoices={state.lossChoices} />
          </section> :
          openApp ? <AppPage app={openApp} state={state} now={now} expanded={expanded} toggle={toggle}
            editorForm={state.editor?.kind === 'program' || state.editor?.kind === 'app' ? editorForm : null} /> :
          shown === 'apps' ? <Apps state={state} now={now} open={setAppPage}
            editorForm={state.editor?.kind === 'program' || state.editor?.kind === 'app' ? editorForm : null} /> :
          shown === 'network' ? <Network state={state} now={now} go={setPage} /> :
          newProfile && currentDraft ? <section className="page" aria-label={t('Import a VPN profile')}>
            <p className="page-intro secondary">{t('Use the file or link you were given. Sallyport keeps an encrypted copy for your Windows account.')}</p>
            <NewProfileForm key={newProfile.token} editor={newProfile} draft={currentDraft} onChange={setDraft} />
          </section> :
          editedProfile && profileEdit && currentDraft ? <ProfileEdit profile={editedProfile}
            form={<ProfileEditForm key={profileEdit.token} editor={profileEdit} draft={currentDraft} onChange={setDraft} />} /> :
          shown === 'profiles' ? <Profiles state={state} now={now} /> :
          <Settings state={state} expanded={expanded} toggle={toggle}
            moveForm={state.editor?.kind === 'export' || state.editor?.kind === 'import' ? editorForm : null} />}
      </main>
    </div>
    <div className="sr-only" ref={polite} role="status" aria-live="polite" aria-atomic="true" />
    <div className="sr-only" ref={alert} role="alert" aria-atomic="true" />
    {state?.confirmation && <RemovalConfirmation key={state.confirmation.token} confirmation={state.confirmation} />}
    {state?.signIn && <SignInDialog key={state.signIn.token} signIn={state.signIn} />}
    {dropping && <div className="drop-overlay" aria-hidden="true"><p>{t('Drop to import a VPN profile')}</p></div>}
  </div>;
}
