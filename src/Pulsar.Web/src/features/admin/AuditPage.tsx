import { useTranslation } from 'react-i18next';
import { useMutation } from '@tanstack/react-query';
import { useSearchParams } from 'react-router';
import { Button, Spinner } from '../../components/ui/Button';
import { Alert, Badge, PageHeader, Table, Td, Th } from '../../components/ui/Display';
import { Field, Input } from '../../components/ui/Form';
import { errorMessage } from '../../lib/errors';
import { useFormatDate } from '../../lib/format';
import { exportAudit, useAuditLog, type AuditEntry, type AuditFilters } from './api';
import { auditCsv } from './auditExport';

const outcomeTone = { success: 'success', failure: 'danger', denied: 'danger' } as const;
const filterNames = ['action', 'actorUserId', 'from', 'until', 'targetType', 'targetId'] as const;

function readFilters(params: URLSearchParams): AuditFilters {
  const result: AuditFilters = {};
  for (const key of filterNames) {
    const value = params.get(key)?.trim();
    if (!value) continue;
    if (key === 'from' || key === 'until') {
      const date = new Date(value);
      result[key] = Number.isNaN(date.getTime()) ? value : date.toISOString();
    } else result[key] = value;
  }
  return result;
}

function localDateTime(value: string | null) {
  if (!value) return '';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;
  const pad = (part: number) => String(part).padStart(2, '0');
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`;
}

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
  const [params, setParams] = useSearchParams();
  const filters = readFilters(params);
  const audit = useAuditLog(filters);
  const exporting = useMutation({
    mutationFn: exportAudit,
    onSuccess: (page) => {
      const url = URL.createObjectURL(new Blob([auditCsv(page.entries)], { type: 'text/csv;charset=utf-8' }));
      const link = document.createElement('a');
      link.href = url;
      link.download = 'pulsar-audit.csv';
      link.click();
      URL.revokeObjectURL(url);
    },
  });
  const entries = audit.data?.pages.flatMap((page) => page.entries) ?? [];

  return (
    <>
      <PageHeader title={t('audit.title')} description={t('audit.description')} />
      <form
        key={params.toString()}
        className="mb-5 space-y-3"
        onSubmit={(event) => {
          event.preventDefault();
          const form = new FormData(event.currentTarget);
          const next = new URLSearchParams(params);
          for (const key of filterNames) {
            const value = String(form.get(key) ?? '').trim();
            if (value) next.set(key, key === 'from' || key === 'until' ? new Date(value).toISOString() : value);
            else next.delete(key);
          }
          exporting.reset();
          setParams(next);
        }}
      >
        <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-3">
          {filterNames.map((name) => (
            <Field key={name} label={t(`audit.filters.${name}`)}>
              {(control) => (
                <Input
                  {...control}
                  name={name}
                  defaultValue={
                    name === 'from' || name === 'until' ? localDateTime(params.get(name)) : (params.get(name) ?? '')
                  }
                  type={name === 'from' || name === 'until' ? 'datetime-local' : 'text'}
                  {...(name === 'actorUserId'
                    ? { pattern: '[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}' }
                    : {})}
                  maxLength={name === 'action' ? 64 : name === 'targetType' ? 32 : name === 'targetId' ? 512 : 64}
                />
              )}
            </Field>
          ))}
        </div>
        <div className="flex flex-wrap items-center gap-3">
          <Button type="submit" variant="secondary">
            {t('audit.filters.apply')}
          </Button>
          <Button
            variant="ghost"
            onClick={() => {
              const next = new URLSearchParams(params);
              filterNames.forEach((key) => next.delete(key));
              exporting.reset();
              setParams(next);
            }}
          >
            {t('audit.filters.clear')}
          </Button>
          <Button variant="secondary" busy={exporting.isPending} onClick={() => exporting.mutate(filters)}>
            {t('audit.filters.export')}
          </Button>
          <p className="text-xs text-muted">{t('audit.filters.exportHint')}</p>
        </div>
      </form>
      {exporting.isError && <Alert>{errorMessage(t, exporting.error)}</Alert>}
      {audit.isPending && <Spinner label={t('common.loading')} />}
      {audit.isError && <Alert>{errorMessage(t, audit.error)}</Alert>}
      {audit.data && (
        <>
          <Table label={t('audit.title')}>
            <thead>
              <tr>
                <Th>{t('audit.when')}</Th>
                <Th className="hidden md:table-cell">{t('audit.who')}</Th>
                <Th>{t('audit.action')}</Th>
                <Th>{t('audit.outcome')}</Th>
                <Th className="hidden lg:table-cell">{t('audit.from')}</Th>
              </tr>
            </thead>
            <tbody>
              {entries.map((entry) => (
                <tr key={entry.id}>
                  <Td>
                    <p className="whitespace-nowrap">{formatDate(entry.occurredAt)}</p>
                    {/* On a phone, who did it goes under when: the table keeps three columns. */}
                    <p className="text-xs md:hidden">
                      <Actor entry={entry} />
                    </p>
                  </Td>
                  <Td className="hidden md:table-cell">
                    <Actor entry={entry} />
                  </Td>
                  <Td>
                    <p>{t(`audit.actions.${entry.action}`, { defaultValue: entry.action })}</p>
                    <p className="font-mono text-xs text-muted">{entry.action}</p>
                    {entry.targetType && (
                      <p className="max-w-56 break-words font-mono text-xs text-muted">
                        {entry.targetType}: {entry.targetId ?? '—'}
                      </p>
                    )}
                  </Td>
                  <Td>
                    <Badge tone={outcomeTone[entry.outcome]}>{t(`audit.outcomes.${entry.outcome}`)}</Badge>
                  </Td>
                  <Td className="hidden font-mono text-xs text-muted lg:table-cell">{entry.ipAddress ?? '—'}</Td>
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
