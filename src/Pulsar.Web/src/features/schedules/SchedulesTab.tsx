import { useMemo, useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import type { TFunction } from 'i18next';
import { Button, Spinner } from '../../components/ui/Button';
import { Dialog } from '../../components/ui/Dialog';
import { Alert, Badge, Table, Td, Th } from '../../components/ui/Display';
import { Checkbox, Field, Input, Select } from '../../components/ui/Form';
import { errorMessage } from '../../lib/errors';
import { useFormatDate } from '../../lib/format';
import { canOnServer, Permission } from '../../lib/permissions';
import { useCurrentUser } from '../auth/api';
import type { ServerDetails } from '../servers/api';
import {
  useDeleteSchedule,
  useRunSchedule,
  useSaveSchedule,
  useScheduleRuns,
  useSchedulePreview,
  useSchedules,
  type ScheduledAction,
  type ScheduledRunView,
  type ScheduledTaskView,
} from './api';
import { browserTimeZone, fromCron, HOUR_INTERVALS, timeOfDay, timeZones, toCron, type ScheduleKind } from './cron';

const MAX_NAME_LENGTH = 128;
const MAX_COMMAND_LENGTH = 512;

/** Each action and the permission a user needs to schedule it (the panel checks it too). */
const actionPermissions: Record<ScheduledAction, string> = {
  restart: Permission.ServerRestart,
  start: Permission.ServerStart,
  stop: Permission.ServerStop,
  backup: Permission.ServerBackupsCreate,
  update: Permission.ServerInstall,
  consoleCommand: Permission.ServerConsoleWrite,
};

const runTones = {
  running: 'accent',
  succeeded: 'success',
  failed: 'danger',
  skipped: 'neutral',
} as const;

/**
 * The server's scheduled tasks (docs/architecture/07, "Schedules"): what runs when, in which time zone, and how the
 * last runs went. Kept fresh by the live channel (a run ending changes the server).
 */
export function SchedulesTab({ details }: { details: ServerDetails }) {
  const { t } = useTranslation();
  const me = useCurrentUser();
  const { server } = details;
  const schedules = useSchedules(server.id);
  const run = useRunSchedule(server.id);
  const remove = useDeleteSchedule(server.id);
  const [editing, setEditing] = useState<ScheduledTaskView | 'new' | null>(null);
  const [history, setHistory] = useState<ScheduledTaskView | null>(null);
  const canEdit = canOnServer(me, server, Permission.ServerSchedulesEdit);
  const allowed = (Object.keys(actionPermissions) as ScheduledAction[]).filter((action) =>
    canOnServer(me, server, actionPermissions[action]),
  );

  if (schedules.isPending) {
    return <Spinner label={t('common.loading')} />;
  }
  if (schedules.isError) {
    return <Alert>{errorMessage(t, schedules.error)}</Alert>;
  }

  const error = run.error ?? remove.error;
  const deleteTask = (task: ScheduledTaskView) =>
    window.confirm(t('schedules.deleteConfirm', { name: task.name })) && remove.mutate(task.id);

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <p className="max-w-2xl text-sm text-muted">{t('schedules.hint')}</p>
        {canEdit && allowed.length > 0 && <Button onClick={() => setEditing('new')}>{t('schedules.create')}</Button>}
      </div>
      {error && <Alert>{errorMessage(t, error)}</Alert>}

      {schedules.data.tasks.length === 0 ? (
        <p className="text-sm text-muted">{t('schedules.empty')}</p>
      ) : (
        <Table label={t('schedules.title')}>
          <thead>
            <tr>
              <Th>{t('schedules.task')}</Th>
              <Th className="hidden md:table-cell">{t('schedules.when')}</Th>
              <Th className="hidden lg:table-cell">{t('schedules.nextRun')}</Th>
              <Th>{t('schedules.lastRun')}</Th>
              <Th className="text-right">
                <span className="sr-only">{t('common.actions')}</span>
              </Th>
            </tr>
          </thead>
          <tbody>
            {schedules.data.tasks.map((task) => (
              <TaskRow
                key={task.id}
                task={task}
                canEdit={canEdit && allowed.includes(task.action)}
                pending={
                  (run.isPending && run.variables === task.id) || (remove.isPending && remove.variables === task.id)
                }
                onRun={() => run.mutate(task.id)}
                onEdit={() => setEditing(task)}
                onHistory={() => setHistory(task)}
                onDelete={() => deleteTask(task)}
                showDelete={canEdit}
              />
            ))}
          </tbody>
        </Table>
      )}

      {editing !== null && (
        <TaskDialog
          serverId={server.id}
          task={editing === 'new' ? null : editing}
          actions={allowed}
          onDone={() => setEditing(null)}
        />
      )}
      {history !== null && <HistoryDialog serverId={server.id} task={history} onDone={() => setHistory(null)} />}
    </div>
  );
}

