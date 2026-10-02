import { useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useNavigate, useParams, useSearchParams } from 'react-router';
import { ApiError } from '../../api/client';
import { Button, Spinner } from '../../components/ui/Button';
import { Alert, Badge, Card, PageHeader, Table, Td, Th } from '../../components/ui/Display';
import { Checkbox } from '../../components/ui/Form';
import { errorMessage } from '../../lib/errors';
import { useFormatDate } from '../../lib/format';
import { can, Permission } from '../../lib/permissions';
import { useCurrentUser } from '../auth/api';
import {
  useDeleteServer,
  useReinstallServer,
  useServer,
  useServerCommand,
  useUpdateGame,
  type OperationView,
  type ServerCommand,
  type ServerDetails,
} from './api';
import { NodeDisconnectedBadge, ServerStateBadge, StateReason } from './components';
import { NetworkTab, OperationProgress, SettingsTab } from './ServerTabs';
import { serverActions } from './serverStatus';

const tabs = ['overview', 'settings', 'network'] as const;
type Tab = (typeof tabs)[number];

/** One server: its state, the lifecycle actions and its recent operations. */
export function ServerPage() {
  const { t } = useTranslation();
  const { id = '' } = useParams();
  const details = useServer(id);
  const [search, setSearch] = useSearchParams();
  const requested = search.get('tab');
  const tab: Tab = tabs.find((name) => name === requested) ?? 'overview';

  if (details.isPending) {
    return <Spinner label={t('common.loading')} />;
  }
  if (details.isError) {
    return details.error instanceof ApiError && details.error.code === 'server.not_found' ? (
      <div className="py-16 text-center">
        <h1 className="text-xl font-semibold">{t('servers.notFound')}</h1>
        <Link to="/servers" className="mt-6 inline-block text-sm text-accent hover:underline">
          {t('servers.backToList')}
        </Link>
      </div>
    ) : (
      <Alert>{errorMessage(t, details.error)}</Alert>
    );
  }

  const { server } = details.data;
  return (
    <>
      <p className="mb-2 text-sm">
        <Link to="/servers" className="text-muted hover:text-text">
          ← {t('servers.title')}
        </Link>
      </p>
      <PageHeader
        title={server.name}
        description={details.data.description ?? undefined}
        actions={
          <>
            {!server.nodeConnected && <NodeDisconnectedBadge />}
            <ServerStateBadge server={server} />
          </>
        }
      />
      <LifecycleActions details={details.data} />
      {server.activeOperation && (
        <div className="mt-4">
          <OperationProgress serverId={server.id} operation={server.activeOperation} />
        </div>
      )}
      <div role="tablist" aria-label={t('servers.tabs.label')} className="mt-6 flex gap-1 border-b border-border">
        {tabs.map((name) => (
          <button
            key={name}
            type="button"
            role="tab"
            aria-selected={tab === name}
            onClick={() => setSearch(name === 'overview' ? {} : { tab: name }, { replace: true })}
            className={`-mb-px border-b-2 px-4 py-2 text-sm ${tab === name ? 'border-accent text-accent' : 'border-transparent text-muted hover:text-text'}`}
          >
            {t(`servers.tabs.${name}`)}
          </button>
        ))}
      </div>
      <div role="tabpanel" className="mt-6">
        {tab === 'overview' && (
          <>
            <div className="grid gap-6 xl:grid-cols-2">
              <OverviewCard details={details.data} />
              <OperationsCard operations={details.data.recentOperations} />
            </div>
            <AdministrationSection details={details.data} />
          </>
        )}
        {tab === 'settings' && <SettingsTab details={details.data} />}
        {tab === 'network' && <NetworkTab details={details.data} />}
      </div>
    </>
  );
}

