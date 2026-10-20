import { RefreshCw, Download } from 'lucide-react';
import { useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { Button, Spinner } from '../../components/ui/Button';
import { Dialog } from '../../components/ui/Dialog';
import { Alert, Badge, Card, PageHeader, Table, Td, Th } from '../../components/ui/Display';
import { Checkbox, Field, Input } from '../../components/ui/Form';
import { errorMessage } from '../../lib/errors';
import { useFormatDate } from '../../lib/format';
import { useCurrentUser } from '../auth/api';
import {
  useApplyUpdate,
  useCheckForUpdates,
  useUpdates,
  useUpdateSettings,
  type NodeUpdateView,
  type UpdateRequestResult,
} from './api';

/**
 * Pulsar's updates (settings.manage, Phase 19): the panel's version, the newest release when it is newer (its notes are
 * the release's signed manifest's, plain text), the daily look for releases, and each node with how its last update
 * went. Updating asks for the password (and the code) again; the panel's own machine goes first.
 */
export function UpdatesPage() {
  const { t } = useTranslation();
  const formatDate = useFormatDate();
  const updates = useUpdates();
  const check = useCheckForUpdates();
  const settings = useUpdateSettings();
  const [asking, setAsking] = useState<NodeUpdateView[] | null>(null);
  const data = updates.data;
  const ready = data?.available ? data.nodes.filter((n) => n.updateAvailable && n.blocked === null) : [];

  return (
    <>
      <PageHeader
        title={t('updates.title')}
        description={t('updates.description')}
        actions={
          <Button variant="secondary" icon={RefreshCw} busy={check.isPending} onClick={() => check.mutate()}>
            {t('updates.checkNow')}
          </Button>
        }
      />
      {updates.isPending && <Spinner label={t('common.loading')} />}
      {updates.isError && <Alert>{errorMessage(t, updates.error)}</Alert>}
      {check.isError && <Alert>{errorMessage(t, check.error)}</Alert>}
      {data && (
        <div className="space-y-6">
          <Card className="space-y-3 p-5">
            <p className="text-sm text-muted">{t('updates.panelVersion', { version: data.panelVersion })}</p>
            {!data.hasKeys && <Alert>{t('updates.noKeys')}</Alert>}
            {data.available ? (
              <div className="space-y-2">
                <h2 className="text-lg font-semibold">{t('updates.available', { version: data.available.version })}</h2>
                <p className="text-sm text-muted">
                  {t('updates.published', { when: formatDate(data.available.published) })}
                </p>
                {data.available.notes && (
                  <p className="rounded-md border border-border bg-bg p-3 text-sm whitespace-pre-wrap">
                    {data.available.notes}
                  </p>
                )}
                {data.available.newerThanPanel && <p className="text-sm">{t('updates.panelFirst')}</p>}
              </div>
            ) : (
              <h2 className="text-lg font-semibold">{t('updates.upToDate')}</h2>
            )}
            <p className="text-xs text-muted">
              {data.checkedAt
                ? t('updates.checkedAt', { when: formatDate(data.checkedAt) })
                : t('updates.neverChecked')}
            </p>
            {data.checkError && <Alert>{t('updates.checkFailed', { error: data.checkError })}</Alert>}
          </Card>

          <Card className="space-y-2 p-5">
            <Checkbox
              label={t('updates.checkAutomatically')}
              checked={data.checkAutomatically}
              disabled={settings.isPending}
              onChange={(event) => settings.mutate(event.target.checked)}
            />
            <p className="text-sm text-muted">{t('updates.checkHint')}</p>
            {settings.isError && <Alert>{errorMessage(t, settings.error)}</Alert>}
          </Card>

          <Card className="p-0">
            <div className="flex flex-wrap items-center justify-between gap-3 p-5 pb-3">
              <h2 className="font-semibold">{t('updates.nodes')}</h2>
              {ready.length > 1 && (
                <Button icon={Download} onClick={() => setAsking(ready)}>
                  {t('updates.updateAll', { count: ready.length })}
                </Button>
              )}
            </div>
            <Table label={t('updates.nodes')}>
              <thead>
                <tr>
                  <Th>{t('updates.node')}</Th>
                  <Th>{t('updates.version')}</Th>
                  <Th>{t('updates.lastUpdate')}</Th>
                  <Th className="text-right">{t('common.actions')}</Th>
                </tr>
              </thead>
              <tbody>
                {data.nodes.map((node) => (
                  <tr key={node.nodeId}>
                    <Td>
                      <span className="font-medium">{node.name}</span>{' '}
                      {node.hostsPanel && <Badge tone="accent">{t('updates.panelHost')}</Badge>}
                      {!node.connected && <Badge>{t('updates.offline')}</Badge>}
                    </Td>
                    <Td className="font-mono text-xs">{node.agentVersion ?? '—'}</Td>
                    <Td>
                      <NodeStatus node={node} />
                    </Td>
                    <Td className="text-right">
                      {data.available && node.updateAvailable && node.blocked === null ? (
                        <Button size="sm" icon={Download} onClick={() => setAsking([node])}>
                          {node.hostsPanel ? t('updates.updatePanel') : t('updates.update')}
                        </Button>
                      ) : (
                        data.available &&
                        node.updateAvailable &&
                        node.blocked && <span className="text-xs text-muted">{t(`errors.${node.blocked}`)}</span>
                      )}
                    </Td>
                  </tr>
                ))}
              </tbody>
            </Table>
          </Card>
        </div>
      )}
      {asking && data?.available && (
        <ApplyDialog nodes={asking} version={data.available.version} onClose={() => setAsking(null)} />
      )}
    </>
  );
}

function NodeStatus({ node }: { node: NodeUpdateView }) {
  const { t } = useTranslation();
  const formatDate = useFormatDate();
  const last = node.last;
  if (!last) {
    return <span className="text-xs text-muted">—</span>;
  }
  const tone =
    last.status === 'succeeded'
      ? 'success'
      : last.status === 'failed' || last.status === 'rolled_back'
        ? 'danger'
        : 'accent';
  return (
    <div className="space-y-1">
      <Badge tone={tone}>
        {t(`updates.status.${last.status}`, { defaultValue: last.status })}
        {last.to ? ` ${last.to}` : ''}
      </Badge>
      <p className="text-xs text-muted">{formatDate(last.updatedAt)}</p>
      {last.error && <p className="text-xs break-words whitespace-pre-wrap text-danger">{last.error}</p>}
    </div>
  );
}

function ApplyDialog({ nodes, version, onClose }: { nodes: NodeUpdateView[]; version: string; onClose: () => void }) {
  const { t } = useTranslation();
  const me = useCurrentUser();
  const apply = useApplyUpdate();
  const [password, setPassword] = useState('');
  const [code, setCode] = useState('');
  const [refused, setRefused] = useState<UpdateRequestResult[]>([]);
  const close = () => {
    apply.reset(); // the password and the code were its variables
    onClose();
  };
  const names = new Map(nodes.map((n) => [n.nodeId, n.name]));

  const submit = (event: FormEvent) => {
    event.preventDefault();
    apply.mutate(
      {
        nodeIds: nodes.map((n) => n.nodeId),
        password,
        code: me.twoFactorEnabled ? code.replace(/[\s-]/g, '') : null,
      },
      {
        onSuccess: (results) => {
          const notSent = results.filter((r) => !r.sent);
          if (notSent.length === 0) {
            close();
          } else {
            setRefused(notSent);
          }
        },
      },
    );
  };

  return (
    <Dialog
      open
      onOpenChange={(next) => !next && !apply.isPending && close()}
      title={t('updates.confirmTitle', { version })}
      description={t('updates.confirmHint')}
    >
      <form onSubmit={submit} className="space-y-4" noValidate>
        <ul className="list-inside list-disc text-sm">
          {nodes.map((n) => (
            <li key={n.nodeId}>
              {n.name}
              {n.hostsPanel ? ` (${t('updates.panelHost')})` : ''}
            </li>
          ))}
        </ul>
        {apply.isError && <Alert>{errorMessage(t, apply.error)}</Alert>}
        {refused.length > 0 && (
          <Alert>
            {refused.map((r) => (
              <span key={r.nodeId} className="block">
                {names.get(r.nodeId) ?? r.nodeId}: {r.message ?? t(`errors.${r.errorCode ?? 'unexpected'}`)}
              </span>
            ))}
          </Alert>
        )}
        <Field label={t('account.currentPassword')}>
          {(ids) => (
            <Input
              {...ids}
              type="password"
              autoComplete="current-password"
              value={password}
              onChange={(e) => setPassword(e.target.value)}
            />
          )}
        </Field>
        {me.twoFactorEnabled && (
          <Field label={t('twoFactor.appCode')}>
            {(ids) => (
              <Input
                {...ids}
                value={code}
                onChange={(e) => setCode(e.target.value)}
                inputMode="numeric"
                autoComplete="one-time-code"
                maxLength={9}
              />
            )}
          </Field>
        )}
        <div className="flex justify-end gap-2">
          <Button type="button" variant="ghost" disabled={apply.isPending} onClick={close}>
            {refused.length > 0 ? t('actions.close') : t('actions.cancel')}
          </Button>
          {refused.length === 0 && (
            <Button
              type="submit"
              icon={Download}
              busy={apply.isPending}
              disabled={password.length === 0 || (me.twoFactorEnabled && code.replace(/[\s-]/g, '').length !== 6)}
            >
              {t('updates.confirm')}
            </Button>
          )}
        </div>
      </form>
    </Dialog>
  );
}