interface TaskRowProps {
  task: ScheduledTaskView;
  canEdit: boolean;
  showDelete: boolean;
  pending: boolean;
  onRun: () => void;
  onEdit: () => void;
  onHistory: () => void;
  onDelete: () => void;
}

function TaskRow({ task, canEdit, showDelete, pending, onRun, onEdit, onHistory, onDelete }: TaskRowProps) {
  const { t, i18n } = useTranslation();
  const formatDate = useFormatDate();
  const when = describeSchedule(t, i18n.language, task.cron, task.timeZone);
  return (
    <tr>
      <Td className="max-w-md">
        <span className="flex min-w-0 flex-wrap items-center gap-2">
          <span className="truncate font-medium">{task.name}</span>
          {!task.enabled && <Badge>{t('schedules.disabled')}</Badge>}
        </span>
        <p className="mt-1 text-xs break-words text-muted">{describeAction(t, task)}</p>
        <p className="mt-1 text-xs text-muted md:hidden">{when}</p>
      </Td>
      <Td className="hidden text-muted md:table-cell">{when}</Td>
      <Td className="hidden whitespace-nowrap text-muted lg:table-cell">
        {task.nextRunAt ? formatDate(task.nextRunAt) : t('common.none')}
      </Td>
      <Td>
        {task.lastRun ? (
          <span className="flex flex-col items-start gap-1">
            <Badge tone={runTones[task.lastRun.status]}>{t(`schedules.runStatuses.${task.lastRun.status}`)}</Badge>
            <span className="text-xs whitespace-nowrap text-muted">{formatDate(task.lastRun.scheduledFor)}</span>
            {runDetail(t, task.lastRun) && (
              <span className="text-xs break-words text-muted">{runDetail(t, task.lastRun)}</span>
            )}
          </span>
        ) : (
          <span className="text-sm text-muted">{t('common.never')}</span>
        )}
      </Td>
      <Td className="text-right whitespace-nowrap">
        <span className="inline-flex flex-wrap justify-end gap-1">
          {canEdit && (
            <Button
              variant="ghost"
              size="sm"
              disabled={pending}
              aria-label={t('schedules.runNowNamed', { name: task.name })}
              onClick={onRun}
            >
              {t('schedules.runNow')}
            </Button>
          )}
          <Button
            variant="ghost"
            size="sm"
            aria-label={t('schedules.historyNamed', { name: task.name })}
            onClick={onHistory}
          >
            {t('schedules.history')}
          </Button>
          {canEdit && (
            <Button
              variant="ghost"
              size="sm"
              disabled={pending}
              aria-label={t('schedules.editNamed', { name: task.name })}
              onClick={onEdit}
            >
              {t('actions.edit')}
            </Button>
          )}
          {showDelete && (
            <Button
              variant="ghost"
              size="sm"
              disabled={pending}
              aria-label={t('schedules.deleteNamed', { name: task.name })}
              onClick={onDelete}
            >
              {t('actions.delete')}
            </Button>
          )}
        </span>
      </Td>
    </tr>
  );
}

/** "Restart, only while running", "Console: say hello", "Update, verifying every file". */
function describeAction(t: TFunction, task: ScheduledTaskView): string {
  const action = t(`schedules.actions.${task.action}`);
  if (task.action === 'consoleCommand') {
    return t('schedules.commandSummary', { command: task.command ?? '' });
  }
  if (task.action === 'update' && task.validate) {
    return `${action} · ${t('schedules.validate')}`;
  }
  return task.onlyIfRunning ? `${action} · ${t('schedules.onlyIfRunningShort')}` : action;
}

