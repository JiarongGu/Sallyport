import { locale, t } from './i18n';

export function duration(iso: string, now: number): string {
  const seconds = Math.max(0, Math.floor((now - Date.parse(iso)) / 1000));
  if (seconds < 60) return t('{0}s', seconds);
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return t('{0}m {1}s', minutes, seconds % 60);
  const hours = Math.floor(minutes / 60);
  return t('{0}h {1}m {2}s', hours, minutes % 60, seconds % 60);
}

export function clock(iso: string): string {
  return new Date(iso).toLocaleTimeString(locale(), { hour: '2-digit', minute: '2-digit' });
}

/** "Next attempt in 12 s", while the host's deadline is ahead; null once it has passed (the next state says what follows). */
export function countdown(at: string | null, now: number): string | null {
  if (!at) return null;
  const seconds = Math.ceil((Date.parse(at) - now) / 1000);
  return seconds > 0 ? t('Next attempt in {0} s', seconds) : null;
}
