import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import { Button, Spinner } from '../../components/ui/Button';
import { Alert, Card } from '../../components/ui/Display';
import { Checkbox, Field, Input, Select } from '../../components/ui/Form';
import { errorMessage } from '../../lib/errors';
import { useFormatBytes } from '../../lib/format';
import { canOnNode, canOnServer, Permission } from '../../lib/permissions';
import { useCurrentUser } from '../auth/api';
import { useAllocations, useNodePortsInUse } from '../nodes/api';
import {
  useCancelOperation,
  useServerSettings,
  useSetServerLimits,
  useUpdateServerPorts,
  useUpdateServerSettings,
  type OperationView,
  type ServerDetails,
  type ServerSettings,
} from './api';
import { useGameText } from './gameText';
import { PortNumberFields } from './PortFields';
import { checkPorts, chosenPorts, parsePort, portsReady, type TemplatePort } from './portChoices';
import { VariableFields } from './VariableFields';
import { initialValue, useUnreadableNumbers, variableError, type VariableValues } from './variables';
import { SteamAccountCard } from '../steam/SteamServerCards';
import { GameUpdateSettingsCard } from '../gameUpdates/GameUpdates';

/** Operations whose work the server's console shows as it runs (what SteamCMD prints, among others). */
const shownInConsole = new Set(['serverInstall', 'serverUpdate', 'addonInstall', 'addonRemove', 'modsChange']);

/**
 * The running operation: its stage, a progress bar when the agent reports one, Cancel for installs and updates, and a
 * way to the console that shows an install as it runs.
 */
export function OperationProgress({
  server,
  operation,
  inConsole = false,
}: {
  server: ServerRef;
  operation: OperationView;
  /** The console is the open tab: no way to it. */
  inConsole?: boolean;
}) {
  const { t } = useTranslation();
  const me = useCurrentUser();
  const cancel = useCancelOperation();
  const percent = operation.percent == null ? null : Math.round(operation.percent);
  // Backups are cancelled by whoever may make them; installs and updates by whoever may install.
  const cancelPermission =
    operation.type === 'backupCreate' ? Permission.ServerBackupsCreate : Permission.ServerInstall;
  return (
    <Card className="p-4">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <p className="text-sm font-medium">
          {t(`servers.operations.types.${operation.type}`)}
          {operation.stage && (
            <span className="ml-2 text-muted">
              · {t(`servers.stages.${operation.stage}`, { defaultValue: operation.stage })}
            </span>
          )}
          {percent !== null && <span className="ml-2 tabular-nums">{percent}%</span>}
        </p>
        <div className="flex flex-wrap items-center gap-2">
          {!inConsole &&
            shownInConsole.has(operation.type) &&
            canOnServer(me, server, Permission.ServerConsoleView) && (
              <Link to={{ search: '?tab=console' }} className="text-sm text-accent hover:underline">
                {t('servers.operations.showConsole')}
              </Link>
            )}
          {operation.cancellable && canOnServer(me, server, cancelPermission) && (
            <Button
              variant="secondary"
              disabled={operation.cancelRequested}
              busy={cancel.isPending}
              onClick={() => cancel.mutate({ id: server.id, operationId: operation.id })}
            >
              {operation.cancelRequested ? t('servers.operations.cancelling') : t('actions.cancel')}
            </Button>
          )}
        </div>
      </div>
      <div
        role="progressbar"
        aria-label={t(`servers.operations.types.${operation.type}`)}
        aria-valuemin={0}
        aria-valuemax={100}
        {...(percent !== null ? { 'aria-valuenow': percent } : {})}
        className="mt-3 h-2 overflow-hidden rounded-full bg-bg"
      >
        <div
          className={`h-full rounded-full bg-accent transition-[width] ${percent === null ? 'w-1/3 animate-pulse' : ''}`}
          style={percent === null ? undefined : { width: `${percent}%` }}
        />
      </div>
      {cancel.isError && <Alert>{errorMessage(t, cancel.error)}</Alert>}
    </Card>
  );
}