/** "Every day at 04:00", plus the task's time zone when it is not the browser's. */
function describeSchedule(t: TFunction, language: string, cron: string, timeZone: string): string {
  const schedule = fromCron(cron);
  const text =
    schedule.kind === 'daily'
      ? t('schedules.every.daily', { time: timeOfDay(schedule.hour, schedule.minute) })
      : schedule.kind === 'weekly'
        ? t('schedules.every.weekly', {
            day: weekday(language, schedule.day),
            time: timeOfDay(schedule.hour, schedule.minute),
          })
        : schedule.kind === 'hours'
          ? t('schedules.every.hours', { count: schedule.every })
          : t('schedules.every.custom', { cron: schedule.cron });
  return timeZone === browserTimeZone() ? text : `${text} (${timeZone})`;
}

/** Why a run did not do its action, in the UI language. */
function runDetail(t: TFunction, run: ScheduledRunView): string | null {
  if (run.errorCode === 'schedules.missed') {
    return t('schedules.missed', { count: run.occurrences });
  }
  if (run.errorCode) {
    return t(`errors.${run.errorCode}`, { defaultValue: run.message ?? run.errorCode, detail: run.message ?? '' });
  }
  return null;
}

function weekday(language: string, day: number): string {
  // 2023-01-01 was a Sunday (day 0 in cron).
  return new Intl.DateTimeFormat(language, { weekday: 'long', timeZone: 'UTC' }).format(
    new Date(Date.UTC(2023, 0, 1 + day)),
  );
}

/**
 * Formats in the task's time zone; one the browser does not know (the panel accepts every zone .NET resolves) in UTC,
 * and says it did.
 */
function zoneFormat(language: string, timeZone: string): [Intl.DateTimeFormat, boolean] {
  const style = { dateStyle: 'full', timeStyle: 'short' } as const;
  try {
    return [new Intl.DateTimeFormat(language, { ...style, timeZone }), true];
  } catch {
    return [new Intl.DateTimeFormat(language, { ...style, timeZone: 'UTC' }), false];
  }
}

interface TaskDialogProps {
  serverId: string;
  task: ScheduledTaskView | null;
  actions: ScheduledAction[];
  onDone: () => void;
}

