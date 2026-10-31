import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button } from '../../components/ui/Button';
import { Alert } from '../../components/ui/Display';
import { Checkbox, Field, Input } from '../../components/ui/Form';
import { errorMessage } from '../../lib/errors';
import { canOnNode, Permission } from '../../lib/permissions';
import { useCurrentUser } from '../auth/api';
import { useNodes } from '../nodes/api';
import { useRemoveUserQuota, useSetUserQuota, useUserQuota, type UserQuota } from './api';

/**
 * A user's quota (Pulsar 1.0.24): how many servers they may create for themselves, the memory and CPU of all of them,
 * and on which nodes, among those the one editing may create servers on.
 */
export function QuotaSection({ userId }: { userId: string }) {
  const { t } = useTranslation();
  const quota = useUserQuota(userId);
  if (quota.isPending) {
    return null;
  }
  return (
    <section className="space-y-3 border-t border-border pt-4">
      <h3 className="font-semibold">{t('quotas.title')}</h3>
      <p className="text-sm text-muted">{t('quotas.adminHint')}</p>
      {quota.isError ? (
        <Alert>{errorMessage(t, quota.error)}</Alert>
      ) : (
        <QuotaForm key={JSON.stringify(quota.data)} userId={userId} quota={quota.data ?? null} />
      )}
    </section>
  );
}

function QuotaForm({ userId, quota }: { userId: string; quota: UserQuota | null }) {
  const { t } = useTranslation();
  const me = useCurrentUser();
  const nodes = useNodes();
  const set = useSetUserQuota();
  const remove = useRemoveUserQuota();
  const [servers, setServers] = useState(String(quota?.maxServers ?? 1));
  const [memory, setMemory] = useState(String(quota?.maxMemoryMb ?? 4096));
  const [cpu, setCpu] = useState(String(quota?.maxCpuPercent ?? 100));
  const [nodeIds, setNodeIds] = useState<ReadonlySet<string>>(new Set(quota?.nodeIds ?? []));
  const [saved, setSaved] = useState(false);
  const offered =
    nodes.data?.filter((node) => canOnNode(me, node.id, Permission.ServerCreate) || nodeIds.has(node.id)) ?? [];
  const [maxServers, maxMemoryMb, maxCpuPercent] = [Number(servers), Number(memory), Number(cpu)];
  const invalid = [maxServers, maxMemoryMb, maxCpuPercent].some((n) => !Number.isInteger(n) || n < 0);

  const toggle = (id: string) =>
    setNodeIds((current) => {
      const next = new Set(current);
      if (!next.delete(id)) {
        next.add(id);
      }
      return next;
    });
  const save = () => {
    setSaved(false);
    set.mutate(
      {
        id: userId,
        body: { maxServers, maxMemoryMb, maxCpuPercent, nodeIds: [...nodeIds] },
      },
      { onSuccess: () => setSaved(true) },
    );
  };

  return (
    <div className="space-y-3">
      {(set.error ?? remove.error) && <Alert>{errorMessage(t, set.error ?? remove.error)}</Alert>}
      {saved && !set.error && (
        <p role="status" className="text-sm text-success">
          {t('users.saved')}
        </p>
      )}
      {quota && (
        <p className="text-sm text-muted">
          {t('quotas.inUse', { servers: quota.used.servers, memory: quota.used.memoryMb, cpu: quota.used.cpuPercent })}
        </p>
      )}
      <div className="grid grid-cols-1 gap-3 sm:grid-cols-3">
        <Field label={t('quotas.maxServers')}>
          {(ids) => (
            <Input {...ids} type="number" min={0} value={servers} onChange={(e) => setServers(e.target.value)} />
          )}
        </Field>
        <Field label={t('quotas.maxMemory')}>
          {(ids) => <Input {...ids} type="number" min={0} value={memory} onChange={(e) => setMemory(e.target.value)} />}
        </Field>
        <Field label={t('quotas.maxCpu')}>
          {(ids) => <Input {...ids} type="number" min={0} value={cpu} onChange={(e) => setCpu(e.target.value)} />}
        </Field>
      </div>
      <fieldset className="space-y-2">
        <legend className="text-sm font-medium">{t('quotas.nodes')}</legend>
        {offered.length === 0 && <p className="text-sm text-muted">{t('quotas.noNodesToGive')}</p>}
        {offered.map((node) => (
          <Checkbox key={node.id} label={node.name} checked={nodeIds.has(node.id)} onChange={() => toggle(node.id)} />
        ))}
      </fieldset>
      <div className="flex flex-wrap justify-end gap-2">
        {quota && (
          <Button
            variant="secondary"
            busy={remove.isPending}
            onClick={() => window.confirm(t('quotas.removeConfirm')) && remove.mutate(userId)}
          >
            {t('quotas.remove')}
          </Button>
        )}
        <Button busy={set.isPending} disabled={invalid} onClick={save}>
          {quota ? t('actions.save') : t('quotas.give')}
        </Button>
      </div>
    </div>
  );
}
