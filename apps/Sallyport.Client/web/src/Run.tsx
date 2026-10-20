import { t } from './i18n';
import type { RunView } from './contract';
import { Facts, Icon } from './ui/shared';

/** What a route is doing: its detail, whether everything went through the VPN (three outcomes), and its counts. */
export function RunDetails({ run }: { run: RunView }) {
  return <section className="run" aria-label={t('Routing')}>
    {run.detailText && <p className="supporting secondary">{run.detailText}</p>}
    <p className={`coverage coverage-${run.coverage.result}`} role="status">
      <Icon name={run.coverage.result === 'holds' ? 'check' : run.coverage.result === 'failed' ? 'warning' : 'question'} />
      <span>{run.coverage.text}</span>
    </p>
    <Facts facts={run.facts} />
  </section>;
}
