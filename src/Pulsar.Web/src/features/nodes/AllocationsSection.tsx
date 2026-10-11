import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import { Button, Spinner } from '../../components/ui/Button';
import { Alert, Card, Table, Td, Th } from '../../components/ui/Display';
import { Field, Input } from '../../components/ui/Form';
import { errorMessage } from '../../lib/errors';
import { useAllocations, useCreateAllocations, useDeleteAllocation } from './api';

/**
 * The node's ip:port pairs servers can use (docs/architecture/02). Each one serves both UDP and TCP; 0.0.0.0 means
 * every interface of the node, so it cannot coexist with the same port on a specific address.
 */
export function AllocationsSection({ nodeId, canManage }: { nodeId: string; canManage: boolean }) {
  const { t } = useTranslation();
  const allocations = useAllocations(nodeId);
  const create = useCreateAllocations();
  const remove = useDeleteAllocation();
  const [address, setAddress] = useState('0.0.0.0');
  const [ports, setPorts] = useState('');
  const [notes, setNotes] = useState('');

  const add = () =>
    create.mutate(
      { nodeId, body: { ipAddress: address.trim(), ports: ports.trim(), notes: notes.trim() || null } },
      {
        onSuccess: () => {
          setPorts('');
          setNotes('');
        },
      },
    );

  return (
    <section className="mt-8 space-y-4">
      <div>
        <h2 className="text-lg font-semibold">{t('allocations.title')}</h2>
        <p className="mt-1 text-sm text-muted">{t('allocations.description')}</p>
      </div>
      {allocations.isPending && <Spinner label={t('common.loading')} />}
      {allocations.isError && <Alert>{errorMessage(t, allocations.error)}</Alert>}
      {remove.isError && <Alert>{errorMessage(t, remove.error)}</Alert>}
      {allocations.data?.length === 0 && (
        <Card className="p-6">
          <p className="text-sm text-muted">{t('allocations.empty')}</p>
        </Card>
      )}
      {allocations.data && allocations.data.length > 0 && (
        <Table label={t('allocations.title')}>
          <thead>
            <tr>
              <Th>{t('allocations.address')}</Th>
              <Th>{t('allocations.usedBy')}</Th>
              <Th>{t('allocations.notes')}</Th>
              {canManage && <Th className="text-right">{t('common.actions')}</Th>}
            </tr>
          </thead>
          <tbody>
            {allocations.data.map((allocation) => (
              <tr key={allocation.id}>
                <Td>
                  <code>
                    {allocation.ipAddress}:{allocation.port}
                  </code>
                </Td>
                <Td>
                  {allocation.serverId ? (
                    <Link to={`/servers/${allocation.serverId}`} className="hover:text-accent hover:underline">
                      {allocation.serverName} <span className="text-muted">({allocation.portName})</span>
                    </Link>
                  ) : (
                    // Free, or held by a server the user cannot see (its holder is not theirs to know).
                    <span className="text-muted">{t(allocation.inUse ? 'allocations.inUse' : 'allocations.free')}</span>
                  )}
                </Td>
                <Td>{allocation.notes ?? ''}</Td>
                {canManage && (
                  <Td className="text-right">
                    {!allocation.inUse && (
                      <Button
                        variant="secondary"
                        busy={remove.isPending && remove.variables.allocationId === allocation.id}
                        onClick={() =>
                          window.confirm(
                            t('allocations.deleteConfirm', { address: `${allocation.ipAddress}:${allocation.port}` }),
                          ) && remove.mutate({ nodeId, allocationId: allocation.id })
                        }
                      >
                        {t('actions.delete')}
                      </Button>
                    )}
                  </Td>
                )}
              </tr>
            ))}
          </tbody>
        </Table>
      )}
      {canManage && (
        <Card className="space-y-4 p-6">
          <h3 className="font-medium">{t('allocations.add')}</h3>
          {create.isError && <Alert>{errorMessage(t, create.error)}</Alert>}
          <div className="grid gap-4 sm:grid-cols-3">
            <Field label={t('allocations.addressLabel')} hint={t('allocations.addressHint')}>
              {(ids) => (
                <Input {...ids} value={address} onChange={(e) => setAddress(e.target.value)} autoComplete="off" />
              )}
            </Field>
            <Field label={t('allocations.portsLabel')} hint={t('allocations.portsHint')}>
              {(ids) => (
                <Input
                  {...ids}
                  value={ports}
                  onChange={(e) => setPorts(e.target.value)}
                  placeholder="27015-27030"
                  autoComplete="off"
                />
              )}
            </Field>
            <Field label={t('allocations.notesLabel')}>
              {(ids) => <Input {...ids} value={notes} onChange={(e) => setNotes(e.target.value)} autoComplete="off" />}
            </Field>
          </div>
          <div className="flex justify-end">
            <Button busy={create.isPending} disabled={ports.trim() === ''} onClick={add}>
              {t('allocations.addButton')}
            </Button>
          </div>
        </Card>
      )}
    </section>
  );
}