/** Variables, extra arguments and restart policy (docs/architecture/05, "Startup"): they apply at the next start. */
export function SettingsTab({ details }: { details: ServerDetails }) {
  const { t } = useTranslation();
  const settings = useServerSettings(details.server.id);
  // Here, not in the form: a save remounts the form with what the panel stored, and the result must survive that.
  const save = useUpdateServerSettings();
  const saveLimits = useSetServerLimits();
  if (settings.isPending) {
    return <Spinner label={t('common.loading')} />;
  }
  // A failed reload keeps the form (and what is typed in it) with the settings loaded last.
  if (settings.data === undefined) {
    return <Alert>{errorMessage(t, settings.error)}</Alert>;
  }
  // Keyed on the saved settings: a save starts the form again from what the panel stored.
  return (
    <div className="space-y-6">
      {settings.isError && <Alert>{t('servers.stale', { error: errorMessage(t, settings.error) })}</Alert>}
      <SettingsForm key={JSON.stringify(settings.data)} server={details.server} settings={settings.data} save={save} />
      <GameUpdateSettingsCard details={details} />
      <LimitsCard key={JSON.stringify(details.limits)} details={details} save={saveLimits} />
      <SteamAccountCard key={details.steam?.accountId ?? 'anonymous'} details={details} />
    </div>
  );
}

/**
 * CPU and memory caps (docs/architecture/06, "Resource limits") and how many databases the server may have. Everyone who sees the server sees them; changing them
 * takes server.limits.edit, which is given on a node or globally only (a server's owner does not raise their own).
 */
function LimitsCard({ details, save }: { details: ServerDetails; save: ReturnType<typeof useSetServerLimits> }) {
  const { t } = useTranslation();
  const me = useCurrentUser();
  const formatBytes = useFormatBytes();
  const { limits, server } = details;
  const canEdit = canOnServer(me, server, Permission.ServerLimitsEdit);
  const [cpu, setCpu] = useState(limits.cpuPercent?.toString() ?? '');
  const [memory, setMemory] = useState(limits.memoryMb?.toString() ?? '');
  const [databases, setDatabases] = useState(limits.databases.toString());
  const [databasesInvalid, setDatabasesInvalid] = useState(false);
  // What the browser cannot read as a number ("1e") it reports as empty, which would remove the cap: refused instead.
  const [unreadable, setUnreadable] = useState({ cpu: false, memory: false, databases: false });
  const [showUnreadable, setShowUnreadable] = useState(false);
  const number = (text: string) => (text.trim() === '' ? null : Number(text));
  const typed = (field: keyof typeof unreadable, input: HTMLInputElement) =>
    setUnreadable((current) => ({ ...current, [field]: input.validity.badInput }));
  const unreadableError = (field: keyof typeof unreadable) =>
    showUnreadable && unreadable[field] ? t('servers.limits.notANumber') : undefined;

  return (
    <Card className="p-6">
      <div role="region" aria-labelledby="server-limits" className="space-y-4">
        <div>
          <h2 id="server-limits" className="text-lg font-semibold">
            {t('servers.limits.title')}
          </h2>
          <p className="mt-1 text-sm text-muted">{t('servers.limits.hint')}</p>
        </div>
        {save.isError && <Alert>{errorMessage(t, save.error)}</Alert>}
        {save.isSuccess && (
          <p role="status" className="text-sm text-success">
            {t('servers.limits.saved')}
          </p>
        )}
        {canEdit ? (
          <form
            className="space-y-4"
            noValidate
            onSubmit={(e) => {
              e.preventDefault();
              setShowUnreadable(true);
              if (unreadable.cpu || unreadable.memory || unreadable.databases) {
                return;
              }
              // Empty CPU and memory remove their caps; an empty database limit keeps the current one.
              const databaseLimit = number(databases);
              const invalid =
                databaseLimit != null &&
                !(Number.isInteger(databaseLimit) && databaseLimit >= 0 && databaseLimit <= 50);
              setDatabasesInvalid(invalid);
              if (invalid) {
                return;
              }
              save.mutate({
                id: server.id,
                body: {
                  cpuPercent: number(cpu),
                  memoryMb: number(memory),
                  ...(databaseLimit == null ? {} : { databases: databaseLimit }),
                },
              });
            }}
          >
            <div className="grid gap-4 sm:grid-cols-3">
              <Field label={t('servers.limits.cpu')} error={unreadableError('cpu')}>
                {(ids) => (
                  <Input
                    {...ids}
                    type="number"
                    min={1}
                    max={100}
                    placeholder={t('servers.limits.none')}
                    value={cpu}
                    onChange={(e) => {
                      setCpu(e.target.value);
                      typed('cpu', e.target);
                    }}
                  />
                )}
              </Field>
              <Field label={t('servers.limits.memory')} error={unreadableError('memory')}>
                {(ids) => (
                  <Input
                    {...ids}
                    type="number"
                    min={128}
                    step={128}
                    placeholder={t('servers.limits.none')}
                    value={memory}
                    onChange={(e) => {
                      setMemory(e.target.value);
                      typed('memory', e.target);
                    }}
                  />
                )}
              </Field>
              <Field
                label={t('servers.limits.databases')}
                error={
                  unreadableError('databases') ?? (databasesInvalid ? t('servers.limits.databasesInvalid') : undefined)
                }
              >
                {(ids) => (
                  <Input
                    {...ids}
                    type="number"
                    min={0}
                    max={50}
                    value={databases}
                    onChange={(e) => {
                      setDatabases(e.target.value);
                      typed('databases', e.target);
                    }}
                  />
                )}
              </Field>
            </div>
            <div className="flex justify-end">
              <Button type="submit" busy={save.isPending}>
                {t('actions.save')}
              </Button>
            </div>
          </form>
        ) : (
          <ul className="space-y-1 text-sm">
            {limits.cpuPercent == null && limits.memoryMb == null && (
              <li className="text-muted">{t('servers.limits.noneSet')}</li>
            )}
            {limits.cpuPercent != null && <li>{t('servers.limits.cpuValue', { value: limits.cpuPercent })}</li>}
            {limits.memoryMb != null && (
              <li>{t('servers.limits.memoryValue', { value: formatBytes(limits.memoryMb * 1024 * 1024) })}</li>
            )}
            <li>{t('servers.limits.databasesValue', { count: limits.databases })}</li>
          </ul>
        )}
      </div>
    </Card>
  );
}

