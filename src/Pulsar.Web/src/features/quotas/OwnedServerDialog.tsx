import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router';
import { Button } from '../../components/ui/Button';
import { Dialog } from '../../components/ui/Dialog';
import { Alert } from '../../components/ui/Display';
import { Field, Input, Select } from '../../components/ui/Form';
import { errorMessage } from '../../lib/errors';
import { VariableFields } from '../servers/VariableFields';
import { initialValue, useUnreadableNumbers, variableError } from '../servers/variables';
import { useCreateOwnedServer, type QuotaView } from './api';

/** Smallest memory cap a server can have (the API's Server.MinMemoryLimitMb). */
const minMemoryMb = 128;

/** What the quota has left, and what it allows in all. */
export function QuotaSummary({ quota }: { quota: QuotaView }) {
  const { t } = useTranslation();
  return (
    <dl className="grid grid-cols-3 gap-3 text-sm">
      {(
        [
          ['servers', quota.used.servers, quota.maxServers],
          ['memory', quota.used.memoryMb, quota.maxMemoryMb],
          ['cpu', quota.used.cpuPercent, quota.maxCpuPercent],
        ] as const
      ).map(([name, used, max]) => (
        <div key={name} className="rounded-lg border border-border bg-surface-raised/50 p-3">
          <dt className="text-xs text-muted">{t(`quotas.${name}`)}</dt>
          <dd className="mt-1 font-semibold tabular-nums">{t('quotas.usedOf', { used, max })}</dd>
        </div>
      ))}
    </dl>
  );
}

/**
 * A server the user creates for themselves (Pulsar 1.0.24): on one of their quota's nodes, as one of the games offered,
 * with the memory and CPU it may use out of what the quota has left. Its ports are chosen by the panel; it is theirs.
 */
export function OwnedServerDialog({ quota, onClose }: { quota: QuotaView; onClose: () => void }) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const create = useCreateOwnedServer();
  const nodes = quota.nodes.filter((node) => node.connected);
  const [nodeId, setNodeId] = useState(nodes[0]?.id ?? '');
  const [templateId, setTemplateId] = useState('');
  const [name, setName] = useState('');
  const memoryLeft = Math.max(0, quota.maxMemoryMb - quota.used.memoryMb);
  const cpuLeft = Math.max(0, quota.maxCpuPercent - quota.used.cpuPercent);
  const [memory, setMemory] = useState(String(Math.min(memoryLeft, 2048)));
  const [cpu, setCpu] = useState(String(Math.min(cpuLeft, 100)));
  const [values, setValues] = useState<Record<string, string>>({});
  const [showErrors, setShowErrors] = useState(false);
  const unreadable = useUnreadableNumbers();
  const game = quota.games.find((g) => g.templateId === templateId);
  const needed = game?.variables ?? [];
  const valueOf = (key: string) => {
    const variable = needed.find((v) => v.key === key);
    return values[key] ?? (variable ? initialValue(variable) : '');
  };
  const full = quota.used.servers >= quota.maxServers;

  const memoryNumber = Number(memory);
  const cpuNumber = Number(cpu);
  const errors = {
    node: nodes.some((n) => n.id === nodeId) ? null : 'validation.required',
    game: game ? null : 'validation.required',
    name: name.trim() === '' ? 'validation.required' : null,
    memory:
      !Number.isInteger(memoryNumber) || memoryNumber < minMemoryMb || memoryNumber > memoryLeft
        ? t('quotas.memoryRange', { min: minMemoryMb, max: memoryLeft })
        : null,
    cpu:
      !Number.isInteger(cpuNumber) || cpuNumber < 1 || cpuNumber > Math.min(100, cpuLeft)
        ? t('quotas.cpuRange', { max: Math.min(100, cpuLeft) })
        : null,
  };
  const variableErrors = Object.fromEntries(
    needed.map((v) => [v.key, variableError(v, valueOf(v.key), false, unreadable.keys.has(v.key))]),
  );

  const submit = () => {
    if (Object.values(errors).some((e) => e !== null) || Object.values(variableErrors).some((e) => e !== null)) {
      setShowErrors(true);
      return;
    }
    create.mutate(
      {
        nodeId,
        templateId,
        name: name.trim(),
        memoryMb: memoryNumber,
        cpuPercent: cpuNumber,
        variables: Object.fromEntries(needed.map((v) => [v.key, valueOf(v.key)])),
      },
      { onSuccess: (created) => void navigate(`/servers/${created.server.id}?tab=console`) },
    );
  };
  const shown = (error: string | null) => (showErrors && error ? t(error) : undefined);

  return (
    <Dialog open onOpenChange={(open) => !open && onClose()} title={t('quotas.create')} size="lg">
      <div className="space-y-4">
        <QuotaSummary quota={quota} />
        {create.isError && <Alert>{errorMessage(t, create.error)}</Alert>}
        {full && <Alert>{t('errors.quota.servers')}</Alert>}
        {nodes.length === 0 && <p className="text-sm text-muted">{t('quotas.noNodes')}</p>}
        <Field label={t('servers.node')} error={shown(errors.node)}>
          {(ids) => (
            <Select {...ids} value={nodeId} onChange={(e) => setNodeId(e.target.value)}>
              <option value="">{t('servers.chooseNode')}</option>
              {nodes.map((node) => (
                <option key={node.id} value={node.id}>
                  {node.name}
                </option>
              ))}
            </Select>
          )}
        </Field>
        <Field label={t('quotas.game')} error={shown(errors.game)}>
          {(ids) => (
            <Select
              {...ids}
              value={templateId}
              onChange={(e) => {
                setTemplateId(e.target.value);
                setValues({});
                unreadable.reset();
              }}
            >
              <option value="">{t('quotas.chooseGame')}</option>
              {quota.games.map((g) => (
                <option key={g.templateId} value={g.templateId}>
                  {g.name}
                </option>
              ))}
            </Select>
          )}
        </Field>
        <Field label={t('servers.name')} error={shown(errors.name)}>
          {(ids) => <Input {...ids} value={name} maxLength={64} onChange={(e) => setName(e.target.value)} />}
        </Field>
        <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
          <Field
            label={t('quotas.memoryLabel')}
            hint={game?.minMemoryMb ? t('quotas.gameNeeds', { memory: game.minMemoryMb }) : undefined}
            error={showErrors ? (errors.memory ?? undefined) : undefined}
          >
            {(ids) => (
              <Input
                {...ids}
                type="number"
                inputMode="numeric"
                value={memory}
                onChange={(e) => setMemory(e.target.value)}
              />
            )}
          </Field>
          <Field label={t('quotas.cpuLabel')} error={showErrors ? (errors.cpu ?? undefined) : undefined}>
            {(ids) => (
              <Input {...ids} type="number" inputMode="numeric" value={cpu} onChange={(e) => setCpu(e.target.value)} />
            )}
          </Field>
        </div>
        {needed.length > 0 && (
          <VariableFields
            variables={needed}
            values={Object.fromEntries(needed.map((v) => [v.key, valueOf(v.key)]))}
            onChange={(key, value) => setValues((current) => ({ ...current, [key]: value }))}
            errors={showErrors ? variableErrors : undefined}
            onUnreadable={unreadable.mark}
          />
        )}
        <div className="flex justify-end gap-2">
          <Button variant="secondary" onClick={onClose}>
            {t('actions.cancel')}
          </Button>
          <Button busy={create.isPending} disabled={full || nodes.length === 0} onClick={submit}>
            {t('quotas.createAction')}
          </Button>
        </div>
      </div>
    </Dialog>
  );
}
