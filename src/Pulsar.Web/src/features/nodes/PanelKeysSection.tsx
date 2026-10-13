import { useTranslation } from 'react-i18next';
import { Button, Spinner } from '../../components/ui/Button';
import { Alert, Badge, Card } from '../../components/ui/Display';
import { errorMessage } from '../../lib/errors';
import { useFormatDate } from '../../lib/format';
import { usePanelKeys, useRetirePanelKey, useRotatePanelKey } from './api';

const statusTones = { active: 'success', retiring: 'accent', retired: 'neutral' } as const;

/**
 * The keys the panel signs commands with. Rotating starts signing with a new key while agents keep accepting the old
 * one; retiring it is safe once every node has the new key.
 */
export function PanelKeysSection() {
  const { t } = useTranslation();
  const formatDate = useFormatDate();
  const overview = usePanelKeys();
  const rotate = useRotatePanelKey();
  const retire = useRetirePanelKey();
  const error = overview.error ?? rotate.error ?? retire.error;
  const data = overview.data;
  const keys = data?.keys.filter((k) => k.status !== 'retired') ?? [];
  const retiring = keys.find((k) => k.status === 'retiring');

  // The question counts the nodes as they are now: one may have connected with only the old key since the page loaded.
  const askToRetire = async (keyId: string) => {
    const fresh = await overview.refetch();
    if (
      fresh.data &&
      window.confirm(
        t('panelKeys.retireConfirm', {
          outdated: fresh.data.nodesOutdated,
          unknown: fresh.data.nodesUnknown,
        }),
      )
    ) {
      retire.mutate(keyId);
    }
  };

  return (
    <section className="mt-10">
      <h2 className="mb-1 text-lg font-semibold">{t('panelKeys.title')}</h2>
      <p className="mb-4 text-sm text-muted">{t('panelKeys.description')}</p>
      {overview.isPending && <Spinner label={t('common.loading')} />}
      {error && <Alert>{errorMessage(t, error)}</Alert>}
      {data && (
        <Card className="space-y-4 p-6">
          <ul className="space-y-3">
            {keys.map((key) => (
              <li key={key.keyId} className="flex flex-wrap items-center justify-between gap-3">
                <div className="min-w-0">
                  <code className="font-mono text-xs break-all">{key.keyId}</code>
                  <p className="text-xs text-muted">
                    {t('panelKeys.created', { date: formatDate(key.createdAt) })} ·{' '}
                    {t('panelKeys.trustedBy', { count: key.nodesTrusting, total: data.nodes })}
                  </p>
                </div>
                <Badge tone={statusTones[key.status]}>{t(`panelKeys.states.${key.status}`)}</Badge>
              </li>
            ))}
          </ul>
          {retiring && (data.nodesOutdated > 0 || data.nodesUnknown > 0) && (
            <p className="text-sm text-danger">
              {t('panelKeys.notEveryNode', { outdated: data.nodesOutdated, unknown: data.nodesUnknown })}
            </p>
          )}
          <div className="flex flex-wrap gap-2">
            {retiring ? (
              <Button
                variant="secondary"
                busy={retire.isPending || overview.isFetching}
                onClick={() => void askToRetire(retiring.keyId)}
              >
                {t('panelKeys.retire')}
              </Button>
            ) : (
              <Button
                variant="secondary"
                busy={rotate.isPending}
                onClick={() => window.confirm(t('panelKeys.rotateConfirm')) && rotate.mutate()}
              >
                {t('panelKeys.rotate')}
              </Button>
            )}
          </div>
        </Card>
      )}
    </section>
  );
}