/** A server as permission checks need it, and its game (for its labels in the reader's language). */
interface ServerRef {
  id: string;
  nodeId: string;
  templateKey?: string;
}

interface SettingsFormProps {
  server: ServerRef;
  settings: ServerSettings;
  save: ReturnType<typeof useUpdateServerSettings>;
}

function SettingsForm({ server, settings, save }: SettingsFormProps) {
  const { t } = useTranslation();
  const me = useCurrentUser();
  const canEdit = canOnServer(me, server, Permission.ServerStartupEdit);
  const canEditProtected = canOnServer(me, server, Permission.ServerSettingsEdit);
  const gameText = useGameText(server.templateKey);
  const definitions = gameText.variables(settings.variables.map((v) => v.definition));
  const stored = Object.fromEntries(
    settings.variables.map((v) => [
      v.definition.key,
      v.definition.secret ? '' : (v.value ?? initialValue(v.definition)),
    ]),
  );
  const secretsSet = new Set(
    settings.variables.filter((v) => v.definition.secret && v.hasValue).map((v) => v.definition.key),
  );
  const [values, setValues] = useState<VariableValues>(stored);
  const [clearSecrets, setClearSecrets] = useState<ReadonlySet<string>>(new Set());
  const [extra, setExtra] = useState(settings.extraArguments.join('\n'));
  const [autoStart, setAutoStart] = useState(settings.autoStart);
  const [restartOnCrash, setRestartOnCrash] = useState(settings.restartOnCrash);
  const [showErrors, setShowErrors] = useState(false);
  const unreadable = useUnreadableNumbers();

  const errors = Object.fromEntries(
    definitions.map((v) => [
      v.key,
      variableError(
        v,
        values[v.key] ?? '',
        secretsSet.has(v.key) && !clearSecrets.has(v.key),
        unreadable.keys.has(v.key),
      ),
    ]),
  ) as Record<string, string | null>;

  const submit = () => {
    if (Object.values(errors).some((e) => e !== null)) {
      setShowErrors(true);
      return;
    }
    // Only what changed; a secret is sent when typed or cleared, never otherwise (the browser does not have it). An
    // empty value puts the default back (or clears a secret).
    const changed: Record<string, string> = {};
    for (const variable of definitions) {
      const value = values[variable.key] ?? '';
      if (variable.secret) {
        if (clearSecrets.has(variable.key)) {
          changed[variable.key] = '';
        } else if (value !== '') {
          changed[variable.key] = value;
        }
      } else if (value !== stored[variable.key]) {
        changed[variable.key] = value;
      }
    }
    save.mutate({
      id: server.id,
      body: {
        variables: changed,
        extraArguments: extra
          .split('\n')
          .map((line) => line.trim())
          .filter((line) => line !== ''),
        autoStart,
        restartOnCrash,
      },
    });
  };

  return (
    <Card className="space-y-6 p-6">
      <div>
        <h2 className="text-lg font-semibold">{t('servers.settings.title')}</h2>
        <p className="mt-1 text-sm text-muted">{t('servers.settings.hint')}</p>
      </div>
      {save.isError && <Alert>{errorMessage(t, save.error)}</Alert>}
      {save.isSuccess && (
        <p role="status" className="text-sm text-success">
          {t('servers.settings.saved')}
        </p>
      )}
      <VariableFields
        variables={definitions}
        values={values}
        onChange={(key, value) => setValues((current) => ({ ...current, [key]: value }))}
        optionsOpen
        secretsSet={secretsSet}
        errors={showErrors ? errors : undefined}
        onUnreadable={unreadable.mark}
        readOnly={(variable) => !canEdit || (!variable.userEditable && !canEditProtected)}
      />
      {canEdit && secretsSet.size > 0 && (
        <div className="space-y-2">
          {definitions
            .filter((v) => secretsSet.has(v.key))
            .map((v) => (
              <Checkbox
                key={v.key}
                label={t('servers.settings.clearSecret', { label: v.label })}
                checked={clearSecrets.has(v.key)}
                onChange={(e) =>
                  setClearSecrets((current) => {
                    const next = new Set(current);
                    if (e.target.checked) {
                      next.add(v.key);
                    } else {
                      next.delete(v.key);
                    }
                    return next;
                  })
                }
              />
            ))}
        </div>
      )}
      {settings.allowExtraArguments && (
        <Field label={t('servers.settings.extraArguments')} hint={t('servers.settings.extraArgumentsHint')}>
          {(ids) => (
            <textarea
              {...ids}
              rows={3}
              value={extra}
              disabled={!canEdit}
              onChange={(e) => setExtra(e.target.value)}
              className="w-full rounded-md border border-border bg-bg px-3 py-2 font-mono text-sm disabled:opacity-60"
            />
          )}
        </Field>
      )}
      <div className="space-y-2">
        <Checkbox
          label={t('servers.settings.autoStart')}
          checked={autoStart}
          disabled={!canEdit}
          onChange={(e) => setAutoStart(e.target.checked)}
        />
        <Checkbox
          label={t('servers.restartOnCrash')}
          checked={restartOnCrash}
          disabled={!canEdit}
          onChange={(e) => setRestartOnCrash(e.target.checked)}
        />
      </div>
      {canEdit && (
        <div className="flex justify-end">
          <Button busy={save.isPending} onClick={submit}>
            {t('actions.save')}
          </Button>
        </div>
      )}
    </Card>
  );
}

