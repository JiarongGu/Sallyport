import { t } from '../i18n';
import { isShenoraAvailable, WindowCommands } from '@shenora/react';
import { useEffect, useId, useRef, type MouseEvent, type ReactNode } from 'react';
import type { ClientState, Page, ProfileView } from '../contract';
import { Icon, type IconName } from './shared';

const commands = new WindowCommands();
const run = (work: Promise<void>) => { void work.catch(() => console.error('Sallyport window command could not be sent.')); };

/** The window moves from any part of its frame that is not a control: the sidebar's empty space and the header. */
function dragFrom(event: MouseEvent) {
  if (event.button !== 0 || !isShenoraAvailable()) return;
  if ((event.target as HTMLElement).closest('button, select, input, a, [role="button"]')) return;
  run(commands.startDrag());
}

function systemMenu(event: MouseEvent) {
  if ((event.target as HTMLElement).closest('button, select, input, a')) return;
  event.preventDefault();
  if (isShenoraAvailable()) run(commands.showSystemMenu());
}

export const pages: readonly (readonly [Page, string, IconName])[] = [
  ['network', 'Overview', 'overview'], ['apps', 'Apps', 'apps'], ['profiles', 'Profiles', 'key'], ['settings', 'Settings', 'gear'],
];

/** The profile shown first on the Overview: the one chosen, else the first with a connection open (trouble first, as
 *  the host orders them), else the first. */
export function featuredProfile(state: ClientState, featured: string | null): ProfileView | undefined {
  return state.profiles.find((p) => p.id === featured)
    ?? state.profiles.find((p) => state.network.channels.some((c) => c.profileId === p.id))
    ?? state.profiles[0];
}

/**
 * The menu as a rail, always narrow: the mark; the pages, each an icon with its word under it; Settings at the foot;
 * and under the buttons whether anything is connected, in its tone. The Overview holds the switch itself.
 */
export function Sidebar({ state, shown, go }: { state?: ClientState; shown: Page; go: (page: Page) => void }) {
  const item = ([value, label, icon]: readonly [Page, string, IconName]) =>
    <button key={value} type="button" className={`nav-item${shown === value ? ' selected' : ''}`}
      aria-current={shown === value ? 'page' : undefined} title={t(label)} onClick={() => go(value)}>
      <Icon name={icon} /><span className="nav-label">{t(label)}</span>
    </button>;
  return <aside className="sidebar" onMouseDown={dragFrom} onContextMenu={systemMenu}>
    <div className="sidebar-head" title="Sallyport"><GateMark /></div>
    <nav className="navigation" aria-label={t('Main')}>
      {pages.filter(([value]) => value !== 'settings').map(item)}
      <span className="nav-spacer" />
      {state && <div className={`rail-status tone-${state.summary.tone}`} role="status" title={state.summary.text}>
        <span className="rail-dot" aria-hidden="true" /><span className="rail-text">{state.summary.text}</span>
      </div>}
      {item(pages.find(([value]) => value === 'settings')!)}
    </nav>
  </aside>;
}

/** The page's own header, which is also the window's title bar: its title, its actions, minimize and close — no
 *  maximize: the window keeps the size it is given. The form paints the two buttons where these report they are. */
export function MainHeader({ title, children }: { title: string; children?: ReactNode }) {
  const buttons = useRef<HTMLDivElement>(null);
  const hosted = isShenoraAvailable();

  useEffect(() => {
    if (!hosted) return;
    const report = () => {
      const kinds = ['minimize', 'close'] as const;
      const rects = Array.from(buttons.current?.children ?? []).map((element, index) => {
        const rect = element.getBoundingClientRect();
        return { kind: kinds[index], x: rect.left, y: rect.top, width: rect.width, height: rect.height };
      });
      void commands.setCaptionButtons(rects).catch(() => console.error('Sallyport caption regions could not be reported.'));
    };
    // Moving between monitors can change DPI without changing the CSS viewport size.
    let dpi: MediaQueryList;
    const watchDpi = () => {
      dpi?.removeEventListener('change', watchDpi);
      dpi = window.matchMedia(`(resolution: ${window.devicePixelRatio}dppx)`);
      dpi.addEventListener('change', watchDpi);
      report();
    };
    watchDpi();
    const observer = new ResizeObserver(report);
    if (buttons.current) observer.observe(buttons.current);
    window.addEventListener('resize', report);
    return () => {
      observer.disconnect();
      window.removeEventListener('resize', report);
      dpi.removeEventListener('change', watchDpi);
    };
  }, [hosted]);

  return <header className="main-head" onMouseDown={dragFrom} onContextMenu={systemMenu}>
    {hosted && <div className="top-resize" onMouseDown={(event) => {
      event.stopPropagation();
      if (event.button === 0) run(commands.startResize('top'));
    }} />}
    <h1>{title}</h1>
    <div className="head-actions">{children}</div>
    <div className="caption-buttons" ref={buttons} onMouseDown={(event) => event.stopPropagation()}>
      <button type="button" aria-label={t('Minimize')} onClick={() => run(commands.minimize())}><svg viewBox="0 0 12 12" aria-hidden="true"><path d="M1 6.5H11" /></svg></button>
      <button type="button" className="caption-close" aria-label={t('Close')} onClick={() => run(commands.close())}><svg viewBox="0 0 12 12" aria-hidden="true"><path d="M1.5 1.5L10.5 10.5M10.5 1.5L1.5 10.5" /></svg></button>
    </div>
  </header>;
}

/** The caption buttons in the page's colours; `dimmed` as a dialog's backdrop (black at 30%) dims everything else — the
 *  form paints them outside the page, so the backdrop cannot reach them. */
export function paintCaptionButtons(dimmed: boolean) {
  if (!isShenoraAvailable()) return;
  const style = getComputedStyle(document.documentElement);
  const token = (name: string) => style.getPropertyValue(name).trim();
  const dim = (hex: string) => !dimmed || !/^#[0-9a-f]{6}$/i.test(hex) ? hex
    : '#' + [1, 3, 5].map((i) => Math.round(parseInt(hex.slice(i, i + 2), 16) * 0.7).toString(16).padStart(2, '0')).join('');
  run(commands.setCaptionButtonColors({
    surface: dim(token('--background')), hover: dim(token('--caption-hover')), pressed: dim(token('--caption-pressed')),
    glyph: dim(token('--text')), closeHover: '#c42b1c', closePressed: '#a32417', closeGlyphHot: '#ffffff',
  }));
}

/** Sallyport's mark: a gateway with a way out rising through it, white on the brand's tile. The window's icon, the
 *  tray's and the splash draw the same 32-unit geometry (BrandMark.cs). */
export function GateMark() {
  const gradient = useId();
  return <svg width="24" height="24" viewBox="0 0 32 32" aria-hidden="true">
    <defs><linearGradient id={gradient} x1="1" y1="1" x2="31" y2="31" gradientUnits="userSpaceOnUse">
      <stop offset="0" stopColor="#3d8bff" /><stop offset="1" stopColor="#5b4be0" />
    </linearGradient></defs>
    <rect x="1" y="1" width="30" height="30" rx="8" fill={`url(#${gradient})`} />
    <path d="M8.5 24V15.5a7.5 7.5 0 0 1 15 0V24M16 24.5v-9M13 18.5L16 15.5l3 3" fill="none" stroke="#fff"
      strokeWidth="2.75" strokeLinecap="round" strokeLinejoin="round" />
  </svg>;
}