function TaskDialog({ serverId, task, actions, onDone }: TaskDialogProps) {
  const { t, i18n } = useTranslation();
  const save = useSaveSchedule(serverId);
  const [name, setName] = useState(task?.name ?? '');
  const [action, setAction] = useState<ScheduledAction>(task?.action ?? actions[0] ?? 'restart');
  const [command, setCommand] = useState(task?.command ?? '');
  const [validate, setValidate] = useState(task?.validate ?? false);
  const [onlyIfRunning, setOnlyIfRunning] = useState(task?.onlyIfRunning ?? false);
  const [enabled, setEnabled] = useState(task?.enabled ?? true);
  const [schedule, setSchedule] = useState<ScheduleKind>(
    task ? fromCron(task.cron) : { kind: 'daily', hour: 4, minute: 0 },
  );
  const [timeZone, setTimeZone] = useState(task?.timeZone ?? browserTimeZone());
  const zones = useMemo(() => timeZones(timeZone), [timeZone]);
  const cron = toCron(schedule);
  const preview = useSchedulePreview(serverId, cron, timeZone);
  const canWaitForRunning = action === 'restart' || action === 'backup';
  const nameInvalid = name.trim() === '' || name.trim().length > MAX_NAME_LENGTH;
  const commandInvalid =
    action === 'consoleCommand' && (command.trim() === '' || command.trim().length > MAX_COMMAND_LENGTH);
  const [submitted, setSubmitted] = useState(false);

  const submit = (event: FormEvent) => {
    event.preventDefault();
    setSubmitted(true);
    if (nameInvalid || commandInvalid) {
      return;
    }
    save.mutate(
      {
        taskId: task?.id ?? null,
        request: {
          name: name.trim(),
          cron,
          timeZone,
          action,
          command: action === 'consoleCommand' ? command.trim() : null,
          validate: action === 'update' && validate,
          onlyIfRunning: canWaitForRunning && onlyIfRunning,
          enabled,
        },
      },
      { onSuccess: onDone },
    );
  };

  const [formatInZone, zoneKnown] = zoneFormat(i18n.language, timeZone);

  return (
    <Dialog
      open
      onOpenChange={(open) => !open && onDone()}
      title={task ? t('schedules.editTitle', { name: task.name }) : t('schedules.createTitle')}
    >
      <form onSubmit={submit} className="space-y-4" noValidate>
        <Field
          label={t('schedules.name')}
          error={submitted && nameInvalid ? t('schedules.nameInvalid', { max: MAX_NAME_LENGTH }) : undefined}
        >
          {(control) => (
            <Input
              {...control}
              value={name}
              maxLength={MAX_NAME_LENGTH + 1}
              onChange={(e) => setName(e.target.value)}
            />
          )}
        </Field>
        <Field label={t('schedules.action')}>
          {(control) => (
            <Select {...control} value={action} onChange={(e) => setAction(e.target.value as ScheduledAction)}>
              {actions.map((a) => (
                <option key={a} value={a}>
                  {t(`schedules.actions.${a}`)}
                </option>
              ))}
            </Select>
          )}
        </Field>
        {action === 'consoleCommand' && (
          <Field
            label={t('schedules.command')}
            hint={t('schedules.commandHint')}
            error={submitted && commandInvalid ? t('schedules.commandInvalid', { max: MAX_COMMAND_LENGTH }) : undefined}
          >
            {(control) => (
              <Input
                {...control}
                value={command}
                maxLength={MAX_COMMAND_LENGTH + 1}
                className="font-mono"
                onChange={(e) => setCommand(e.target.value)}
              />
            )}
          </Field>
        )}
        {action === 'update' && (
          <Checkbox
            label={t('schedules.validate')}
            checked={validate}
            onChange={(e) => setValidate(e.target.checked)}
          />
        )}
        {action === 'update' && <p className="text-sm text-muted">{t('schedules.updateHint')}</p>}
        {canWaitForRunning && (
          <Checkbox
            label={t('schedules.onlyIfRunning')}
            checked={onlyIfRunning}
            onChange={(e) => setOnlyIfRunning(e.target.checked)}
          />
        )}

        <ScheduleFields schedule={schedule} onChange={setSchedule} />
        <Field label={t('schedules.timeZone')} hint={t('schedules.timeZoneHint')}>
          {(control) => (
            <Select {...control} value={timeZone} onChange={(e) => setTimeZone(e.target.value)}>
              {zones.map((zone) => (
                <option key={zone} value={zone}>
                  {zone}
                </option>
              ))}
            </Select>
          )}
        </Field>

        <div aria-live="polite" className="rounded-md border border-border p-3 text-sm">
          <p className="font-medium">{t('schedules.nextRuns')}</p>
          {!zoneKnown && <p className="mt-1 text-muted">{t('schedules.timeZoneUnknown', { zone: timeZone })}</p>}
          {preview.isError ? (
            <p className="mt-1 text-danger">{errorMessage(t, preview.error)}</p>
          ) : preview.data ? (
            <ul className="mt-1 space-y-0.5 text-muted">
              {preview.data.occurrences.map((at) => (
                <li key={at}>{formatInZone.format(new Date(at))}</li>
              ))}
            </ul>
          ) : (
            <p className="mt-1 text-muted">{t('common.loading')}</p>
          )}
        </div>

        <Checkbox label={t('schedules.enabled')} checked={enabled} onChange={(e) => setEnabled(e.target.checked)} />
        {save.isError && <Alert>{errorMessage(t, save.error)}</Alert>}
        <div className="flex justify-end gap-2">
          <Button type="button" variant="ghost" onClick={onDone}>
            {t('actions.cancel')}
          </Button>
          <Button type="submit" busy={save.isPending}>
            {t('actions.save')}
          </Button>
        </div>
      </form>
    </Dialog>
  );
}

