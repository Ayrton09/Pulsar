import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button, Spinner } from '../../components/ui/Button';
import { Alert, Card } from '../../components/ui/Display';
import { Checkbox, Field, Select } from '../../components/ui/Form';
import { errorMessage } from '../../lib/errors';
import { can, Permission } from '../../lib/permissions';
import { useCurrentUser } from '../auth/api';
import { useAllocations } from '../nodes/api';
import {
  useCancelOperation,
  useServerSettings,
  useUpdateServerPorts,
  useUpdateServerSettings,
  type OperationView,
  type ServerDetails,
  type ServerSettings,
} from './api';
import { VariableFields } from './VariableFields';
import { initialValue, variableError, type VariableValues } from './variables';

/** The running operation: its stage, a progress bar when the agent reports one, and Cancel for installs and updates. */
export function OperationProgress({ serverId, operation }: { serverId: string; operation: OperationView }) {
  const { t } = useTranslation();
  const me = useCurrentUser();
  const cancel = useCancelOperation();
  const percent = operation.percent == null ? null : Math.round(operation.percent);
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
        {operation.cancellable && can(me, Permission.ServerInstall) && (
          <Button
            variant="secondary"
            disabled={operation.cancelRequested}
            busy={cancel.isPending}
            onClick={() => cancel.mutate({ id: serverId, operationId: operation.id })}
          >
            {operation.cancelRequested ? t('servers.operations.cancelling') : t('actions.cancel')}
          </Button>
        )}
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
  if (settings.isPending) {
    return <Spinner label={t('common.loading')} />;
  }
  if (settings.isError) {
    return <Alert>{errorMessage(t, settings.error)}</Alert>;
  }
  // Keyed on the saved settings: a save starts the form again from what the panel stored.
  return <SettingsForm key={JSON.stringify(settings.data)} serverId={details.server.id} settings={settings.data} />;
}

function SettingsForm({ serverId, settings }: { serverId: string; settings: ServerSettings }) {
  const { t } = useTranslation();
  const me = useCurrentUser();
  const save = useUpdateServerSettings();
  const canEdit = can(me, Permission.ServerStartupEdit);
  const canEditProtected = can(me, Permission.ServerSettingsEdit);
  const definitions = settings.variables.map((v) => v.definition);
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

  const errors = Object.fromEntries(
    definitions.map((v) => [
      v.key,
      variableError(v, values[v.key] ?? '', secretsSet.has(v.key) && !clearSecrets.has(v.key)),
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
      id: serverId,
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
        secretsSet={secretsSet}
        errors={showErrors ? errors : undefined}
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

/** The server's ports and the node allocations they use; changed only while the server is stopped. */
export function NetworkTab({ details }: { details: ServerDetails }) {
  const { t } = useTranslation();
  const me = useCurrentUser();
  const allocations = useAllocations(details.server.nodeId);
  const save = useUpdateServerPorts();
  const [chosen, setChosen] = useState<Record<string, string>>({});
  const { server } = details;
  const stopped = server.runtimeState === 'offline' || server.runtimeState === 'crashed';
  const canEdit = can(me, Permission.ServerSettingsEdit) && stopped && server.activeOperation === null;
  const options = (allocations.data ?? []).filter((a) => a.serverId === null || a.serverId === server.id);

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
      {details.ports.map((port) => {
        const current = port.allocationId ?? '';
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
            disabled={Object.keys(chosen).length === 0}
            onClick={() =>
              save.mutate(
                {
                  id: server.id,
                  ports: Object.fromEntries(
                    details.ports.map((p) => [p.name, (chosen[p.name] ?? p.allocationId ?? '') || null]),
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
