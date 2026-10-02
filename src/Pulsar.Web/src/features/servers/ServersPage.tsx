import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import { Button, Spinner } from '../../components/ui/Button';
import { Alert, Card, PageHeader, Table, Td, Th } from '../../components/ui/Display';
import { errorMessage } from '../../lib/errors';
import { useFormatDate } from '../../lib/format';
import { can, Permission } from '../../lib/permissions';
import { useCurrentUser } from '../auth/api';
import { useWatchServers } from '../live/hooks';
import { useServers } from './api';
import { NodeDisconnectedBadge, ServerStateBadge, StateReason } from './components';
import { CreateServerDialog } from './CreateServerDialog';

export function ServersPage() {
  const { t } = useTranslation();
  const me = useCurrentUser();
  const servers = useServers();
  useWatchServers();
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
