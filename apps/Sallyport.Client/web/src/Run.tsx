import { t } from './i18n';
import type { RunView } from './contract';
import { Facts, Icon } from './ui/shared';

/** On a card or the Overview, an app's route in a line: while it routes, what the app has had through the VPN and its
 *  own round trip (the owner: "its better we display its single app usage and latency"); while not, why. Whether
 *  everything went through the VPN, and the counts, are its own page's (RunDetails). */
export function RunLine({ run }: { run: RunView }) {
  if (run.phase === 'routing' && run.usage)
    return <p className="run-line secondary metadata">
      <span><Icon name="down" /> {t('Received {0}', run.usage.received)}</span>
      <span><Icon name="up" /> {t('Sent {0}', run.usage.sent)}</span>
      <span>{run.usage.latency ? t('Latency {0}', run.usage.latency) : t('Latency: not measured yet')}</span>
    </p>;
  return run.detailText ? <p className="run-line secondary metadata">{run.detailText}</p> : null;
}

/** What a route is doing, on the app's own page: its detail, whether everything went through the VPN (three outcomes),
 *  and its counts. */
export function RunDetails({ run, counts = true }: { run: RunView; counts?: boolean }) {
  return <section className="run" aria-label={t('VPN')}>
    {run.detailText && <p className="supporting secondary">{run.detailText}</p>}
    <p className={`coverage coverage-${run.coverage.result}`} role="status">
      <Icon name={run.coverage.result === 'holds' ? 'check' : run.coverage.result === 'failed' ? 'warning' : 'question'} />
      <span>{run.coverage.text}</span>
    </p>
    {counts && <Facts facts={run.facts} />}
  </section>;
}
