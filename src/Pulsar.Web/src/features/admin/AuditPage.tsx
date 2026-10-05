import { useTranslation } from 'react-i18next';
import { Button, Spinner } from '../../components/ui/Button';
import { Alert, Badge, PageHeader, Table, Td, Th } from '../../components/ui/Display';
import { errorMessage } from '../../lib/errors';
import { useFormatDate } from '../../lib/format';
import { useAuditLog, type AuditEntry } from './api';

const outcomeTone = { success: 'success', failure: 'danger', denied: 'danger' } as const;

function Actor({ entry }: { entry: AuditEntry }) {
  const { t } = useTranslation();
  if (entry.actorType === 'user') {
    return <>{entry.actorDisplay ?? entry.actorUserId}</>;
  }
  const kind = t(`audit.actors.${entry.actorType}`);
  // Nodes, scheduled tasks and API keys are named too: which node reported it, which task ran, whose key it was.
  const named =
    (entry.actorType === 'node' || entry.actorType === 'schedule' || entry.actorType === 'apiKey') &&
    entry.actorDisplay;
  return <span className="text-muted">{named ? `${kind}: ${entry.actorDisplay}` : kind}</span>;
}

export function AuditPage() {
  const { t } = useTranslation();
  const formatDate = useFormatDate();
  const audit = useAuditLog();
  const entries = audit.data?.pages.flatMap((page) => page.entries) ?? [];

  return (
    <>
      <PageHeader title={t('audit.title')} description={t('audit.description')} />
      {audit.isPending && <Spinner label={t('common.loading')} />}
      {audit.isError && <Alert>{errorMessage(t, audit.error)}</Alert>}
      {audit.data && (
        <>
          <Table label={t('audit.title')}>
            <thead>
              <tr>
                <Th>{t('audit.when')}</Th>
                <Th>{t('audit.who')}</Th>
                <Th>{t('audit.action')}</Th>
                <Th>{t('audit.outcome')}</Th>
                <Th>{t('audit.from')}</Th>
              </tr>
            </thead>
            <tbody>
              {entries.map((entry) => (
                <tr key={entry.id}>
                  <Td className="whitespace-nowrap">{formatDate(entry.occurredAt)}</Td>
                  <Td>
                    <Actor entry={entry} />
                  </Td>
                  <Td>
                    <p>{t(`audit.actions.${entry.action}`, { defaultValue: entry.action })}</p>
                    <p className="font-mono text-xs text-muted">{entry.action}</p>
                  </Td>
                  <Td>
                    <Badge tone={outcomeTone[entry.outcome]}>{t(`audit.outcomes.${entry.outcome}`)}</Badge>
                  </Td>
                  <Td className="font-mono text-xs text-muted">{entry.ipAddress ?? '—'}</Td>
                </tr>
              ))}
            </tbody>
          </Table>
          {entries.length === 0 && <p className="mt-4 text-sm text-muted">{t('audit.empty')}</p>}
          {audit.hasNextPage && (
            <div className="mt-4 flex justify-center">
              <Button variant="secondary" busy={audit.isFetchingNextPage} onClick={() => void audit.fetchNextPage()}>
                {t('actions.loadMore')}
              </Button>
            </div>
          )}
        </>
      )}
    </>
  );
}
