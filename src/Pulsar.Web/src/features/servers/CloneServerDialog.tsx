import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router';
import { Button } from '../../components/ui/Button';
import { Dialog } from '../../components/ui/Dialog';
import { Alert } from '../../components/ui/Display';
import { Field, Input, Select } from '../../components/ui/Form';
import { errorMessage } from '../../lib/errors';
import { canOnNode, Permission } from '../../lib/permissions';
import { useCurrentUser } from '../auth/api';
import { useWatchNodes } from '../live/hooks';
import { useNodes } from '../nodes/api';
import { useCloneServer, type ServerDetails } from './api';

/**
 * Cloning a server (Pulsar 1.0.16): its name and the node it goes to (its own or another the user may create servers
 * on). The clone installs as a new server does, then gets a copy of this server's files; this one is left as it is.
 */
export function CloneServerDialog({ details, onClose }: { details: ServerDetails; onClose: () => void }) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const clone = useCloneServer();
  const nodes = useNodes();
  useWatchNodes();
  const me = useCurrentUser();
  const [name, setName] = useState(t('servers.clone.defaultName', { name: details.server.name }).slice(0, 64));
  const [nodeId, setNodeId] = useState(details.server.nodeId);
  const [showErrors, setShowErrors] = useState(false);
  const connected =
    nodes.data?.filter((node) => node.connected && canOnNode(me, node.id, Permission.ServerCreate)) ?? [];
  const nameError = name.trim() === '' ? 'validation.required' : name.trim().length > 64 ? 'validation.tooLong' : null;
  const nodeError = connected.some((node) => node.id === nodeId) ? null : 'validation.required';

  const submit = () => {
    if (nameError || nodeError) {
      setShowErrors(true);
      return;
    }
    clone.mutate(
      { id: details.server.id, nodeId, name: name.trim() },
      // To the clone's console, which shows its install, then the copy.
      { onSuccess: (created) => void navigate(`/servers/${created.server.id}?tab=console`) },
    );
  };

  return (
    <Dialog open onOpenChange={(open) => !open && onClose()} title={t('servers.clone.title')} size="lg">
      <div className="space-y-4">
        <p className="text-sm text-muted">{t('servers.clone.hint')}</p>
        {clone.isError && <Alert>{errorMessage(t, clone.error)}</Alert>}
        {nodes.isError && <Alert>{errorMessage(t, nodes.error)}</Alert>}
        <Field label={t('servers.name')} error={showErrors && nameError ? t(nameError) : undefined}>
          {(ids) => <Input {...ids} value={name} onChange={(e) => setName(e.target.value)} autoComplete="off" />}
        </Field>
        <Field
          label={t('servers.node')}
          hint={t('servers.nodeHint')}
          error={showErrors && nodeError ? t(nodeError) : undefined}
        >
          {(ids) => (
            <Select {...ids} value={nodeId} onChange={(e) => setNodeId(e.target.value)}>
              <option value="">{t('servers.chooseNode')}</option>
              {connected.map((node) => (
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
          <Button busy={clone.isPending} onClick={submit}>
            {t('servers.clone.action')}
          </Button>
        </div>
      </div>
    </Dialog>
  );
}
