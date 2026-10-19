import { Plus, Search } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button, Spinner } from '../../components/ui/Button';
import { Alert, Card, PageHeader } from '../../components/ui/Display';
import { errorMessage } from '../../lib/errors';
import { canAnywhere, Permission } from '../../lib/permissions';
import { useCurrentUser } from '../auth/api';
import { useWatchServers } from '../live/hooks';
import { useServers, type ServerSummary } from './api';
import { CreateServerDialog } from './CreateServerDialog';
import { ServerCard } from './ServerCard';

const filters = ['all', 'online', 'offline'] as const;
type Filter = (typeof filters)[number];

const alive = (server: ServerSummary) => server.runtimeState === 'running' || server.runtimeState === 'starting';

export function ServersPage() {
  const { t } = useTranslation();
  const me = useCurrentUser();
  const servers = useServers();
  useWatchServers();
  const [creating, setCreating] = useState(false);
  const [search, setSearch] = useState('');
  const [filter, setFilter] = useState<Filter>('all');
  const canCreate = canAnywhere(me, Permission.ServerCreate);

  const all = servers.data ?? [];
  const words = search.trim().toLocaleLowerCase();
  const shown = all.filter(
    (server) =>
      (filter === 'all' || (filter === 'online') === alive(server)) &&
      (words === '' ||
        [server.name, server.templateName, server.nodeName].some((text) => text.toLocaleLowerCase().includes(words))),
  );
  const counts: Record<Filter, number> = {
    all: all.length,
    online: all.filter(alive).length,
    offline: all.filter((server) => !alive(server)).length,
  };

  return (
    <>
      <PageHeader
        title={t('servers.title')}
        description={t('servers.description')}
        actions={
          canCreate && (
            <Button icon={Plus} onClick={() => setCreating(true)}>
              {t('servers.create')}
            </Button>
          )
        }
      />
      {servers.isPending && <Spinner label={t('common.loading')} />}
      {servers.isError && <Alert>{errorMessage(t, servers.error)}</Alert>}
      {servers.data?.length === 0 && (
        <Card className="p-6">
          <p className="text-sm text-muted">{canCreate ? t('servers.emptyCreate') : t('servers.empty')}</p>
        </Card>
      )}
      {all.length > 0 && (
        <>
          <div className="mb-5 flex flex-wrap items-center gap-3">
            <label className="relative min-w-0 flex-1 sm:max-w-xs">
              <span className="sr-only">{t('servers.search')}</span>
              <Search
                aria-hidden="true"
                className="pointer-events-none absolute top-1/2 left-3 size-4 -translate-y-1/2 text-muted"
              />
              <input
                type="search"
                value={search}
                onChange={(event) => setSearch(event.target.value)}
                placeholder={t('servers.search')}
                className="h-10 w-full rounded-lg border border-border bg-surface pr-3 pl-9 text-sm placeholder:text-muted focus-visible:border-accent focus-visible:outline-none"
              />
            </label>
            <div
              role="group"
              aria-label={t('servers.filters.label')}
              className="flex rounded-lg border border-border bg-surface p-1"
            >
              {filters.map((name) => (
                <button
                  key={name}
                  type="button"
                  aria-pressed={filter === name}
                  onClick={() => setFilter(name)}
                  className={`rounded-md px-3 py-1 text-sm transition-colors focus-visible:outline-2 focus-visible:outline-accent ${
                    filter === name ? 'bg-surface-raised font-medium text-text' : 'text-muted hover:text-text'
                  }`}
                >
                  {t(`servers.filters.${name}`)} <span className="text-muted tabular-nums">{counts[name]}</span>
                </button>
              ))}
            </div>
          </div>
          {shown.length === 0 ? (
            <p className="py-10 text-center text-sm text-muted">{t('servers.noMatch')}</p>
          ) : (
            <ul
              aria-label={t('servers.title')}
              className="grid grid-cols-1 gap-5 sm:grid-cols-2 xl:grid-cols-3 2xl:grid-cols-4"
            >
              {shown.map((server) => (
                <li key={server.id} className="flex min-w-0">
                  <ServerCard server={server} />
                </li>
              ))}
            </ul>
          )}
        </>
      )}
      {creating && <CreateServerDialog onClose={() => setCreating(false)} />}
    </>
  );
}
