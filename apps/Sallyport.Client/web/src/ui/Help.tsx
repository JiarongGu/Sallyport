import { useId, useLayoutEffect, useRef, useState, type ReactNode } from 'react';
import { t } from '../i18n';
import { paintCaptionButtons } from './Chrome';
import { Icon } from './shared';

/**
 * What something is for, opened from a "?" beside it rather than written on the page (the owner: "a proper helper modal
 * instead putting all in the display"), so the page says only what a person needs to act. The words are the page's own:
 * they explain, they decide nothing (D15).
 */
export function HelpButton({ title, label, children }: { title: string; label?: string; children: ReactNode }) {
  const [open, setOpen] = useState(false);
  const about = t('About {0}', title);
  return <>
    {/* A "?" beside a heading; with `label`, a button of those words, where the help is the thing to open. */}
    {label ? <button type="button" aria-haspopup="dialog" onClick={() => setOpen(true)}>{label}</button>
      : <button type="button" className="subtle icon help-button" aria-label={about} title={about} aria-haspopup="dialog"
        onClick={() => setOpen(true)}><Icon name="question" /></button>}
    {open && <HelpDialog title={title} onClose={() => setOpen(false)}>{children}</HelpDialog>}
  </>;
}

/** A heading with its help beside it, as a section on Settings opens. */
export function HelpHeading({ id, title, children }: { id?: string; title: string; children: ReactNode }) {
  return <div className="help-heading">
    <h2 id={id}>{title}</h2>
    <HelpButton title={title}>{children}</HelpButton>
  </div>;
}

/** The help as the confirmation dialog looks; Escape, the button or a click on the dimmed page closes it, and the focus
 *  goes back to the "?" it came from. */
export function HelpDialog({ title, onClose, children }: { title: string; onClose: () => void; children: ReactNode }) {
  const dialog = useRef<HTMLDialogElement>(null);
  const close = useRef<HTMLButtonElement>(null);
  const prefix = useId();
  useLayoutEffect(() => {
    const trigger = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    const element = dialog.current;
    element?.showModal();
    paintCaptionButtons(true);
    close.current?.focus();
    return () => {
      element?.close();
      paintCaptionButtons(false);
      queueMicrotask(() => { if (trigger?.isConnected) trigger.focus(); });
    };
  }, []);

  return <dialog ref={dialog} className="confirmation help-dialog" tabIndex={-1} aria-labelledby={`${prefix}-title`}
    onCancel={(event) => { event.preventDefault(); onClose(); }}
    // The dialog's own box is all of it; a click outside lands on the dialog element itself, over its backdrop.
    onClick={(event) => { if (event.target === dialog.current) onClose(); }}>
    <h2 id={`${prefix}-title`}>{title}</h2>
    <div className="help-body">{children}</div>
    <div className="actions">
      <button ref={close} type="button" className="primary" onClick={onClose}>{t('Got it')}</button>
    </div>
  </dialog>;
}
