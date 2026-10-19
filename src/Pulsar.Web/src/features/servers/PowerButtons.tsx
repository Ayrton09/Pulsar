import { OctagonX, Play, RotateCw, Square, type LucideIcon } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Button } from '../../components/ui/Button';
import { Alert } from '../../components/ui/Display';
import { errorMessage } from '../../lib/errors';
import { canOnServer, Permission } from '../../lib/permissions';
import { useCurrentUser } from '../auth/api';
import { useServerCommand, type ServerCommand, type ServerSummary } from './api';
import { commandRefusal, serverActions } from './serverStatus';

type Variant = 'success' | 'secondary' | 'danger' | 'dangerSubtle';

/**
 * Start, restart, stop and (on the server's page) kill, each only with its permission. The full form keeps every button
 * in place and greys out the ones that do not apply now; the compact form, on server cards, shows only the ones that
 * apply, as icons with their name as label.
 */
export function PowerButtons({ server, compact = false }: { server: ServerSummary; compact?: boolean }) {
  const { t } = useTranslation();
  const me = useCurrentUser();
  const command = useServerCommand();
  const available = serverActions(server);
  const buttons: [ServerCommand, string, boolean, LucideIcon, Variant][] = [
    ['start', Permission.ServerStart, available.start, Play, 'success'],
    ['restart', Permission.ServerRestart, available.restart, RotateCw, 'secondary'],
    ['stop', Permission.ServerStop, available.stop, Square, 'danger'],
    // Killing loses unsaved progress: it looks dangerous, without outshouting the everyday actions.
    ['kill', Permission.ServerKill, available.kill, OctagonX, 'dangerSubtle'],
  ];
  const visible = buttons.filter(
    ([name, permission, enabled]) => canOnServer(me, server, permission) && (!compact || (enabled && name !== 'kill')),
  );
  if (visible.length === 0) {
    return null;
  }

  const refused = command.data && command.data.status !== 'accepted' && command.data.status !== 'alreadyInState';
  return (
    <section
      {...(compact
        ? { role: 'group', 'aria-label': t('servers.actionsFor', { name: server.name }) }
        : { 'aria-label': t('servers.actions') })}
      className={compact ? 'flex flex-col items-end gap-1' : 'space-y-2'}
    >
      <div className="flex flex-wrap gap-2">
        {visible.map(([name, , enabled, icon, variant]) => (
          <Button
            key={name}
            icon={icon}
            variant={variant}
            size={compact ? 'icon' : 'md'}
            aria-label={
              compact
                ? t('servers.commandFor', { command: t(`servers.commands.${name}`), name: server.name })
                : undefined
            }
            title={compact ? t(`servers.commands.${name}`) : undefined}
            disabled={!enabled || command.isPending}
            busy={command.isPending && command.variables.command === name}
            onClick={() =>
              (name !== 'kill' || window.confirm(t('servers.killConfirm', { name: server.name }))) &&
              command.mutate({ id: server.id, command: name })
            }
          >
            {!compact && t(`servers.commands.${name}`)}
          </Button>
        ))}
      </div>
      {command.isError &&
        (compact ? (
          <p role="alert" className="text-xs text-danger">
            {errorMessage(t, command.error)}
          </p>
        ) : (
          <Alert>{errorMessage(t, command.error)}</Alert>
        ))}
      {refused && (
        <p role="status" className={compact ? 'text-xs text-danger' : 'text-sm text-danger'}>
          {commandRefusal(t, command.data.status, command.data.errorCode)}
        </p>
      )}
    </section>
  );
}
