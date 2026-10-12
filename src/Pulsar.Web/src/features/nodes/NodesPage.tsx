import { zodResolver } from '@hookform/resolvers/zod';
import { useState } from 'react';
import { useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import { Button, Spinner } from '../../components/ui/Button';
import { Dialog } from '../../components/ui/Dialog';
import { Alert, Card, PageHeader, Table, Td, Th } from '../../components/ui/Display';
import { Checkbox, Field, Input } from '../../components/ui/Form';
import { errorMessage, fieldErrors } from '../../lib/errors';
import { useFormatDate, useFormatGigabytes } from '../../lib/format';
import { can, Permission } from '../../lib/permissions';
import { useCurrentUser } from '../auth/api';
import { useWatchNodes } from '../live/hooks';
import { useApproveNodes, useCreateNode, useNodes, type CreatedNode, type NodeSummary } from './api';
import { EnrollmentCommand, NodeStateBadge } from './components';
import { EnrollmentTokensSection } from './EnrollmentTokensSection';
import { nodeSchema, type NodeFormValues } from './nodeForm';
import { AgentTransportSection } from './AgentTransportSection';
import { PanelKeysSection } from './PanelKeysSection';

export function NodesPage() {
  const { t } = useTranslation();
  const me = useCurrentUser();
  const nodes = useNodes();
  useWatchNodes();
  const formatDate = useFormatDate();
  const formatGigabytes = useFormatGigabytes();
  const [creating, setCreating] = useState(false);
  const canManage = can(me, Permission.NodeManage);

  return (
    <>
      <PageHeader
        title={t('nodes.title')}
        description={t('nodes.description')}
        actions={canManage && <Button onClick={() => setCreating(true)}>{t('nodes.create')}</Button>}
      />
      {nodes.isPending && <Spinner label={t('common.loading')} />}
      {nodes.isError && <Alert>{errorMessage(t, nodes.error)}</Alert>}
      {canManage && nodes.data && <ApprovalCard nodes={nodes.data.filter((n) => n.status === 'pendingApproval')} />}
      {nodes.data?.length === 0 && (
        <Card className="p-6">
          <p className="text-sm text-muted">{canManage ? t('nodes.emptyManage') : t('nodes.empty')}</p>
        </Card>
      )}
      {nodes.data && nodes.data.length > 0 && (
        <Table label={t('nodes.title')}>
          <thead>
            <tr>
              <Th>{t('nodes.node')}</Th>
              <Th>{t('nodes.status')}</Th>
              <Th>{t('nodes.machine')}</Th>
              <Th>{t('nodes.usage')}</Th>
              <Th>{t('nodes.lastSeen')}</Th>
            </tr>
          </thead>
          <tbody>
            {nodes.data.map((node) => (
              <tr key={node.id}>
                <Td>
                  <Link to={`/nodes/${node.id}`} className="font-medium hover:text-accent hover:underline">
                    {node.name}
                  </Link>
                  {node.description && <p className="text-xs text-muted">{node.description}</p>}
                </Td>
                <Td>
                  <NodeStateBadge node={node} />
                </Td>
                <Td>
                  {node.inventory ? (
                    <>
                      <p>{node.inventory.hostname}</p>
                      <p className="text-xs text-muted">{node.inventory.osDescription}</p>
                    </>
                  ) : (
                    <span className="text-muted">{t('common.none')}</span>
                  )}
                </Td>
                <Td>
                  {node.connected && node.metrics ? (
                    <>
                      <p>{t('nodes.cpuValue', { value: node.metrics.cpuPercent })}</p>
                      <p className="text-xs text-muted">
                        {t('nodes.memoryValue', {
                          used: formatGigabytes(node.metrics.memoryUsedBytes),
                          total: formatGigabytes(node.metrics.memoryTotalBytes),
                        })}
                      </p>
                    </>
                  ) : (
                    <span className="text-muted">—</span>
                  )}
                </Td>
                <Td>
                  {node.lastSeenAt ? (
                    formatDate(node.lastSeenAt)
                  ) : (
                    <span className="text-muted">{t('common.never')}</span>
                  )}
                </Td>
              </tr>
            ))}
          </tbody>
        </Table>
      )}
      {can(me, Permission.NodeCredentialsManage) && <EnrollmentTokensSection />}
      {can(me, Permission.SettingsManage) && <PanelKeysSection />}
      {can(me, Permission.SettingsManage) && <AgentTransportSection />}
      {creating && <CreateNodeDialog onClose={() => setCreating(false)} />}
    </>
  );
}

/**
 * Machines enrolled with a reusable token that requires approval: their agents connect once approved, all at once or
 * a few (the rest stay waiting; deleting a node turns it away). Nothing starts checked, and "select all" takes only
 * the nodes on screen: a machine a leaked token adds while the page is open is never approved without being seen.
 */
function ApprovalCard({ nodes }: { nodes: NodeSummary[] }) {
  const { t } = useTranslation();
  const formatDate = useFormatDate();
  const approve = useApproveNodes();
  const [checked, setChecked] = useState<ReadonlySet<string>>(new Set());
  if (nodes.length === 0) {
    return null;
  }

  const selected = nodes.filter((n) => checked.has(n.id)).map((n) => n.id);
  const toggle = (id: string, on: boolean) =>
    setChecked((current) => {
      const next = new Set(current);
      if (on) {
        next.add(id);
      } else {
        next.delete(id);
      }
      return next;
    });

  return (
    <Card className="mb-6 space-y-4 border-accent/40 p-6">
      <div role="region" aria-labelledby="nodes-awaiting" className="space-y-3">
        <h2 id="nodes-awaiting" className="text-lg font-semibold">
          {t('nodes.approval.title', { count: nodes.length })}
        </h2>
        <p className="text-sm text-muted">{t('nodes.approval.hint')}</p>
        {approve.isError && <Alert>{errorMessage(t, approve.error)}</Alert>}
        <Checkbox
          label={t('nodes.approval.selectAll')}
          checked={selected.length === nodes.length}
          onChange={(e) => setChecked(new Set(e.target.checked ? nodes.map((n) => n.id) : []))}
        />
        <ul className="space-y-2">
          {nodes.map((node) => (
            <li key={node.id}>
              <Checkbox
                label={
                  <>
                    <span className="font-medium">{node.name}</span>{' '}
                    <span className="text-xs text-muted">
                      {t('nodes.approval.machine', {
                        hostname: node.inventory?.hostname ?? '—',
                        os: node.inventory?.osDescription ?? '—',
                        date: formatDate(node.createdAt),
                      })}
                    </span>
                    <span className="block break-all text-xs text-muted">
                      {t('nodes.approval.origin', {
                        token: node.enrollmentTokenName ?? '—',
                        fingerprint: node.credentialFingerprint ?? '—',
                      })}
                    </span>
                  </>
                }
                checked={checked.has(node.id)}
                onChange={(e) => toggle(node.id, e.target.checked)}
              />
            </li>
          ))}
        </ul>
        <Button busy={approve.isPending} disabled={selected.length === 0} onClick={() => approve.mutate(selected)}>
          {t('nodes.approval.approve', { count: selected.length })}
        </Button>
      </div>
    </Card>
  );
}

/** Two steps: the node's name, then the one-time enrollment command for its machine. */
function CreateNodeDialog({ onClose }: { onClose: () => void }) {
  const { t } = useTranslation();
  const create = useCreateNode();
  const [created, setCreated] = useState<CreatedNode | null>(null);
  const { register, handleSubmit, formState } = useForm<NodeFormValues>({
    resolver: zodResolver(nodeSchema),
    defaultValues: { name: '', description: '', publicHost: '' },
  });
  const server = fieldErrors(create.error);
  const message = (key: keyof NodeFormValues) => {
    const error = formState.errors[key]?.message;
    return error ? t(error) : server[key];
  };

  const onSubmit = handleSubmit(({ name, description, publicHost }) =>
    create.mutate(
      { name, description: description || null, publicHost: publicHost || null },
      { onSuccess: setCreated },
    ),
  );

  if (created) {
    return (
      <Dialog
        open
        onOpenChange={(open) => !open && onClose()}
        title={t('nodes.enroll.title', { name: created.node.name })}
        size="lg"
      >
        <EnrollmentCommand instructions={created.enrollment} />
        <div className="flex justify-end gap-2 pt-6">
          <Link to={`/nodes/${created.node.id}`} className="self-center text-sm text-accent hover:underline">
            {t('nodes.openNode')}
          </Link>
          <Button onClick={onClose}>{t('actions.close')}</Button>
        </div>
      </Dialog>
    );
  }

  return (
    <Dialog open onOpenChange={(open) => !open && onClose()} title={t('nodes.create')}>
      <form onSubmit={(e) => void onSubmit(e)} className="space-y-4" noValidate>
        {create.isError && <Alert>{errorMessage(t, create.error)}</Alert>}
        <Field label={t('nodes.name')} hint={t('nodes.nameHint')} error={message('name')}>
          {(ids) => <Input {...ids} {...register('name')} autoComplete="off" />}
        </Field>
        <Field label={t('nodes.descriptionLabel')} error={message('description')}>
          {(ids) => <Input {...ids} {...register('description')} autoComplete="off" />}
        </Field>
        <Field label={t('nodes.publicHost')} hint={t('nodes.publicHostHint')} error={message('publicHost')}>
          {(ids) => <Input {...ids} {...register('publicHost')} autoComplete="off" placeholder="play.example.com" />}
        </Field>
        <div className="flex justify-end gap-2 pt-2">
          <Button variant="secondary" onClick={onClose}>
            {t('actions.cancel')}
          </Button>
          <Button type="submit" busy={create.isPending}>
            {t('actions.create')}
          </Button>
        </div>
      </form>
    </Dialog>
  );
}
