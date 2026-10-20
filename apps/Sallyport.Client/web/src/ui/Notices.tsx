import { t } from '../i18n';
import { useId } from 'react';
import type { Notice } from '../contract';
import { post } from '../commands';
import { ActionButton, Disclosure, Icon } from './shared';

export function Notices({ notices, earlierOpen, toggleEarlier }: {
  notices: Notice[]; earlierOpen: boolean; toggleEarlier: () => void;
}) {
  const earlierId = useId();
  return <section className="notice-area" aria-label={t('Notices')}>
    {notices[0] && <NoticeView notice={notices[0]} />}
    {notices.length > 1 && <>
      <Disclosure open={earlierOpen} controls={earlierId} onClick={toggleEarlier}>{t('Earlier notices ({0})', notices.length - 1)}</Disclosure>
      <div id={earlierId} hidden={!earlierOpen}>
        {notices.slice(1).map((notice) => <NoticeView key={notice.id} notice={notice} />)}
      </div>
    </>}
  </section>;
}

function NoticeView({ notice }: { notice: Notice }) {
  return <div className={`notice notice-${notice.kind}`}>
    <Icon name={notice.kind === 'error' ? 'error' : notice.kind === 'warning' ? 'warning' : 'info'} />
    <p>{notice.text}</p>
    <ActionButton action={notice.dismiss} icon="dismiss" onClick={() => post('DISMISS_NOTICE', { id: notice.id })} />
  </div>;
}