function LifecycleActions({ details }: { details: ServerDetails }) {
  const { t } = useTranslation();
  const me = useCurrentUser();
  const command = useServerCommand();
  const { server } = details;
  const available = serverActions(server);
  const run = (name: ServerCommand) => command.mutate({ id: server.id, command: name });
  const buttons: [ServerCommand, string, boolean][] = [
    ['start', Permission.ServerStart, available.start],
    ['stop', Permission.ServerStop, available.stop],
    ['restart', Permission.ServerRestart, available.restart],
    ['kill', Permission.ServerKill, available.kill],
  ];
  const visible = buttons.filter(([, permission]) => can(me, permission));
  if (visible.length === 0) {
    return null;
  }

  const refused = command.data && command.data.status !== 'accepted' && command.data.status !== 'alreadyInState';
  return (
    <section aria-label={t('servers.actions')} className="space-y-2">
      <div className="flex flex-wrap gap-2">
        {visible.map(([name, , enabled]) => (
          <Button
            key={name}
            variant={name === 'kill' ? 'danger' : name === 'start' ? 'primary' : 'secondary'}
            disabled={!enabled || command.isPending}
            busy={command.isPending && command.variables.command === name}
            onClick={() =>
              (name !== 'kill' || window.confirm(t('servers.killConfirm', { name: server.name }))) && run(name)
            }
          >
            {t(`servers.commands.${name}`)}
          </Button>
        ))}
      </div>
      {command.isError && <Alert>{errorMessage(t, command.error)}</Alert>}
      {refused && (
        <p role="status" className="text-sm text-danger">
          {t('servers.commandRefused', { status: command.data.status, code: command.data.errorCode ?? '' })}
        </p>
      )}
    </section>
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

function OverviewCard({ details }: { details: ServerDetails }) {
  const { t } = useTranslation();
  const formatDate = useFormatDate();
  const { server } = details;
  const when = (iso: string | null) =>
    iso ? formatDate(iso) : <span className="text-muted">{t('common.never')}</span>;
  return (
    <Card className="p-6">
      <h2 className="mb-4 text-lg font-semibold">{t('servers.overview')}</h2>
      <Facts
        items={[
          [t('servers.game'), `${server.templateName} (${details.templateVersion})`],
          ...details.ports
            .filter((port) => port.port !== null)
            .map((port): [string, ReactNode] => [
              port.label,
              <code key={port.name}>
                {port.ipAddress}:{port.port}
              </code>,
            ]),
          [
            t('servers.node'),
            <Link key="node" to={`/nodes/${server.nodeId}`} className="hover:text-accent hover:underline">
              {server.nodeName}
            </Link>,
          ],
          [
            t('servers.status'),
            <div key="status">
              <ServerStateBadge server={server} />
              <StateReason server={server} />
            </div>,
          ],
          [t('servers.desired'), t(`servers.desiredStates.${server.desiredState}`)],
          [t('servers.lastExitCode'), server.lastExitCode ?? '—'],
          [t('servers.lastStarted'), when(server.lastStartedAt)],
          [t('servers.lastStopped'), when(server.lastStoppedAt)],
          [t('servers.restartOnCrash'), details.restartOnCrash ? t('servers.yes') : t('servers.no')],
          [t('servers.shortId'), <code key="short">{server.shortId}</code>],
        ]}
      />
    </Card>
  );
}

const statusTones = {
  queued: 'neutral',
  running: 'accent',
  succeeded: 'success',
  failed: 'danger',
  cancelled: 'neutral',
  interrupted: 'danger',
} as const;

function OperationsCard({ operations }: { operations: OperationView[] }) {
  const { t } = useTranslation();
  const formatDate = useFormatDate();
  return (
    <Card className="p-6">
      <h2 className="mb-4 text-lg font-semibold">{t('servers.operations.title')}</h2>
      {operations.length === 0 ? (
        <p className="text-sm text-muted">{t('servers.operations.empty')}</p>
      ) : (
        <Table label={t('servers.operations.title')}>
          <thead>
            <tr>
              <Th>{t('servers.operations.type')}</Th>
              <Th>{t('servers.operations.status')}</Th>
              <Th>{t('servers.operations.when')}</Th>
            </tr>
          </thead>
          <tbody>
            {operations.map((operation) => (
              <tr key={operation.id}>
                <Td>{t(`servers.operations.types.${operation.type}`)}</Td>
                <Td>
                  <Badge tone={statusTones[operation.status]}>
                    {t(`servers.operations.statuses.${operation.status}`)}
                  </Badge>
                  {operation.errorCode && (
                    <p className="mt-1 text-xs text-danger">
                      {operation.errorMessage ?? operation.errorCode} <code>({operation.errorCode})</code>
                    </p>
                  )}
                </Td>
                <Td>{formatDate(operation.completedAt ?? operation.createdAt)}</Td>
              </tr>
            ))}
          </tbody>
        </Table>
      )}
    </Card>
  );
}

function AdministrationSection({ details }: { details: ServerDetails }) {
  const { t } = useTranslation();
  const me = useCurrentUser();
  const navigate = useNavigate();
  const reinstall = useReinstallServer();
  const remove = useDeleteServer();
  const update = useUpdateGame();
  const [validate, setValidate] = useState(false);
  const { server } = details;
  const available = serverActions(server);
  const canInstall = can(me, Permission.ServerInstall);
  const canDelete = can(me, Permission.ServerDelete);
  if (!canInstall && !canDelete) {
    return null;
  }

  const error = reinstall.error ?? remove.error ?? update.error;
  return (
    <section className="mt-8 space-y-4">
      <h2 className="text-lg font-semibold">{t('servers.administration')}</h2>
      {error && <Alert>{errorMessage(t, error)}</Alert>}
      <div className="flex flex-wrap gap-2">
        {canInstall && details.canUpdate && (
          <Button
            variant="secondary"
            disabled={!available.reinstall || server.installState !== 'installed'}
            busy={update.isPending}
            onClick={() => update.mutate({ id: server.id, validate })}
          >
            {t('servers.updateGame')}
          </Button>
        )}
        {canInstall && (
          <Button
            variant="secondary"
            disabled={!available.reinstall}
            busy={reinstall.isPending}
            onClick={() =>
              window.confirm(t('servers.reinstallConfirm', { name: server.name })) && reinstall.mutate(server.id)
            }
          >
            {t('servers.reinstall')}
          </Button>
        )}
        {canDelete && (
          <Button
            variant="danger"
            disabled={!available.delete}
            busy={remove.isPending}
            onClick={() =>
              window.confirm(t('servers.deleteConfirm', { name: server.name })) &&
              remove.mutate(server.id, { onSuccess: () => void navigate('/servers') })
            }
          >
            {t('actions.delete')}
          </Button>
        )}
      </div>
      {canInstall && details.canUpdate && (
        <Checkbox
          label={t('servers.validateFiles')}
          checked={validate}
          onChange={(e) => setValidate(e.target.checked)}
        />
      )}
      <p className="text-xs text-muted">{t('servers.administrationHint')}</p>
    </section>
  );
}
