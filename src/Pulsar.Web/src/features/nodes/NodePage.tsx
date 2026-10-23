import type { TFunction } from 'i18next';
import { zodResolver } from '@hookform/resolvers/zod';
import { useState, type ReactNode } from 'react';
import { useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { Link, useNavigate, useParams } from 'react-router';
import { Button, Spinner } from '../../components/ui/Button';
import { Dialog } from '../../components/ui/Dialog';
import { Alert, Badge, Card, PageHeader, Table, Td, Th } from '../../components/ui/Display';
import { RefreshFailed } from '../../components/ui/RefreshFailed';
import { Field, Input } from '../../components/ui/Form';
import { ApiError } from '../../api/client';
import { errorMessage, fieldErrors } from '../../lib/errors';
import { useFormatDate, useFormatGigabytes, useFormatUptime } from '../../lib/format';
import { canOnNode, Permission } from '../../lib/permissions';
import { useCurrentUser } from '../auth/api';
import { useWatchNodes } from '../live/hooks';
import { NodeUsageHistory } from '../metrics/UsageHistory';
import {
  useApproveNode,
  useCreateEnrollmentToken,
  useDeleteNode,
  useNode,
  usePingNode,
  useRevokeCredential,
  useRotateNodeKey,
  useSetNodeEnabled,
  useUpdateNode,
  type EnrollmentInstructions,
  type NodeCredential,
  type NodeSummary,
} from './api';
import { EnrollmentCommand, NodeStateBadge } from './components';
import { nodeSchema, type NodeFormValues } from './nodeForm';
import { AllocationsSection } from './AllocationsSection';

/**
 * One node: what the agent reports (inventory, live usage), its credentials, and the administration actions. Another
 * node is another page: an edit, a dialog or enrollment instructions left open here never carry over to it.
 */
export function NodePage() {
  const { id = '' } = useParams();
  return <NodeView key={id} id={id} />;
}

function NodeView({ id }: { id: string }) {
  const { t } = useTranslation();
  const me = useCurrentUser();
  const details = useNode(id);
  useWatchNodes();
  const canManage = canOnNode(me, id, Permission.NodeManage);
  const canManageCredentials = canOnNode(me, id, Permission.NodeCredentialsManage);

  if (details.isPending) {
    return <Spinner label={t('common.loading')} />;
  }
  const notFound = details.error instanceof ApiError && details.error.code === 'node.not_found';
  // A failed refetch keeps the page (and an edit in progress); a node deleted meanwhile is gone, though.
  if (!details.data || notFound) {
    return notFound ? (
      <div className="py-16 text-center">
        <h1 className="text-xl font-semibold">{t('nodes.notFound')}</h1>
        <Link to="/nodes" className="mt-6 inline-block text-sm text-accent hover:underline">
          {t('nodes.backToList')}
        </Link>
      </div>
    ) : (
      <Alert>{errorMessage(t, details.error)}</Alert>
    );
  }

  const { node, credentials } = details.data;
  return (
    <>
      {details.isError && <RefreshFailed error={details.error} retry={() => void details.refetch()} />}
      <p className="mb-2 text-sm">
        <Link to="/nodes" className="text-muted hover:text-text">
          ← {t('nodes.title')}
        </Link>
      </p>
      <PageHeader
        title={node.name}
        description={node.description ?? undefined}
        status={
          <>
            <NodeStateBadge node={node} />
            {node.panelKeyTrust === 'outdated' && <Badge tone="danger">{t('nodes.panelKeyOutdated')}</Badge>}
          </>
        }
        actions={canManage && node.connected && <PingButton nodeId={node.id} />}
      />
      {node.status === 'pendingApproval' && <ApprovalNotice node={node} canManage={canManage} />}
      <div className="grid grid-cols-1 gap-6 xl:grid-cols-2">
        <MachineCard node={node} />
        <UsageCard node={node} />
      </div>
      <NodeUsageHistory nodeId={node.id} />
      <AllocationsSection nodeId={node.id} canManage={canManage} />
      <CredentialsSection nodeId={node.id} credentials={credentials} canManage={canManageCredentials} />
      {(canManage || canManageCredentials) && (
        <AdministrationSection node={node} canManage={canManage} canManageCredentials={canManageCredentials} />
      )}
    </>
  );
}

/** A node a reusable token created that waits for approval: its agent cannot connect until then. */
function ApprovalNotice({ node, canManage }: { node: NodeSummary; canManage: boolean }) {
  const { t } = useTranslation();
  const approve = useApproveNode();
  return (
    <Card className="mb-6 space-y-3 border-accent/40 p-6">
      <p className="text-sm">{t('nodes.approval.notice')}</p>
      {approve.isError && <Alert>{errorMessage(t, approve.error)}</Alert>}
      {canManage && (
        <Button busy={approve.isPending} onClick={() => approve.mutate(node.id)}>
          {t('nodes.approval.approveOne')}
        </Button>
      )}
    </Card>
  );
}

function PingButton({ nodeId }: { nodeId: string }) {
  const { t } = useTranslation();
  const ping = usePingNode();
  return (
    <div className="flex items-center gap-2">
      {ping.data && (
        <span role="status" className="text-xs text-muted">
          {ping.data.status === 'accepted'
            ? t('nodes.pingResult', { ms: Math.round(ping.data.roundTripMilliseconds) })
            : t('nodes.pingRefused', {
                status: t(`servers.ackStatuses.${ping.data.status}`, { defaultValue: ping.data.status }),
              })}
        </span>
      )}
      {ping.isError && (
        <span role="status" className="text-xs text-danger">
          {errorMessage(t, ping.error)}
        </span>
      )}
      <Button variant="secondary" size="sm" busy={ping.isPending} onClick={() => ping.mutate(nodeId)}>
        {t('nodes.ping')}
      </Button>
    </div>
  );
}

function Facts({ items }: { items: [string, ReactNode][] }) {
  return (
    <dl className="grid grid-cols-[minmax(8rem,auto)_1fr] gap-x-4 gap-y-2 text-sm">
      {items.map(([label, value]) => (
        <div key={label} className="contents">
          <dt className="text-muted">{label}</dt>
          <dd className="min-w-0 break-words">{value}</dd>
        </div>
      ))}
    </dl>
  );
}

/** How the agent connected last: with its node key as client certificate (Phase 16) or with the session token alone. */
function connection(t: TFunction, node: NodeSummary): string {
  if (node.connectedWithCertificate === true) {
    return t('nodes.connectionCertificate');
  }

  if (node.connectedWithCertificate === false) {
    return t(node.agentCanUseCertificate === false ? 'nodes.connectionTokenOldAgent' : 'nodes.connectionToken');
  }

  return t('nodes.connectionUnknown');
}

function MachineCard({ node }: { node: NodeSummary }) {
  const { t } = useTranslation();
  const formatGigabytes = useFormatGigabytes();
  const inventory = node.inventory;
  return (
    <Card className="p-6">
      <h2 className="mb-4 text-lg font-semibold">{t('nodes.machine')}</h2>
      {inventory ? (
        <Facts
          items={[
            [t('nodes.hostname'), inventory.hostname],
            [t('nodes.os'), `${inventory.osDescription} (${inventory.osBuild})`],
            [t('nodes.cpu'), `${inventory.cpuModel ?? '—'} · ${t('nodes.cores', { count: inventory.logicalCores })}`],
            [t('nodes.memory'), formatGigabytes(inventory.memoryTotalBytes)],
            [t('nodes.serversRoot'), inventory.serversRoot ?? t('common.none')],
            [
              t('nodes.agent'),
              t('nodes.agentVersion', { version: inventory.agentVersion, protocol: inventory.protocolVersion }),
            ],
            [t('nodes.publicHost'), node.publicHost ?? t('common.none')],
            [node.connected ? t('nodes.connection') : t('nodes.lastConnection'), connection(t, node)],
          ]}
        />
      ) : (
        <p className="text-sm text-muted">{t('nodes.noInventory')}</p>
      )}
    </Card>
  );
}

function UsageCard({ node }: { node: NodeSummary }) {
  const { t } = useTranslation();
  const formatDate = useFormatDate();
  const formatGigabytes = useFormatGigabytes();
  const formatUptime = useFormatUptime();
  const metrics = node.connected ? node.metrics : null;
  return (
    <Card className="p-6">
      <h2 className="mb-4 text-lg font-semibold">{t('nodes.usage')}</h2>
      {metrics ? (
        <div className="space-y-4">
          <Meter
            label={t('nodes.cpu')}
            percent={metrics.cpuPercent}
            value={t('nodes.cpuValue', { value: metrics.cpuPercent })}
          />
          <Meter
            label={t('nodes.memory')}
            percent={(100 * metrics.memoryUsedBytes) / metrics.memoryTotalBytes}
            value={t('nodes.memoryValue', {
              used: formatGigabytes(metrics.memoryUsedBytes),
              total: formatGigabytes(metrics.memoryTotalBytes),
            })}
          />
          {metrics.disks?.map((disk) => (
            <Meter
              key={disk.root}
              label={t('nodes.disk', {
                root: disk.root,
                uses: disk.uses.map((use) => t(`nodes.diskUses.${use}`, { defaultValue: use })).join(', '),
              })}
              percent={disk.totalBytes > 0 ? (100 * (disk.totalBytes - disk.freeBytes)) / disk.totalBytes : 0}
              value={t('nodes.diskValue', {
                free: formatGigabytes(disk.freeBytes),
                total: formatGigabytes(disk.totalBytes),
              })}
            />
          ))}
          <Facts
            items={[
              [t('nodes.uptime'), formatUptime(metrics.uptime)],
              [t('nodes.updated'), formatDate(metrics.at)],
            ]}
          />
        </div>
      ) : (
        <p className="text-sm text-muted">
          {node.lastSeenAt ? t('nodes.offlineSince', { date: formatDate(node.lastSeenAt) }) : t('nodes.neverConnected')}
        </p>
      )}
    </Card>
  );
}

function Meter({ label, percent, value }: { label: string; percent: number; value: string }) {
  const clamped = Math.min(100, Math.max(0, percent));
  return (
    <div>
      <div className="mb-1 flex justify-between text-sm">
        <span className="text-muted">{label}</span>
        <span>{value}</span>
      </div>
      <div
        role="meter"
        aria-label={label}
        aria-valuemin={0}
        aria-valuemax={100}
        aria-valuenow={Math.round(clamped)}
        aria-valuetext={value}
        className="h-2 overflow-hidden rounded-full bg-surface-raised"
      >
        <div className="h-full rounded-full bg-accent" style={{ width: `${clamped}%` }} />
      </div>
    </div>
  );
}

const credentialTones = { active: 'success', pending: 'neutral', superseded: 'neutral', revoked: 'danger' } as const;

function CredentialsSection({
  nodeId,
  credentials,
  canManage,
}: {
  nodeId: string;
  credentials: NodeCredential[];
  canManage: boolean;
}) {
  const { t } = useTranslation();
  const formatDate = useFormatDate();
  const [revoking, setRevoking] = useState<NodeCredential | null>(null);
  return (
    <section className="mt-8">
      <h2 className="mb-1 text-lg font-semibold">{t('nodes.credentials.title')}</h2>
      <p className="mb-4 text-sm text-muted">{t('nodes.credentials.description')}</p>
      {credentials.length === 0 ? (
        <Card className="p-6">
          <p className="text-sm text-muted">{t('nodes.credentials.empty')}</p>
        </Card>
      ) : (
        <Table label={t('nodes.credentials.title')}>
          <thead>
            <tr>
              <Th>{t('nodes.credentials.fingerprint')}</Th>
              <Th>{t('nodes.status')}</Th>
              <Th>{t('nodes.credentials.created')}</Th>
              <Th>{t('nodes.credentials.lastUsed')}</Th>
              {canManage && (
                <Th>
                  <span className="sr-only">{t('common.actions')}</span>
                </Th>
              )}
            </tr>
          </thead>
          <tbody>
            {credentials.map((credential) => (
              <tr key={credential.id}>
                <Td>
                  <code className="font-mono text-xs break-all">{credential.fingerprint}</code>
                  <p className="text-xs text-muted">{t(`nodes.credentials.storage.${credential.keyStorage}`)}</p>
                </Td>
                <Td>
                  <Badge tone={credentialTones[credential.status]}>
                    {t(`nodes.credentials.states.${credential.status}`)}
                  </Badge>
                  {credential.revokedAt && (
                    <p className="mt-1 text-xs text-muted">
                      {formatDate(credential.revokedAt)}
                      {credential.revokeReason && ` · ${credential.revokeReason}`}
                    </p>
                  )}
                  {credential.supersededAt && (
                    <p className="mt-1 text-xs text-muted">{formatDate(credential.supersededAt)}</p>
                  )}
                </Td>
                <Td>{formatDate(credential.createdAt)}</Td>
                <Td>
                  {credential.lastUsedAt ? (
                    formatDate(credential.lastUsedAt)
                  ) : (
                    <span className="text-muted">{t('common.never')}</span>
                  )}
                </Td>
                {canManage && (
                  <Td className="text-right">
                    {credential.status === 'active' && (
                      <Button variant="ghost" size="sm" onClick={() => setRevoking(credential)}>
                        {t('nodes.credentials.revoke')}
                      </Button>
                    )}
                  </Td>
                )}
              </tr>
            ))}
          </tbody>
        </Table>
      )}
      {revoking && <RevokeDialog nodeId={nodeId} credential={revoking} onClose={() => setRevoking(null)} />}
    </section>
  );
}

function RevokeDialog({
  nodeId,
  credential,
  onClose,
}: {
  nodeId: string;
  credential: NodeCredential;
  onClose: () => void;
}) {
  const { t } = useTranslation();
  const revoke = useRevokeCredential();
  const [reason, setReason] = useState('');
  return (
    <Dialog
      open
      onOpenChange={(open) => !open && onClose()}
      title={t('nodes.credentials.revokeTitle')}
      description={t('nodes.credentials.revokeDescription')}
    >
      <div className="space-y-4">
        {revoke.isError && <Alert>{errorMessage(t, revoke.error)}</Alert>}
        <code className="block font-mono text-xs break-all">{credential.fingerprint}</code>
        <Field label={t('nodes.credentials.reason')}>
          {(ids) => <Input {...ids} value={reason} maxLength={256} onChange={(e) => setReason(e.target.value)} />}
        </Field>
        <div className="flex justify-end gap-2">
          <Button variant="secondary" onClick={onClose}>
            {t('actions.cancel')}
          </Button>
          <Button
            variant="danger"
            busy={revoke.isPending}
            onClick={() =>
              revoke.mutate(
                { id: nodeId, credentialId: credential.id, reason: reason.trim() || null },
                { onSuccess: onClose },
              )
            }
          >
            {t('nodes.credentials.revoke')}
          </Button>
        </div>
      </div>
    </Dialog>
  );
}

function AdministrationSection({
  node,
  canManage,
  canManageCredentials,
}: {
  node: NodeSummary;
  canManage: boolean;
  canManageCredentials: boolean;
}) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const setEnabled = useSetNodeEnabled();
  const remove = useDeleteNode();
  const token = useCreateEnrollmentToken();
  const rotate = useRotateNodeKey();
  const [editing, setEditing] = useState(false);
  const [instructions, setInstructions] = useState<EnrollmentInstructions | null>(null);
  const error = setEnabled.error ?? remove.error ?? token.error ?? rotate.error;
  const disabled = node.status === 'disabled';

  return (
    <section className="mt-8 space-y-4">
      <h2 className="text-lg font-semibold">{t('nodes.administration')}</h2>
      {error && <Alert>{errorMessage(t, error)}</Alert>}
      {rotate.data && (
        <p role="status" className="text-sm text-muted">
          {rotate.data.status === 'accepted' || rotate.data.status === 'alreadyInState'
            ? t('nodes.rotateStarted')
            : t('nodes.pingRefused', {
                status: t(`servers.ackStatuses.${rotate.data.status}`, { defaultValue: rotate.data.status }),
              })}
        </p>
      )}
      <div className="flex flex-wrap gap-2">
        {canManage && (
          <Button variant="secondary" onClick={() => setEditing(true)}>
            {t('actions.edit')}
          </Button>
        )}
        {canManageCredentials && node.connected && (
          <Button variant="secondary" busy={rotate.isPending} onClick={() => rotate.mutate(node.id)}>
            {t('nodes.rotateKey')}
          </Button>
        )}
        {canManageCredentials && (
          <Button
            variant="secondary"
            busy={token.isPending}
            disabled={disabled}
            onClick={() =>
              token.mutate(node.id, {
                // The token lives only in the dialog's state, gone once it closes.
                onSuccess: (created) => {
                  setInstructions(created);
                  token.reset();
                },
              })
            }
          >
            {node.status === 'pendingEnrollment' ? t('nodes.newToken') : t('nodes.reenroll')}
          </Button>
        )}
        {canManage && (
          <Button
            variant="secondary"
            busy={setEnabled.isPending}
            onClick={() =>
              (disabled || window.confirm(t('nodes.disableConfirm', { name: node.name }))) &&
              setEnabled.mutate({ id: node.id, enabled: disabled })
            }
          >
            {disabled ? t('nodes.enable') : t('nodes.disable')}
          </Button>
        )}
        {canManage && (
          <Button
            variant="danger"
            busy={remove.isPending}
            onClick={() =>
              window.confirm(t('nodes.deleteConfirm', { name: node.name })) &&
              remove.mutate(node.id, { onSuccess: () => void navigate('/nodes') })
            }
          >
            {t('actions.delete')}
          </Button>
        )}
      </div>
      <p className="text-xs text-muted">{t('nodes.administrationHint')}</p>
      {editing && <EditNodeDialog node={node} onClose={() => setEditing(false)} />}
      {instructions && (
        <Dialog
          open
          onOpenChange={(open) => !open && setInstructions(null)}
          title={t('nodes.enroll.title', { name: node.name })}
          size="lg"
        >
          <EnrollmentCommand instructions={instructions} />
          <div className="flex justify-end pt-6">
            <Button onClick={() => setInstructions(null)}>{t('actions.close')}</Button>
          </div>
        </Dialog>
      )}
    </section>
  );
}