/** Daily, weekly, every few hours, or cron for anything else. */
function ScheduleFields({
  schedule,
  onChange,
}: {
  schedule: ScheduleKind;
  onChange: (schedule: ScheduleKind) => void;
}) {
  const { t, i18n } = useTranslation();
  const time =
    schedule.kind === 'daily' || schedule.kind === 'weekly' ? timeOfDay(schedule.hour, schedule.minute) : '04:00';
  const setTime = (value: string) => {
    const [hour, minute] = value.split(':').map(Number);
    if (hour === undefined || minute === undefined || Number.isNaN(hour) || Number.isNaN(minute)) {
      return;
    }
    if (schedule.kind === 'daily' || schedule.kind === 'weekly') {
      onChange({ ...schedule, hour, minute });
    }
  };
  const changeKind = (kind: ScheduleKind['kind']) => {
    const [hour, minute] = time.split(':').map(Number) as [number, number];
    onChange(
      kind === 'daily'
        ? { kind, hour, minute }
        : kind === 'weekly'
          ? { kind, day: schedule.kind === 'weekly' ? schedule.day : 1, hour, minute }
          : kind === 'hours'
            ? { kind, every: 6 }
            : { kind, cron: toCron(schedule) },
    );
  };

  return (
    <div className="space-y-4">
      <Field label={t('schedules.repeat')}>
        {(control) => (
          <Select
            {...control}
            value={schedule.kind}
            onChange={(e) => changeKind(e.target.value as ScheduleKind['kind'])}
          >
            <option value="daily">{t('schedules.kinds.daily')}</option>
            <option value="weekly">{t('schedules.kinds.weekly')}</option>
            <option value="hours">{t('schedules.kinds.hours')}</option>
            <option value="custom">{t('schedules.kinds.custom')}</option>
          </Select>
        )}
      </Field>
      {schedule.kind === 'weekly' && (
        <Field label={t('schedules.day')}>
          {(control) => (
            <Select
              {...control}
              value={schedule.day}
              onChange={(e) => onChange({ ...schedule, day: Number(e.target.value) })}
            >
              {[1, 2, 3, 4, 5, 6, 0].map((day) => (
                <option key={day} value={day}>
                  {weekday(i18n.language, day)}
                </option>
              ))}
            </Select>
          )}
        </Field>
      )}
      {(schedule.kind === 'daily' || schedule.kind === 'weekly') && (
        <Field label={t('schedules.time')}>
          {(control) => (
            <Input {...control} type="time" value={time} required onChange={(e) => setTime(e.target.value)} />
          )}
        </Field>
      )}
      {schedule.kind === 'hours' && (
        <Field label={t('schedules.everyHours')}>
          {(control) => (
            <Select
              {...control}
              value={schedule.every}
              onChange={(e) => onChange({ kind: 'hours', every: Number(e.target.value) })}
            >
              {HOUR_INTERVALS.map((every) => (
                <option key={every} value={every}>
                  {t('schedules.every.hours', { count: every })}
                </option>
              ))}
            </Select>
          )}
        </Field>
      )}
      {schedule.kind === 'custom' && (
        <Field label={t('schedules.cron')} hint={t('schedules.cronHint')}>
          {(control) => (
            <Input
              {...control}
              value={schedule.cron}
              className="font-mono"
              spellCheck={false}
              onChange={(e) => onChange({ kind: 'custom', cron: e.target.value })}
            />
          )}
        </Field>
      )}
    </div>
  );
}

function HistoryDialog({ serverId, task, onDone }: { serverId: string; task: ScheduledTaskView; onDone: () => void }) {
  const { t } = useTranslation();
  const formatDate = useFormatDate();
  const runs = useScheduleRuns(serverId, task.id);
  return (
    <Dialog open onOpenChange={(open) => !open && onDone()} title={t('schedules.historyTitle', { name: task.name })}>
      {runs.isPending ? (
        <Spinner label={t('common.loading')} />
      ) : runs.isError ? (
        <Alert>{errorMessage(t, runs.error)}</Alert>
      ) : runs.data.runs.length === 0 ? (
        <p className="text-sm text-muted">{t('schedules.noRuns')}</p>
      ) : (
        <Table label={t('schedules.historyTitle', { name: task.name })}>
          <thead>
            <tr>
              <Th>{t('schedules.scheduledFor')}</Th>
              <Th>{t('schedules.result')}</Th>
            </tr>
          </thead>
          <tbody>
            {runs.data.runs.map((run) => (
              <tr key={run.id}>
                <Td className="whitespace-nowrap text-muted">{formatDate(run.scheduledFor)}</Td>
                <Td>
                  <Badge tone={runTones[run.status]}>{t(`schedules.runStatuses.${run.status}`)}</Badge>
                  {runDetail(t, run) && <p className="mt-1 text-xs break-words text-muted">{runDetail(t, run)}</p>}
                </Td>
              </tr>
            ))}
          </tbody>
        </Table>
      )}
    </Dialog>
  );
}
