import { getBridge, useShenoraEvent } from '@shenora/react';
import { useCallback, useEffect, useRef, useState } from 'react';
import type { ClientState, Notice } from './contract';
import { MODULE } from './contract';
import { Apps } from './Apps';
import { Games } from './Games';
import { Guide } from './Guide';
import { Profiles } from './Profiles';
import { EditorForm, RemovalConfirmation, seedDraft, type Draft } from './ui/Editors';
import { Notices } from './ui/Notices';
import { TitleBar } from './ui/TitleBar';

type Page = 'profiles' | 'games' | 'apps' | 'guide';

function remembered(): Page {
  // Storage can be refused by WebView policy; navigation must still work.
  try {
    const page = localStorage.getItem('sallyport.page');
    if (page === 'profiles' || page === 'games' || page === 'apps' || page === 'guide') return page;
  } catch { /* The first page is only a convenience. */ }
  return 'profiles';
}

export function App() {
  const [state, setState] = useState<ClientState>();
  const [page, setPage] = useState<Page>(remembered);
  const [expanded, setExpanded] = useState<ReadonlySet<string>>(() => new Set());
  const [earlierOpen, setEarlierOpen] = useState(false);
  const [draft, setDraft] = useState<Draft | null>(null);
  const [now, setNow] = useState(Date.now);
  const polite = useRef<HTMLDivElement>(null);
  const alert = useRef<HTMLDivElement>(null);
  const editorToken = useRef<string | null>(null);
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

  useEffect(() => {
    const timer = window.setInterval(() => setNow(Date.now()), 1000);
    return () => window.clearInterval(timer);
  }, []);

  const go = useCallback((next: Page) => {
    setPage(next);
    try { localStorage.setItem('sallyport.page', next); }
    catch { /* Storage policy must not block navigation. */ }
  }, []);

  useEffect(() => {
    const editor = state?.editor;
    if (editor && editor.token !== editorToken.current)
      go(editor.kind === 'port' || editor.kind === 'policy' ? 'profiles' : editor.kind === 'game' ? 'games' : 'apps');
    editorToken.current = editor?.token ?? null;
  }, [state?.editor?.token, go]);

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

  return <div className="shell">
    <TitleBar summary={state?.summary} />
    <nav className="navigation" aria-label="Main">
      {([['profiles', 'Profiles'], ['games', 'Games'], ['apps', 'Apps'], ['guide', 'How to use']] as const).map(([value, label]) =>
        <button key={value} type="button" className={`nav-item${page === value ? ' selected' : ''}`}
          aria-current={page === value ? 'page' : undefined} onClick={() => go(value)}>{label}</button>)}
    </nav>
    <Notices notices={state?.notices ?? []} earlierOpen={earlierOpen} toggleEarlier={() => setEarlierOpen((open) => !open)} />
    <main className="content" id="main-content">
      {!state ? <div className="loading secondary" role="status">Loading…</div> :
        page === 'profiles' ? <Profiles state={state} now={now} expanded={expanded} toggle={toggle}
          editorForm={state.editor?.kind === 'port' || state.editor?.kind === 'policy' ? editorForm : null} /> :
        page === 'games' ? <Games state={state} now={now} expanded={expanded} toggle={toggle} editorForm={state.editor?.kind === 'game' ? editorForm : null} /> :
        page === 'apps' ? <Apps state={state} expanded={expanded} toggle={toggle} editorForm={state.editor?.kind === 'app' ? editorForm : null} /> :
        <Guide guide={state.guide} manualOpen={expanded.has('manual')} toggleManual={() => toggle('manual')} />}
    </main>
    <div className="sr-only" ref={polite} role="status" aria-live="polite" aria-atomic="true" />
    <div className="sr-only" ref={alert} role="alert" aria-atomic="true" />
    {state?.confirmation && <RemovalConfirmation key={state.confirmation.token} confirmation={state.confirmation} />}
  </div>;
}
