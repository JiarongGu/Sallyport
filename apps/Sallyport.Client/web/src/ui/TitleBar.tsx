import { isShenoraAvailable, useWindowMaximized, WindowCommands } from '@shenora/react';
import { useEffect, useMemo, useRef } from 'react';
import type { ClientState } from '../contract';
import { StatusPill } from './shared';

export function TitleBar({ summary }: { summary?: ClientState['summary'] }) {
  const commands = useMemo(() => new WindowCommands(), []);
  const maximized = useWindowMaximized(commands);
  const buttons = useRef<HTMLDivElement>(null);
  const hosted = isShenoraAvailable();

  useEffect(() => {
    if (!hosted) return;
    const report = () => {
      const kinds = ['minimize', 'maximize', 'close'] as const;
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
  }, [commands, hosted]);

  const run = (work: Promise<void>) => { void work.catch(() => console.error('Sallyport window command could not be sent.')); };

  return <header
    className="title-bar"
    onMouseDown={(event) => { if (event.button === 0 && hosted) run(commands.startDrag()); }}
    onDoubleClick={() => { if (hosted) run(commands.toggleMaximize()); }}
    onContextMenu={(event) => { event.preventDefault(); if (hosted) run(commands.showSystemMenu()); }}
  >
    {!maximized && <div className="top-resize" onMouseDown={(event) => {
      event.stopPropagation();
      if (event.button === 0 && hosted) run(commands.startResize('top'));
    }} />}
    <div className="brand"><GateMark /><span>Sallyport</span></div>
    <div className="window-summary" title={summary?.text}>{summary && <StatusPill tone={summary.tone} announce>{summary.text}</StatusPill>}</div>
    <div className="caption-buttons" ref={buttons} onMouseDown={(event) => event.stopPropagation()}
      onDoubleClick={(event) => event.stopPropagation()} onContextMenu={(event) => event.stopPropagation()}>
      <button type="button" aria-label="Minimize" onClick={() => run(commands.minimize())}><svg viewBox="0 0 12 12" aria-hidden="true"><path d="M1 6.5H11" /></svg></button>
      <button type="button" aria-label={maximized ? 'Restore' : 'Maximize'} onClick={() => run(commands.toggleMaximize())}>
        <svg viewBox="0 0 12 12" aria-hidden="true">{maximized ? <path d="M3.5 3.5V1.5H10.5V8.5H8.5M1.5 3.5H8.5V10.5H1.5Z" /> : <path d="M1.5 1.5H10.5V10.5H1.5Z" />}</svg>
      </button>
      <button type="button" className="caption-close" aria-label="Close" onClick={() => run(commands.close())}><svg viewBox="0 0 12 12" aria-hidden="true"><path d="M1.5 1.5L10.5 10.5M10.5 1.5L1.5 10.5" /></svg></button>
    </div>
  </header>;
}

function GateMark() {
  return <svg width="22" height="22" viewBox="0 0 32 32" aria-hidden="true">
    <circle cx="16" cy="16" r="15.5" fill="var(--accent)" />
    <path d="M10 15 A6 6 0 0 1 22 15 V25 H10 Z" fill="#fff" />
  </svg>;
}