/**
 * The server's ports and the node allocations they use; changed only while the server is stopped. Whoever manages the
 * node types numbers (as when creating a server); others choose among the node's free allocations.
 */
export function NetworkTab(props: { details: ServerDetails }) {
  const { t } = useTranslation();
  // The ports' labels in the reader's language.
  const gameText = useGameText(props.details.server.templateKey);
  const details = { ...props.details, ports: gameText.ports(props.details.ports) };
  const me = useCurrentUser();
  // The node's allocations are for those who may list them (the API refuses anyone else).
  const canListAllocations = canOnNode(
    me,
    details.server.nodeId,
    Permission.NodeView,
    Permission.NodeManage,
    Permission.ServerCreate,
  );
  const allocations = useAllocations(canListAllocations ? details.server.nodeId : undefined);
  const save = useUpdateServerPorts();
  const [chosen, setChosen] = useState<Record<string, string>>({});
  const { server } = details;
  const stopped = server.runtimeState === 'offline' || server.runtimeState === 'crashed';
  const canEdit = canOnServer(me, server, Permission.ServerSettingsEdit) && stopped && server.activeOperation === null;
  const options = (allocations.data ?? []).filter((a) => !a.inUse || a.serverId === server.id);
  const canAddPorts = canOnNode(me, server.nodeId, Permission.NodeManage);
  // Asked while the ports can change (the server stopped): its own are free then.
  const portsInUse = useNodePortsInUse(server.nodeId, canAddPorts && canEdit);
  const inUse = portsInUse.data?.ports ?? [];
  const templatePorts: TemplatePort[] = details.ports.map((port) => ({
    name: port.name,
    label: port.label,
    required: port.required,
    follows: port.follows ?? null,
    offset: port.offset ?? null,
    default: port.default,
    protocols: port.protocols ?? [],
    description: port.description ?? null,
  }));
  // The numbers it has, then what was typed over them.
  const numbers: Record<string, string> = {
    ...Object.fromEntries(details.ports.map((port) => [port.name, port.port === null ? '' : String(port.port)])),
    ...chosen,
  };
  const numbersReady =
    allocations.isSuccess && portsReady(checkPorts(templatePorts, numbers, allocations.data, true, server.id, inUse));

  if (details.ports.length === 0) {
    return (
      <Card className="p-6">
        <p className="text-sm text-muted">{t('servers.network.none')}</p>
      </Card>
    );
  }

  return (
    <Card className="space-y-4 p-6">
      <div>
        <h2 className="text-lg font-semibold">{t('servers.network.title')}</h2>
        <p className="mt-1 text-sm text-muted">
          {stopped ? t('servers.network.hint') : t('servers.network.stopFirst')}
        </p>
      </div>
      {save.isError && <Alert>{errorMessage(t, save.error)}</Alert>}
      {allocations.isError && <Alert>{errorMessage(t, allocations.error)}</Alert>}
      {canAddPorts && allocations.isSuccess && (
        <PortNumberFields
          ports={templatePorts}
          values={numbers}
          onChange={(port, value) => setChosen((c) => ({ ...c, [port]: value }))}
          allocations={allocations.data}
          serverId={server.id}
          inUse={inUse}
          disabled={!canEdit}
        />
      )}
      {!canAddPorts &&
        details.ports.map((port) => {
          const current = port.allocationId ?? '';
          if (port.follows) {
            // Not chosen: it comes with the port it follows.
            return (
              <p key={port.name} className="text-sm">
                <span className="font-medium">{port.label}</span>{' '}
                {port.ipAddress && (
                  <span>
                    {port.ipAddress}:{port.port}{' '}
                  </span>
                )}
                <span className="text-muted">
                  {t('servers.network.follows', {
                    port: details.ports.find((p) => p.name === port.follows)?.label ?? port.follows,
                  })}
                </span>
              </p>
            );
          }
          return (
            <Field key={port.name} label={port.required ? `${port.label} *` : port.label}>
              {(ids) => (
                <Select
                  {...ids}
                  value={chosen[port.name] ?? current}
                  disabled={!canEdit}
                  onChange={(e) => setChosen((c) => ({ ...c, [port.name]: e.target.value }))}
                >
                  {!port.required && <option value="">{t('servers.wizard.noPort')}</option>}
                  {port.required && current === '' && <option value="">—</option>}
                  {/* The allocation it has is always there: the list may be missing (not allowed) or still loading. */}
                  {current !== '' && !options.some((a) => a.id === current) && (
                    <option value={current}>
                      {port.ipAddress}:{port.port}
                    </option>
                  )}
                  {options.map((allocation) => (
                    <option key={allocation.id} value={allocation.id}>
                      {allocation.ipAddress}:{allocation.port}
                    </option>
                  ))}
                </Select>
              )}
            </Field>
          );
        })}
      {canEdit && (
        <div className="flex justify-end">
          <Button
            busy={save.isPending}
            disabled={Object.keys(chosen).length === 0 || (canAddPorts && !numbersReady)}
            onClick={() =>
              save.mutate(
                canAddPorts
                  ? {
                      id: server.id,
                      portNumbers: Object.fromEntries(
                        chosenPorts(templatePorts).map((p) => [p.name, parsePort(numbers[p.name] ?? '')]),
                      ),
                    }
                  : {
                      id: server.id,
                      ports: Object.fromEntries(
                        details.ports
                          .filter((p) => !p.follows)
                          .map((p) => [p.name, (chosen[p.name] ?? p.allocationId ?? '') || null]),
                      ),
                    },
                { onSuccess: () => setChosen({}) },
              )
            }
          >
            {t('actions.save')}
          </Button>
        </div>
      )}
    </Card>
  );
}
