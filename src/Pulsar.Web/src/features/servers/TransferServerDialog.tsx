import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button } from '../../components/ui/Button';
import { Dialog } from '../../components/ui/Dialog';
import { Alert } from '../../components/ui/Display';
import { Field, Select } from '../../components/ui/Form';
import { errorMessage } from '../../lib/errors';
import { canOnNode, Permission } from '../../lib/permissions';
import { useCurrentUser } from '../auth/api';
import { useWatchNodes } from '../live/hooks';
import { useNodes } from '../nodes/api';
import { useTransferServer, type ServerDetails } from './api';

/**
 * Moving a stopped server to another node (Pulsar 1.0.16): the node it goes to, among those the user may create
 * servers on. It stays the same server (its link, access and schedules); its ports change, and its old copy, with the
 * backups kept only on its old node, is deleted once it moved. A move that fails leaves it where it was.
 */
export function TransferServerDialog({ details, onClose }: { details: ServerDetails; onClose: () => void }) {
  const { t } = useTranslation();
  const transfer = useTransferServer();
  const nodes = useNodes();
  useWatchNodes();
  const me = useCurrentUser();
  const [nodeId, setNodeId] = useState('');
  const [showErrors, setShowErrors] = useState(false);
  const others =
    nodes.data?.filter(
      (node) => node.connected && node.id !== details.server.nodeId && canOnNode(me, node.id, Permission.ServerCreate),
    ) ?? [];
  const nodeError = others.some((node) => node.id === nodeId) ? null : 'validation.required';

  const submit = () => {
    if (nodeError) {
      setShowErrors(true);
      return;
    }
    transfer.mutate({ id: details.server.id, nodeId }, { onSuccess: onClose });
  };

  return (
    <Dialog open onOpenChange={(open) => !open && onClose()} title={t('servers.transfer.title')} size="lg">
      <div className="space-y-4">
        <p className="text-sm text-muted">{t('servers.transfer.hint', { name: details.server.name })}</p>
        {transfer.isError && <Alert>{errorMessage(t, transfer.error)}</Alert>}
        {nodes.isError && <Alert>{errorMessage(t, nodes.error)}</Alert>}
        {nodes.data && others.length === 0 && <p className="text-sm text-muted">{t('servers.transfer.noNodes')}</p>}
        <Field label={t('servers.node')} error={showErrors && nodeError ? t(nodeError) : undefined}>
          {(ids) => (
            <Select {...ids} value={nodeId} onChange={(e) => setNodeId(e.target.value)}>
              <option value="">{t('servers.chooseNode')}</option>
              {others.map((node) => (
                <option key={node.id} value={node.id}>
                  {node.name}
                </option>
              ))}
            </Select>
          )}
        </Field>
        <div className="flex justify-end gap-2">
          <Button variant="secondary" onClick={onClose}>
            {t('actions.cancel')}
          </Button>
          <Button busy={transfer.isPending} onClick={submit}>
            {t('servers.transfer.action')}
          </Button>
        </div>
      </div>
    </Dialog>
  );
}
