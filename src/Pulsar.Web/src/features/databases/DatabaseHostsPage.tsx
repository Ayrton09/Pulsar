import { useMutation } from '@tanstack/react-query';
import { useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { api, unwrap } from '../../api/client';
import { Button, Spinner } from '../../components/ui/Button';
import { Dialog } from '../../components/ui/Dialog';
import { Alert, PageHeader, Table, Td, Th } from '../../components/ui/Display';
import { Checkbox, Field, Input } from '../../components/ui/Form';
import { errorMessage } from '../../lib/errors';
import { useNodes } from '../nodes/api';
import {
  isLocalAddress,
  useDatabaseHosts,
  useDatabaseHostsChanged,
  type DatabaseHost,
  type DatabaseHostRequest,
} from './api';

/** MySQL/MariaDB servers game databases are created on (settings.manage); servers create theirs in their Databases tab. */
export function DatabaseHostsPage() {
  const { t } = useTranslation();
  const hosts = useDatabaseHosts();
  const [editing, setEditing] = useState<DatabaseHost | 'new' | null>(null);
  const test = useMutation({
    mutationFn: (hostId: string) =>
      unwrap(api.POST('/api/v1/database-hosts/{hostId}/test', { params: { path: { hostId } } })),
  });

  return (
    <>
      <PageHeader
        title={t('databaseHosts.title')}
        description={t('databaseHosts.description')}
        actions={<Button onClick={() => setEditing('new')}>{t('databaseHosts.add')}</Button>}
      />
      {hosts.isPending && <Spinner label={t('common.loading')} />}
      {hosts.isError && <Alert>{errorMessage(t, hosts.error)}</Alert>}
      {test.isError && <Alert>{errorMessage(t, test.error)}</Alert>}
      {test.isSuccess && (
        <p role="status" className="mb-4 text-sm text-success">
          {t('databaseHosts.tested', { version: test.data.version })}
        </p>
      )}
      {hosts.data && hosts.data.hosts.length === 0 && <p className="text-sm text-muted">{t('databaseHosts.empty')}</p>}
      {hosts.data && hosts.data.hosts.length > 0 && (
        <Table label={t('databaseHosts.title')}>
          <thead>
            <tr>
              <Th>{t('databaseHosts.name')}</Th>
              <Th>{t('databaseHosts.where')}</Th>
              <Th>{t('databaseHosts.use')}</Th>
              <Th>
                <span className="sr-only">{t('common.actions')}</span>
              </Th>
            </tr>
          </thead>
          <tbody>
            {hosts.data.hosts.map((host) => (
              <tr key={host.id}>
                <Td className="font-medium">{host.name}</Td>
                <Td>
                  <p className="font-mono">
                    {host.userName}@{host.host}:{host.port}
                  </p>
                  {host.serverAddress && (
                    <p className="text-xs text-muted">
                      {t('databaseHosts.forServers', {
                        address: `${host.serverAddress}:${host.serverPort ?? host.port}`,
                      })}
                    </p>
                  )}
                  {host.requireTls && <p className="text-xs text-muted">{t('databaseHosts.tlsRequired')}</p>}
                </Td>
                <Td>
                  <p>{t('databaseHosts.databases', { count: host.databases })}</p>
                  <p className="text-xs text-muted">
                    {host.nodeIds.length === 0
                      ? t('databaseHosts.allNodes')
                      : t('databaseHosts.someNodes', { count: host.nodeIds.length })}
                  </p>
                </Td>
                <Td className="text-right whitespace-nowrap">
                  <Button variant="ghost" size="sm" disabled={test.isPending} onClick={() => test.mutate(host.id)}>
                    {t('databaseHosts.test')}
                  </Button>
                  <Button variant="ghost" size="sm" onClick={() => setEditing(host)}>
                    {t('actions.edit')}
                  </Button>
                </Td>
              </tr>
            ))}
          </tbody>
        </Table>
      )}
      {editing && <HostDialog host={editing === 'new' ? null : editing} onClose={() => setEditing(null)} />}
    </>
  );
}

function HostDialog({ host, onClose }: { host: DatabaseHost | null; onClose: () => void }) {
  const { t } = useTranslation();
  const changed = useDatabaseHostsChanged();
  const nodes = useNodes();
  const save = useMutation({
    mutationFn: (body: DatabaseHostRequest) =>
      host
        ? unwrap(api.PUT('/api/v1/database-hosts/{hostId}', { params: { path: { hostId: host.id } }, body }))
        : unwrap(api.POST('/api/v1/database-hosts', { body })),
    onSuccess: async () => {
      await changed();
      onClose();
    },
  });
  const remove = useMutation({
    mutationFn: (hostId: string) =>
      unwrap(api.DELETE('/api/v1/database-hosts/{hostId}', { params: { path: { hostId } } })),
    onSuccess: async () => {
      await changed();
      onClose();
    },
  });
  const [name, setName] = useState(host?.name ?? '');
  const [address, setAddress] = useState(host?.host ?? '');
  const [port, setPort] = useState(String(host?.port ?? 3306));
  const [userName, setUserName] = useState(host?.userName ?? '');
  const [password, setPassword] = useState('');
  const [serverAddress, setServerAddress] = useState(host?.serverAddress ?? '');
  const [serverPort, setServerPort] = useState(host?.serverPort?.toString() ?? '');
  const [userHost, setUserHost] = useState(host?.userHost ?? '%');
  const [nodeIds, setNodeIds] = useState<string[]>(host?.nodeIds ?? []);
  // Untouched on a new host: the panel's default, shown as the address is typed.
  const [requireTls, setRequireTls] = useState<boolean | null>(host ? host.requireTls : null);

  const submit = (event: FormEvent) => {
    event.preventDefault();
    save.mutate({
      name: name.trim(),
      host: address.trim(),
      port: Number(port),
      userName: userName.trim(),
      password: password || null,
      serverAddress: serverAddress.trim() || null,
      serverPort: serverPort.trim() === '' ? null : Number(serverPort),
      userHost: userHost.trim() || null,
      nodeIds,
      requireTls,
    });
  };

  return (
    <Dialog
      open
      onOpenChange={(open) => !open && onClose()}
      title={host ? t('databaseHosts.edit') : t('databaseHosts.add')}
      size="lg"
    >
      <form className="space-y-4" onSubmit={submit} noValidate>
        <Field label={t('databaseHosts.name')}>
          {(control) => <Input {...control} value={name} maxLength={64} onChange={(e) => setName(e.target.value)} />}
        </Field>
        <div className="grid gap-4 sm:grid-cols-[1fr_8rem]">
          <Field label={t('databaseHosts.address')} hint={t('databaseHosts.addressHint')}>
            {(control) => (
              <Input {...control} placeholder="10.0.0.5" value={address} onChange={(e) => setAddress(e.target.value)} />
            )}
          </Field>
          <Field label={t('databaseHosts.port')}>
            {(control) => (
              <Input
                {...control}
                type="number"
                min={1}
                max={65535}
                value={port}
                onChange={(e) => setPort(e.target.value)}
              />
            )}
          </Field>
        </div>
        <div className="grid gap-4 sm:grid-cols-2">
          <Field label={t('databaseHosts.userName')} hint={t('databaseHosts.userNameHint')}>
            {(control) => (
              <Input {...control} autoComplete="off" value={userName} onChange={(e) => setUserName(e.target.value)} />
            )}
          </Field>
          <Field label={t('databaseHosts.password')} hint={host ? t('databaseHosts.passwordKeep') : undefined}>
            {(control) => (
              <Input
                {...control}
                type="password"
                autoComplete="new-password"
                value={password}
                onChange={(e) => setPassword(e.target.value)}
              />
            )}
          </Field>
        </div>
        <div className="grid gap-4 sm:grid-cols-[1fr_8rem]">
          <Field label={t('databaseHosts.serverAddress')} hint={t('databaseHosts.serverAddressHint')}>
            {(control) => (
              <Input
                {...control}
                placeholder="127.0.0.1"
                value={serverAddress}
                onChange={(e) => setServerAddress(e.target.value)}
              />
            )}
          </Field>
          <Field label={t('databaseHosts.serverPort')}>
            {(control) => (
              <Input
                {...control}
                type="number"
                min={1}
                max={65535}
                value={serverPort}
                onChange={(e) => setServerPort(e.target.value)}
              />
            )}
          </Field>
        </div>
        <Field label={t('databaseHosts.userHost')} hint={t('databaseHosts.userHostHint')}>
          {(control) => <Input {...control} value={userHost} onChange={(e) => setUserHost(e.target.value)} />}
        </Field>
        <div className="space-y-1">
          <Checkbox
            label={t('databaseHosts.requireTls')}
            aria-describedby="database-host-tls-hint"
            checked={requireTls ?? (address.trim() !== '' && !isLocalAddress(address))}
            onChange={(e) => setRequireTls(e.target.checked)}
          />
          <p id="database-host-tls-hint" className="text-xs text-muted">
            {t('databaseHosts.requireTlsHint')}
          </p>
        </div>
        <fieldset className="space-y-2">
          <legend className="text-sm font-medium">{t('databaseHosts.nodes')}</legend>
          <p className="text-xs text-muted">{t('databaseHosts.nodesHint')}</p>
          {nodes.data?.map((node) => (
            <Checkbox
              key={node.id}
              label={node.name}
              checked={nodeIds.includes(node.id)}
              onChange={(e) =>
                setNodeIds((current) =>
                  e.target.checked ? [...current, node.id] : current.filter((id) => id !== node.id),
                )
              }
            />
          ))}
          {/* Listing nodes takes more than settings.manage: without the list, say what the host applies to. */}
          {nodes.isError && (
            <>
              <Alert>{t('databaseHosts.nodesUnavailable', { error: errorMessage(t, nodes.error) })}</Alert>
              <p className="text-xs text-muted">
                {nodeIds.length === 0
                  ? t('databaseHosts.nodesAll')
                  : t('databaseHosts.nodesKept', { count: nodeIds.length })}
              </p>
            </>
          )}
        </fieldset>
        {(save.error ?? remove.error) && <Alert>{errorMessage(t, save.error ?? remove.error)}</Alert>}
        <div className="flex items-center justify-between gap-2">
          {host ? (
            <Button
              type="button"
              variant="danger"
              disabled={remove.isPending}
              onClick={() =>
                window.confirm(t('databaseHosts.deleteConfirm', { name: host.name })) && remove.mutate(host.id)
              }
            >
              {t('actions.delete')}
            </Button>
          ) : (
            <span />
          )}
          <div className="flex gap-2">
            <Button type="button" variant="ghost" onClick={onClose}>
              {t('actions.cancel')}
            </Button>
            <Button type="submit" busy={save.isPending}>
              {t('actions.save')}
            </Button>
          </div>
        </div>
      </form>
    </Dialog>
  );
}