function EditNodeDialog({ node, onClose }: { node: NodeSummary; onClose: () => void }) {
  const { t } = useTranslation();
  const update = useUpdateNode();
  const { register, handleSubmit, formState } = useForm<NodeFormValues>({
    resolver: zodResolver(nodeSchema),
    defaultValues: { name: node.name, description: node.description ?? '', publicHost: node.publicHost ?? '' },
  });
  const server = fieldErrors(update.error);
  const message = (key: keyof NodeFormValues) => {
    const error = formState.errors[key]?.message;
    return error ? t(error) : server[key];
  };

  const onSubmit = handleSubmit(({ name, description, publicHost }) =>
    update.mutate(
      { id: node.id, body: { name, description: description || null, publicHost: publicHost || null } },
      { onSuccess: onClose },
    ),
  );

  return (
    <Dialog open onOpenChange={(open) => !open && onClose()} title={t('nodes.editTitle', { name: node.name })}>
      <form onSubmit={(e) => void onSubmit(e)} className="space-y-4" noValidate>
        {update.isError && <Alert>{errorMessage(t, update.error)}</Alert>}
        <Field label={t('nodes.name')} error={message('name')}>
          {(ids) => <Input {...ids} {...register('name')} autoComplete="off" />}
        </Field>
        <Field label={t('nodes.descriptionLabel')} error={message('description')}>
          {(ids) => <Input {...ids} {...register('description')} autoComplete="off" />}
        </Field>
        <Field label={t('nodes.publicHost')} hint={t('nodes.publicHostHint')} error={message('publicHost')}>
          {(ids) => <Input {...ids} {...register('publicHost')} autoComplete="off" />}
        </Field>
        <div className="flex justify-end gap-2 pt-2">
          <Button variant="secondary" onClick={onClose}>
            {t('actions.cancel')}
          </Button>
          <Button type="submit" busy={update.isPending}>
            {t('actions.save')}
          </Button>
        </div>
      </form>
    </Dialog>
  );
}
