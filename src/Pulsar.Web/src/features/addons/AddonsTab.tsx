import { useTranslation } from 'react-i18next';
import { Button, Spinner } from '../../components/ui/Button';
import { Alert, Badge, Card } from '../../components/ui/Display';
import { errorMessage } from '../../lib/errors';
import { useFormatDate } from '../../lib/format';
import { canOnServer, Permission } from '../../lib/permissions';
import { useCurrentUser } from '../auth/api';
import type { ServerDetails } from '../servers/api';
import { serverActions } from '../servers/serverStatus';
import { useAddons, useInstallAddon, useRemoveAddon, type AddonView } from './api';

/**
 * The mods and mod frameworks the server's game supports (docs/architecture/07, "Addons"): the node downloads them from
 * their authors' GitHub releases, checking the checksum GitHub publishes, while the server is stopped.
 */
export function AddonsTab({ details }: { details: ServerDetails }) {
  const { t } = useTranslation();
  const me = useCurrentUser();
  const { server } = details;
  const addons = useAddons(server.id);
  const install = useInstallAddon(server.id);
  const remove = useRemoveAddon(server.id);
  const canChange = canOnServer(me, server, Permission.ServerInstall);
  // The same moment a reinstall is possible: connected, idle and stopped; and installed, as addons go into the game.
  const changeable = serverActions(server).reinstall && server.installState === 'installed';

  if (addons.isPending) {
    return <Spinner label={t('common.loading')} />;
  }
  if (addons.isError) {
    return <Alert>{errorMessage(t, addons.error)}</Alert>;
  }

  const list = addons.data;
  const nameOf = (key: string) => list.find((addon) => addon.key === key)?.name ?? key;
  const error = install.error ?? remove.error;
  const removeAddon = (addon: AddonView) =>
    window.confirm(t('addons.removeConfirm', { name: addon.name, server: server.name })) && remove.mutate(addon.key);

  return (
    <div className="space-y-6">
      <p className="max-w-2xl text-sm text-muted">{t('addons.hint')}</p>
      {canChange && !changeable && <p className="text-sm text-muted">{t('addons.stopToChange')}</p>}
      {error && <Alert>{errorMessage(t, error)}</Alert>}
      <div className="grid gap-4 xl:grid-cols-2">
        {list.map((addon) => (
          <AddonCard
            key={addon.key}
            addon={addon}
            nameOf={nameOf}
            canChange={canChange}
            enabled={changeable && !install.isPending && !remove.isPending}
            installing={install.isPending && install.variables === addon.key}
            removing={remove.isPending && remove.variables === addon.key}
            onInstall={() => install.mutate(addon.key)}
            onRemove={() => removeAddon(addon)}
          />
        ))}
      </div>
    </div>
  );
}

function AddonCard({
  addon,
  nameOf,
  canChange,
  enabled,
  installing,
  removing,
  onInstall,
  onRemove,
}: {
  addon: AddonView;
  nameOf: (key: string) => string;
  canChange: boolean;
  enabled: boolean;
  installing: boolean;
  removing: boolean;
  onInstall: () => void;
  onRemove: () => void;
}) {
  const { t } = useTranslation();
  const formatDate = useFormatDate();
  const installed = addon.installedRelease != null;
  return (
    <Card className="flex flex-col gap-3 p-5">
      <div className="flex flex-wrap items-start justify-between gap-2">
        <div className="min-w-0">
          <h3 className="font-semibold">
            <a href={addon.url} target="_blank" rel="noreferrer noopener" className="hover:text-accent hover:underline">
              {addon.name}
            </a>
          </h3>
          <p className="text-xs text-muted">{t('addons.license', { license: addon.license })}</p>
        </div>
        {installed ? (
          <Badge tone="success">{t('addons.installed', { release: addon.installedRelease })}</Badge>
        ) : (
          <Badge>{t('addons.notInstalled')}</Badge>
        )}
      </div>
      {addon.description && <p className="text-sm">{addon.description}</p>}
      <ul className="space-y-1 text-xs text-muted">
        {addon.requires.length > 0 && <li>{t('addons.requires', { names: addon.requires.map(nameOf).join(', ') })}</li>}
        {addon.conflicts.length > 0 && (
          <li>{t('addons.conflicts', { names: addon.conflicts.map(nameOf).join(', ') })}</li>
        )}
        {addon.reinstallAfterUpdate && <li>{t('addons.reinstallAfterUpdate')}</li>}
        {installed && addon.installedAt && <li>{t('addons.installedAt', { when: formatDate(addon.installedAt) })}</li>}
      </ul>
      {canChange && (
        <div className="mt-auto flex flex-wrap gap-2">
          <Button
            variant={installed ? 'secondary' : 'primary'}
            disabled={!enabled}
            busy={installing}
            onClick={onInstall}
          >
            {installed ? t('addons.update') : t('addons.install')}
          </Button>
          {installed && (
            <Button variant="danger" disabled={!enabled} busy={removing} onClick={onRemove}>
              {t('addons.remove')}
            </Button>
          )}
        </div>
      )}
    </Card>
  );
}
