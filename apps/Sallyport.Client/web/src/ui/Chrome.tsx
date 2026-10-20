import { t } from '../i18n';
import { isShenoraAvailable, WindowCommands } from '@shenora/react';
import { useEffect, useMemo, useRef, type MouseEvent, type ReactNode } from 'react';
import type { ClientState, Page } from '../contract';
import { Icon, StatusPill, type IconName } from './shared';

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
  ['network', 'Network', 'pulse'], ['apps', 'Apps', 'apps'], ['profiles', 'Profiles', 'key'], ['settings', 'Settings', 'gear'],
];

/** Connect's menu, kept open in a wide window: the brand, the pages, and what is connected now. ☰ narrows it to icons. */
export function Sidebar({ shown, go, summary, collapsed, toggle }: {
  shown: Page; go: (page: Page) => void; summary?: ClientState['summary']; collapsed: boolean; toggle: () => void;
}) {
  return <aside className={`sidebar${collapsed ? ' collapsed' : ''}`} onMouseDown={dragFrom} onContextMenu={systemMenu}>
    <div className="sidebar-head">
      <button type="button" className="subtle icon" aria-label={t('Menu')} aria-expanded={!collapsed} title={t('Menu')} onClick={toggle}>
        <Icon name="menu" />
      </button>
      <div className="brand"><GateMark /><span>Sallyport</span></div>
    </div>
    <nav className="navigation" aria-label={t('Main')}>
      {pages.map(([value, label, icon]) =>
        <button key={value} type="button" className={`nav-item${shown === value ? ' selected' : ''}`}
          aria-current={shown === value ? 'page' : undefined} aria-label={t(label)} title={t(label)} onClick={() => go(value)}>
          <Icon name={icon} /><span>{t(label)}</span>
        </button>)}
    </nav>
    <div className="sidebar-foot" title={summary?.text}>{summary && <StatusPill tone={summary.tone}>{summary.text}</StatusPill>}</div>
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

/** The tray's and the taskbar's mark, in the brand's colour in either theme; the splash draws the same (Splash.cs). */
export function GateMark() {
  return useMemo(() => <svg width="20" height="20" viewBox="0 0 32 32" aria-hidden="true">
    <circle cx="16" cy="16" r="15.5" fill="var(--brand)" />
    <path d="M10 15 A6 6 0 0 1 22 15 V25 H10 Z" fill="#fff" />
  </svg>, []);
}
