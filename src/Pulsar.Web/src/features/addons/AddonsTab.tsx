import { useId } from 'react';
import { useTranslation } from 'react-i18next';
import { ApiError } from '../../api/client';
import { Button, Spinner } from '../../components/ui/Button';
import { Alert, Badge, Card } from '../../components/ui/Display';
import { RefreshFailed } from '../../components/ui/RefreshFailed';
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
  // A failed refetch (after a reconnect, say) keeps the tab with the last data, and what is open on it.
  if (!addons.data) {
    return <Alert>{errorMessage(t, addons.error)}</Alert>;
  }

  const list = addons.data;
  const nameOf = (key: string) => list.find((addon) => addon.key === key)?.name ?? key;
  const error = install.error ?? remove.error;
  const installed = new Set(list.filter((addon) => addon.installedRelease != null).map((addon) => addon.key));
  // The panel's refusals name the addons in English: say it again with the names, in the user's language.
  const refusal = (): string | null => {
    const key = install.error ? install.variables : remove.variables;
    const addon = list.find((a) => a.key === key);
    if (!(error instanceof ApiError) || !addon) {
      return null;
    }
    const others =
      error.code === 'addon.requires'
        ? addon.requires.filter((k) => !installed.has(k))
        : error.code === 'addon.conflicts'
          ? [...addon.conflicts, ...list.filter((a) => a.conflicts.includes(addon.key)).map((a) => a.key)].filter((k) =>
              installed.has(k),
            )
          : error.code === 'addon.required_by'
            ? list.filter((a) => installed.has(a.key) && a.requires.includes(addon.key)).map((a) => a.key)
            : [];
    return others.length === 0
      ? null
      : t(`addons.refused.${error.code.slice('addon.'.length)}`, {
          name: addon.name,
          others: [...new Set(others)].map(nameOf).join(', '),
        });
  };
  // Each change clears the other's error: the alert is about the last one.
  const installAddon = (addon: AddonView) => {
    remove.reset();
    install.mutate(addon.key);
  };
  const removeAddon = (addon: AddonView) => {
    if (window.confirm(t('addons.removeConfirm', { name: addon.name, server: server.name }))) {
      install.reset();
      remove.mutate(addon.key);
    }
  };

  return (
    <div className="space-y-6">
      {addons.isError && <RefreshFailed error={addons.error} retry={() => void addons.refetch()} />}
      {/* Where they come from: GitHub with the checksum it publishes, Thunderstore without one. */}
      {list.some((addon) => addon.source !== 'thunderstore') && (
        <p className="max-w-2xl text-sm text-muted">{t('addons.hint')}</p>
      )}
      {list.some((addon) => addon.source === 'thunderstore') && (
        <p className="max-w-2xl text-sm text-muted">{t('addons.hintThunderstore')}</p>
      )}
      {canChange && !changeable && <p className="text-sm text-muted">{t('addons.stopToChange')}</p>}
      {error && <Alert>{refusal() ?? errorMessage(t, error)}</Alert>}
      <div className="grid grid-cols-1 gap-4 xl:grid-cols-2">
        {list.map((addon) => (
          <AddonCard
            key={addon.key}
            addon={addon}
            nameOf={nameOf}
            canChange={canChange}
            enabled={changeable && !install.isPending && !remove.isPending}
            installing={install.isPending && install.variables === addon.key}
            removing={remove.isPending && remove.variables === addon.key}
            onInstall={() => installAddon(addon)}
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
  const noteId = useId();
  const installed = addon.installedRelease != null;
  return (
    <Card className="flex flex-col gap-3 p-5">
      <div className="flex flex-wrap items-start justify-between gap-2">
        <div className="min-w-0">
          <h3 className="font-semibold">
            <a
              href={addon.url}
              target="_blank"
              rel="noreferrer noopener"
              aria-describedby={noteId}
              className="hover:text-accent hover:underline"
            >
              {addon.name}
            </a>
          </h3>
          <span id={noteId} className="sr-only">
            {t('common.opensInNewTab')}
          </span>
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
