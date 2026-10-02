import { zodResolver } from '@hookform/resolvers/zod';
import { useState } from 'react';
import { useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { Link, useNavigate } from 'react-router';
import { z } from 'zod';
import { Button, Spinner } from '../../components/ui/Button';
import { Dialog } from '../../components/ui/Dialog';
import { Alert, Card, PageHeader, Table, Td, Th } from '../../components/ui/Display';
import { Field, Input, Select } from '../../components/ui/Form';
import { errorMessage, fieldErrors } from '../../lib/errors';
import { useFormatDate } from '../../lib/format';
import { can, Permission } from '../../lib/permissions';
import { useCurrentUser } from '../auth/api';
import { useNodes } from '../nodes/api';
import { useCreateServer, useServers, useTemplates } from './api';
import { NodeDisconnectedBadge, ServerStateBadge, StateReason } from './components';

export function ServersPage() {
  const { t } = useTranslation();
  const me = useCurrentUser();
  const servers = useServers();
  const formatDate = useFormatDate();
  const [creating, setCreating] = useState(false);
  const canCreate = can(me, Permission.ServerCreate);

  return (
    <>
      <PageHeader
        title={t('servers.title')}
        description={t('servers.description')}
        actions={canCreate && <Button onClick={() => setCreating(true)}>{t('servers.create')}</Button>}
      />
      {servers.isPending && <Spinner label={t('common.loading')} />}
      {servers.isError && <Alert>{errorMessage(t, servers.error)}</Alert>}
      {servers.data?.length === 0 && (
        <Card className="p-6">
          <p className="text-sm text-muted">{canCreate ? t('servers.emptyCreate') : t('servers.empty')}</p>
        </Card>
      )}
      {servers.data && servers.data.length > 0 && (
        <Table label={t('servers.title')}>
          <thead>
            <tr>
              <Th>{t('servers.server')}</Th>
              <Th>{t('servers.status')}</Th>
              <Th>{t('servers.node')}</Th>
              <Th>{t('servers.lastStarted')}</Th>
            </tr>
          </thead>
          <tbody>
            {servers.data.map((server) => (
              <tr key={server.id}>
                <Td>
                  <Link to={`/servers/${server.id}`} className="font-medium hover:text-accent hover:underline">
                    {server.name}
                  </Link>
                  <p className="text-xs text-muted">{server.templateName}</p>
                </Td>
                <Td>
                  <div className="flex flex-wrap gap-1">
                    <ServerStateBadge server={server} />
                    {!server.nodeConnected && <NodeDisconnectedBadge />}
                  </div>
                  <StateReason server={server} />
                </Td>
                <Td>{server.nodeName}</Td>
                <Td>
                  {server.lastStartedAt ? (
                    formatDate(server.lastStartedAt)
                  ) : (
                    <span className="text-muted">{t('common.never')}</span>
                  )}
                </Td>
              </tr>
            ))}
          </tbody>
        </Table>
      )}
      {creating && <CreateServerDialog onClose={() => setCreating(false)} />}
    </>
  );
}

/** Limits as in Pulsar.Domain.Servers.Server. */
const serverSchema = z.object({
  name: z.string().trim().min(1, 'validation.required').max(64, 'validation.tooLong'),
  description: z.string().trim().max(512, 'validation.tooLong'),
  nodeId: z.string().min(1, 'validation.required'),
  templateId: z.string().min(1, 'validation.required'),
});

type ServerFormValues = z.infer<typeof serverSchema>;

/** Name, node and game. The panel then provisions and installs it on the node; the server page shows the progress. */
function CreateServerDialog({ onClose }: { onClose: () => void }) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const create = useCreateServer();
  const nodes = useNodes();
  const templates = useTemplates();
  const connected = nodes.data?.filter((node) => node.connected) ?? [];
  const { register, handleSubmit, formState } = useForm<ServerFormValues>({
    resolver: zodResolver(serverSchema),
    defaultValues: { name: '', description: '', nodeId: '', templateId: '' },
  });
  const server = fieldErrors(create.error);
  const message = (key: keyof ServerFormValues) => {
    const error = formState.errors[key]?.message;
    return error ? t(error) : server[key];
  };

  const onSubmit = handleSubmit(({ name, description, nodeId, templateId }) =>
    create.mutate(
      { name, description: description || null, nodeId, templateId },
      { onSuccess: (created) => void navigate(`/servers/${created.server.id}`) },
    ),
  );

  return (
    <Dialog open onOpenChange={(open) => !open && onClose()} title={t('servers.create')}>
      <form onSubmit={(e) => void onSubmit(e)} className="space-y-4" noValidate>
        {create.isError && <Alert>{errorMessage(t, create.error)}</Alert>}
        {(nodes.isError || templates.isError) && <Alert>{errorMessage(t, nodes.error ?? templates.error)}</Alert>}
        <Field label={t('servers.name')} error={message('name')}>
          {(ids) => <Input {...ids} {...register('name')} autoComplete="off" />}
        </Field>
        <Field label={t('servers.node')} hint={t('servers.nodeHint')} error={message('nodeId')}>
          {(ids) => (
            <Select {...ids} {...register('nodeId')}>
              <option value="">{t('servers.chooseNode')}</option>
              {connected.map((node) => (
                <option key={node.id} value={node.id}>
                  {node.name}
                </option>
              ))}
            </Select>
          )}
        </Field>
        <Field label={t('servers.game')} error={message('templateId')}>
          {(ids) => (
            <Select {...ids} {...register('templateId')}>
              <option value="">{t('servers.chooseGame')}</option>
              {templates.data?.map((template) => (
                <option key={template.id} value={template.id}>
                  {template.name} {template.version}
                </option>
              ))}
            </Select>
          )}
        </Field>
        <Field label={t('servers.descriptionLabel')} error={message('description')}>
          {(ids) => <Input {...ids} {...register('description')} autoComplete="off" />}
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
