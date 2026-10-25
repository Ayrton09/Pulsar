import { QueryClient } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter } from 'react-router';
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import type { Schemas } from '../../api/client';
import { App } from '../../app/App';
import { initI18n } from '../../app/i18n';
import { routes } from '../../app/routes';
import { adminUser, fakeApi, json, problem, session } from '../../test/fakeApi';
import { fromCron, toCron } from './cron';

const serverId = '0192f1a0-0000-7000-8000-0000000000e1';
const taskId = '0192f1a0-0000-7000-8000-0000000000d1';
const schedulesPath = `/api/v1/servers/${serverId}/schedules`;
const manager = {
  ...adminUser,
  permissions: [
    'server.view',
    'server.schedules.view',
    'server.schedules.edit',
    'server.restart',
    'server.backups.create',
  ],
};
const viewer = { ...adminUser, permissions: ['server.view', 'server.schedules.view'] };
const operator = {
  ...adminUser,
  permissions: [...manager.permissions, 'server.console.write', 'server.stop', 'server.start', 'server.install'],
};

const details: Schemas['ServerDetails'] = {
  server: {
    id: serverId,
    shortId: 'k3x7m2qa',
    name: 'Retakes',
    nodeId: '0192f1a0-0000-7000-8000-0000000000a1',
    nodeName: 'Buenos Aires 1',
    nodeConnected: true,
    templateId: '0192f1a0-0000-7000-8000-0000000000f1',
    templateName: 'Counter-Strike 2',
    templateKey: 'cs2',
    gameAddress: null,
    gameUpdateAvailable: false,
    installState: 'installed',
    runtimeState: 'running',
    desiredState: 'running',
    lastStateReason: null,
    lastExitCode: null,
    lastStartedAt: null,
    lastStoppedAt: null,
    activeOperation: null,
    createdAt: '2026-10-02T12:00:00Z',
  },
  description: null,
  autoStart: false,
  restartOnCrash: true,
  templateKey: 'cs2',
  templateVersion: '1.0.0',
  canUpdate: true,
  ports: [],
  recentOperations: [],
  limits: { cpuPercent: null, memoryMb: null, databases: 2 },
  hasAddons: false,
};

function run(changes: Partial<Schemas['ScheduledRunView']> = {}): Schemas['ScheduledRunView'] {
  return {
    id: '0192f1a0-0000-7000-8000-0000000000c1',
    scheduledFor: '2026-10-03T07:00:00Z',
    startedAt: '2026-10-03T07:00:00Z',
    finishedAt: '2026-10-03T07:00:01Z',
    status: 'succeeded',
    occurrences: 1,
    errorCode: null,
    message: null,
    operationId: null,
    ...changes,
  };
}

function task(changes: Partial<Schemas['ScheduledTaskView']> = {}): Schemas['ScheduledTaskView'] {
  return {
    id: taskId,
    name: 'Nightly restart',
    cron: '0 4 * * *',
    timeZone: 'America/Argentina/Buenos_Aires',
    action: 'restart',
    command: null,
    validate: false,
    steps: null,
    onlyIfRunning: false,
    onlyIfEmpty: false,
    onlyIfUpdateAvailable: false,
    enabled: true,
    nextRunAt: '2026-10-04T07:00:00Z',
    lastRunAt: '2026-10-03T07:00:00Z',
    lastRun: run(),
    createdAt: '2026-10-01T12:00:00Z',
    updatedAt: '2026-10-01T12:00:00Z',
    ...changes,
  };
}

const abilities: Schemas['ScheduleAbilities'] = { canBroadcast: true, knowsPlayers: true, updatesChecked: true };

const tasks: Schemas['ScheduledTaskList'] = {
  abilities,
  tasks: [
    task(),
    task({
      id: '0192f1a0-0000-7000-8000-0000000000d2',
      name: 'Announce',
      cron: '*/20 * * * *',
      timeZone: 'UTC',
      action: 'consoleCommand',
      command: 'say Restart at 4',
      enabled: false,
      nextRunAt: null,
      lastRun: run({ status: 'skipped', errorCode: 'schedules.missed', occurrences: 3 }),
    }),
  ],
};

const preview: Schemas['SchedulePreview'] = {
  occurrences: ['2026-10-04T08:30:00Z', '2026-10-05T08:30:00Z', '2026-10-06T08:30:00Z'],
};

function renderAt(path: string) {
  const router = createMemoryRouter(routes, { initialEntries: [path] });
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false, refetchInterval: false } } });
  render(<App router={router} queryClient={queryClient} />);
  return router;
}

function row(name: string): HTMLElement {
  const table = screen.getByRole('table', { name: 'Scheduled tasks' });
  return within(table)
    .getAllByRole('row')
    .find((r) => within(r).queryByText(name) !== null) as HTMLElement;
}

beforeAll(async () => {
  await initI18n('en-US');
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('cron presets', () => {
  it('turns presets into cron and recognizes them back', () => {
    expect(toCron({ kind: 'daily', hour: 4, minute: 5 })).toBe('5 4 * * *');
    expect(toCron({ kind: 'weekly', day: 1, hour: 17, minute: 30 })).toBe('30 17 * * 1');
    expect(toCron({ kind: 'hours', every: 1 })).toBe('0 * * * *');
    expect(toCron({ kind: 'hours', every: 6 })).toBe('0 */6 * * *');
    expect(fromCron('5 4 * * *')).toEqual({ kind: 'daily', hour: 4, minute: 5 });
    expect(fromCron('30  17 * * 1')).toEqual({ kind: 'weekly', day: 1, hour: 17, minute: 30 });
    expect(fromCron('0 */6 * * *')).toEqual({ kind: 'hours', every: 6 });
    expect(fromCron('0 */5 * * *')).toEqual({ kind: 'custom', cron: '0 */5 * * *' });
    expect(fromCron('30 5 * * 1-5')).toEqual({ kind: 'custom', cron: '30 5 * * 1-5' });
  });
});

describe('schedules tab', () => {
  it('lists tasks with when they run and how they last went', async () => {
    fakeApi({
      'GET /api/v1/auth/session': session(manager),
      [`GET /api/v1/servers/${serverId}`]: () => json(details),
      [`GET ${schedulesPath}`]: () => json(tasks),
    });

    renderAt(`/servers/${serverId}?tab=schedules`);

    await screen.findByRole('table', { name: 'Scheduled tasks' });
    expect(row('Nightly restart')).toHaveTextContent('Every day at 04:00 (America/Argentina/Buenos_Aires)');
    expect(row('Nightly restart')).toHaveTextContent('Done');
    expect(row('Announce')).toHaveTextContent('Console: say Restart at 4');
    expect(row('Announce')).toHaveTextContent('Disabled');
    expect(row('Announce')).toHaveTextContent('Cron */20 * * * *');
    expect(row('Announce')).toHaveTextContent('3 runs missed: the panel was not running.');
    // Only restarts and backups may be scheduled by this user: the console task stays read-only.
    expect(screen.getByRole('button', { name: 'Edit Nightly restart' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Edit Announce' })).not.toBeInTheDocument();
  });

  it('creates a daily task in a chosen time zone, previewing its runs', async () => {
    const saved: unknown[] = [];
    const previews: string[] = [];
    fakeApi({
      'GET /api/v1/auth/session': session(manager),
      [`GET /api/v1/servers/${serverId}`]: () => json(details),
      [`GET ${schedulesPath}`]: () => json({ tasks: [], abilities }),
      [`GET ${schedulesPath}/preview`]: (request) => {
        const query = new URL(request.url).searchParams;
        previews.push(`${query.get('cron')} @ ${query.get('timeZone')}`);
        return json(preview);
      },
      [`POST ${schedulesPath}`]: async (request) => {
        saved.push(await request.json());
        return json(task(), 201);
      },
    });

    renderAt(`/servers/${serverId}?tab=schedules`);

    expect(await screen.findByText('No scheduled tasks yet.')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'New task' }));
    const dialog = await screen.findByRole('dialog', { name: 'New scheduled task' });
    expect(
      within(dialog)
        .getAllByRole('option')
        .map((o) => o.textContent),
    ).toEqual(expect.arrayContaining(['Restart', 'Back up']));
    expect(within(dialog).queryByRole('option', { name: 'Console command' })).not.toBeInTheDocument();
    await userEvent.type(within(dialog).getByLabelText('Name'), 'Nightly backup');
    await userEvent.selectOptions(within(dialog).getByLabelText('Action'), 'backup');
    await userEvent.click(within(dialog).getByLabelText('Only while the server is running'));
    // jsdom types into time inputs one character at a time; a browser's picker sets the whole value.
    fireEvent.change(within(dialog).getByLabelText('Time'), { target: { value: '05:30' } });
    await userEvent.selectOptions(within(dialog).getByLabelText('Time zone'), 'Europe/Madrid');
    expect(await within(dialog).findAllByRole('listitem')).toHaveLength(3);
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save' }));

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(saved).toEqual([
      {
        name: 'Nightly backup',
        cron: '30 5 * * *',
        timeZone: 'Europe/Madrid',
        action: 'backup',
        command: null,
        validate: false,
        onlyIfRunning: true,
        enabled: true,
        steps: null,
        onlyIfEmpty: false,
        onlyIfUpdateAvailable: false,
      },
    ]);
    expect(previews).toContain('30 5 * * * @ Europe/Madrid');
  });

  it('writes a console command as one line, like the console does: a tab is a space, other control characters go', async () => {
    const saved: unknown[] = [];
    fakeApi({
      'GET /api/v1/auth/session': session({
        ...manager,
        permissions: [...manager.permissions, 'server.console.write'],
      }),
      [`GET /api/v1/servers/${serverId}`]: () => json(details),
      [`GET ${schedulesPath}`]: () => json({ tasks: [], abilities }),
      [`GET ${schedulesPath}/preview`]: () => json(preview),
      [`POST ${schedulesPath}`]: async (request) => {
        saved.push(await request.json());
        return json(task(), 201);
      },
    });
    renderAt(`/servers/${serverId}?tab=schedules`);
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'New task' }));
    const dialog = await screen.findByRole('dialog', { name: 'New scheduled task' });
    await user.type(within(dialog).getByLabelText('Name'), 'Announce');
    await user.selectOptions(within(dialog).getByLabelText('Action'), 'consoleCommand');
    const command = within(dialog).getByLabelText('Command');
    await user.click(command);
    await user.paste('say\thi\u0007 there');

    expect(command).toHaveValue('say hi there');
    await user.click(within(dialog).getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(saved).toHaveLength(1));
    expect(saved[0]).toMatchObject({ action: 'consoleCommand', command: 'say hi there' });
  });

  it('edits a custom schedule and shows what the panel refuses', async () => {
    fakeApi({
      'GET /api/v1/auth/session': session(manager),
      [`GET /api/v1/servers/${serverId}`]: () => json(details),
      [`GET ${schedulesPath}`]: () => json({ tasks: [task({ cron: '30 5 * * 1-5' })], abilities }),
      [`GET ${schedulesPath}/preview`]: (request) =>
        new URL(request.url).searchParams.get('cron') === '30 5 * * 1-5'
          ? json(preview)
          : problem(400, 'schedules.cron_invalid'),
      [`PUT ${schedulesPath}/${taskId}`]: () => problem(403, 'schedules.forbidden'),
    });
    renderAt(`/servers/${serverId}?tab=schedules`);

    await userEvent.click(await screen.findByRole('button', { name: 'Edit Nightly restart' }));
    const dialog = await screen.findByRole('dialog', { name: 'Edit Nightly restart' });
    const cron = within(dialog).getByLabelText('Cron expression');
    expect(cron).toHaveValue('30 5 * * 1-5');
    await userEvent.type(cron, ' x');
    expect(await within(dialog).findByText(/not a cron expression of five fields/)).toBeInTheDocument();
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save' }));

    expect(
      await within(dialog).findByText('You may schedule only what you may do yourself on this server.'),
    ).toBeInTheDocument();
  });

  it('edits a task in a time zone the browser does not know, showing its runs in UTC', async () => {
    fakeApi({
      'GET /api/v1/auth/session': session(manager),
      [`GET /api/v1/servers/${serverId}`]: () => json(details),
      [`GET ${schedulesPath}`]: () => json({ tasks: [task({ timeZone: 'Pulsar/Nowhere' })], abilities }),
      [`GET ${schedulesPath}/preview`]: () => json(preview),
    });
    renderAt(`/servers/${serverId}?tab=schedules`);

    await userEvent.click(await screen.findByRole('button', { name: 'Edit Nightly restart' }));
    const dialog = await screen.findByRole('dialog', { name: 'Edit Nightly restart' });

    expect(
      await within(dialog).findByText(
        'Your browser does not know the time zone Pulsar/Nowhere: the next runs are shown in UTC.',
      ),
    ).toBeInTheDocument();
    expect(await within(dialog).findByText(/October 4, 2026 at 8:30/)).toBeInTheDocument();
  });

  it('runs a task now and shows its history', async () => {
    const ran: string[] = [];
    fakeApi({
      'GET /api/v1/auth/session': session(manager),
      [`GET /api/v1/servers/${serverId}`]: () => json(details),
      [`GET ${schedulesPath}`]: () => json({ tasks: [task()], abilities }),
      [`POST ${schedulesPath}/${taskId}/run`]: () => {
        ran.push('run');
        return json(run());
      },
      [`GET ${schedulesPath}/${taskId}/runs`]: () =>
        json({
          runs: [
            run(),
            run({ id: '0192f1a0-0000-7000-8000-0000000000c2', status: 'failed', errorCode: 'server.busy' }),
            run({ id: '0192f1a0-0000-7000-8000-0000000000c3', status: 'skipped', errorCode: 'schedules.not_running' }),
          ],
        }),
    });
    renderAt(`/servers/${serverId}?tab=schedules`);

    await userEvent.click(await screen.findByRole('button', { name: 'Run Nightly restart now' }));
    await waitFor(() => expect(ran).toEqual(['run']));
    await userEvent.click(screen.getByRole('button', { name: 'History of Nightly restart' }));
    const dialog = await screen.findByRole('dialog', { name: 'Runs of Nightly restart' });
    expect(await within(dialog).findByText('The server was not running.')).toBeInTheDocument();
    expect(within(dialog).getByText('Failed')).toBeInTheDocument();
    expect(within(dialog).getAllByText('Done')).toHaveLength(1);
  });

  it('builds a task of several steps from a preset and sends its steps in order', async () => {
    const saved: unknown[] = [];
    fakeApi({
      'GET /api/v1/auth/session': session(operator),
      [`GET /api/v1/servers/${serverId}`]: () => json(details),
      [`GET ${schedulesPath}`]: () => json({ tasks: [], abilities }),
      [`GET ${schedulesPath}/preview`]: () => json(preview),
      [`POST ${schedulesPath}`]: async (request) => {
        saved.push(await request.json());
        return json(task(), 201);
      },
    });
    renderAt(`/servers/${serverId}?tab=schedules`);
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'New task' }));
    const dialog = await screen.findByRole('dialog', { name: 'New scheduled task' });
    // Several steps start as a restart with warnings.
    await user.selectOptions(within(dialog).getByLabelText('Action'), 'sequence');
    expect(within(dialog).getByLabelText('Step 1')).toHaveValue('broadcast');
    expect(within(dialog).getAllByLabelText('Message')[0]).toHaveValue('Restarting in 5 min');
    expect(within(dialog).getByLabelText('Step 7')).toHaveValue('restart');

    await user.selectOptions(within(dialog).getByLabelText('Start from a preset'), 'update');
    expect(within(dialog).getByLabelText('Name')).toHaveValue('Game update');
    expect(within(dialog).getByLabelText('Only when a game update is available')).toBeChecked();
    expect(within(dialog).getAllByLabelText('Message')[2]).toHaveValue('Restarting in 10 s for a game update');
    expect(within(dialog).getByLabelText('Step 9')).toHaveValue('start');

    // The first warning goes, the update verifies every file, and a last message is added (empty: refused until written).
    await user.click(within(dialog).getByRole('button', { name: 'Remove step 1' }));
    await user.click(within(dialog).getByRole('button', { name: 'Remove step 1' }));
    await user.click(within(dialog).getByLabelText('Verify every file'));
    await user.click(within(dialog).getByRole('button', { name: 'Add a step' }));
    await user.click(within(dialog).getByRole('button', { name: 'Move step 8 up' }));
    await user.click(within(dialog).getByRole('button', { name: 'Save' }));
    expect(await within(dialog).findByText('A message of 1 to 128 characters.')).toBeInTheDocument();
    expect(saved).toHaveLength(0);
    const last = within(dialog).getAllByLabelText('Message').at(-1);
    expect(last).toHaveValue('');
    await user.type(last as HTMLElement, 'Back soon');
    await user.click(within(dialog).getByRole('button', { name: 'Save' }));

    await waitFor(() => expect(saved).toHaveLength(1));
    const step = (kind: string, changes: object = {}) => ({
      kind,
      text: null,
      seconds: 0,
      validate: false,
      onlyIfWasRunning: false,
      always: false,
      ...changes,
    });
    expect(saved[0]).toMatchObject({
      name: 'Game update',
      action: 'sequence',
      onlyIfUpdateAvailable: true,
      onlyIfEmpty: false,
      steps: [
        step('broadcast', { text: 'Restarting in 1 min for a game update' }),
        step('wait', { seconds: 50 }),
        step('broadcast', { text: 'Restarting in 10 s for a game update' }),
        step('wait', { seconds: 10 }),
        step('stop'),
        step('update', { validate: true }),
        step('broadcast', { text: 'Back soon' }),
        step('start', { onlyIfWasRunning: true, always: true }),
      ],
    });
  });

  it('shows the step a run is doing and cancels it', async () => {
    const cancelled: string[] = [];
    vi.spyOn(window, 'confirm').mockReturnValue(true);
    const steps: Schemas['SequenceStep'][] = [
      {
        kind: 'broadcast',
        text: 'Restarting in 5 min',
        seconds: 0,
        validate: false,
        onlyIfWasRunning: false,
        always: false,
      },
      { kind: 'wait', text: null, seconds: 300, validate: false, onlyIfWasRunning: false, always: false },
      { kind: 'restart', text: null, seconds: 0, validate: false, onlyIfWasRunning: false, always: false },
    ];
    const running = run({
      status: 'running',
      finishedAt: null,
      sequenceId: '0192f1a0-0000-7000-8000-0000000000b1',
      step: 1,
      steps: 3,
      stepKind: 'wait',
    });
    fakeApi({
      'GET /api/v1/auth/session': session(operator),
      [`GET /api/v1/servers/${serverId}`]: () => json(details),
      [`GET ${schedulesPath}`]: () =>
        json({
          abilities,
          tasks: [
            task({ name: 'Restart with warnings', action: 'sequence', steps, onlyIfEmpty: true, lastRun: running }),
          ],
        }),
      [`POST ${schedulesPath}/${taskId}/cancel`]: () => {
        cancelled.push('cancel');
        return json(running);
      },
    });
    renderAt(`/servers/${serverId}?tab=schedules`);

    await screen.findByRole('table', { name: 'Scheduled tasks' });
    expect(row('Restart with warnings')).toHaveTextContent(
      '3 steps: message → wait → restart · only when nobody plays',
    );
    expect(row('Restart with warnings')).toHaveTextContent('Step 2 of 3 (waiting)');
    expect(screen.queryByRole('button', { name: 'Run Restart with warnings now' })).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Cancel what Restart with warnings is doing' }));
    await waitFor(() => expect(cancelled).toEqual(['cancel']));
  });

  it('offers only the steps and conditions the user and the game allow', async () => {
    const steps: Schemas['SequenceStep'][] = [
      { kind: 'broadcast', text: 'Restarting', seconds: 0, validate: false, onlyIfWasRunning: false, always: false },
      { kind: 'restart', text: null, seconds: 0, validate: false, onlyIfWasRunning: false, always: false },
    ];
    fakeApi({
      'GET /api/v1/auth/session': session(manager),
      [`GET /api/v1/servers/${serverId}`]: () => json(details),
      [`GET ${schedulesPath}`]: () =>
        json({
          abilities: { canBroadcast: true, knowsPlayers: false, updatesChecked: false },
          tasks: [task({ name: 'Restart with warnings', action: 'sequence', steps })],
        }),
      [`GET ${schedulesPath}/preview`]: () => json(preview),
    });
    renderAt(`/servers/${serverId}?tab=schedules`);
    const user = userEvent.setup();

    // Messaging the players needs the console: this user may not change the task that does.
    await screen.findByRole('table', { name: 'Scheduled tasks' });
    expect(screen.queryByRole('button', { name: 'Edit Restart with warnings' })).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'New task' }));
    const dialog = await screen.findByRole('dialog', { name: 'New scheduled task' });
    await user.selectOptions(within(dialog).getByLabelText('Action'), 'sequence');
    // No warnings in the preset, and no step that needs what the user may not do.
    expect(within(dialog).getByLabelText('Step 1')).toHaveValue('restart');
    expect(within(dialog).queryByLabelText('Step 2')).not.toBeInTheDocument();
    const kinds = within(within(dialog).getByLabelText('Step 1'))
      .getAllByRole('option')
      .map((o) => o.textContent);
    expect(kinds).toEqual(['Wait', 'Restart the server', 'Back up']);
    expect(
      within(within(dialog).getByLabelText('Start from a preset'))
        .getAllByRole('option')
        .map((o) => o.textContent),
    ).toEqual(['Choose a preset…', 'Restart with warnings (5 min, 1 min, 10 s)']);
    // The game does not say who plays, and its updates are not checked.
    expect(within(dialog).getByLabelText('Only while the server is running')).toBeInTheDocument();
    expect(within(dialog).queryByLabelText('Only when nobody is playing')).not.toBeInTheDocument();
    expect(within(dialog).queryByLabelText('Only when a game update is available')).not.toBeInTheDocument();
  });

  it('shows only what a viewer may do', async () => {
    fakeApi({
      'GET /api/v1/auth/session': session(viewer),
      [`GET /api/v1/servers/${serverId}`]: () => json(details),
      [`GET ${schedulesPath}`]: () => json(tasks),
    });

    renderAt(`/servers/${serverId}?tab=schedules`);

    await screen.findByRole('table', { name: 'Scheduled tasks' });
    expect(screen.getByRole('button', { name: 'History of Nightly restart' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'New task' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /^Run / })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /^Edit / })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /^Delete / })).not.toBeInTheDocument();
  });

  it('is hidden without the permission to view schedules', async () => {
    fakeApi({
      'GET /api/v1/auth/session': session({ ...adminUser, permissions: ['server.view'] }),
      [`GET /api/v1/servers/${serverId}`]: () => json(details),
    });

    renderAt(`/servers/${serverId}?tab=schedules`);

    expect(await screen.findByRole('tab', { name: 'Overview' })).toHaveAttribute('aria-selected', 'true');
    expect(screen.queryByRole('tab', { name: 'Schedules' })).not.toBeInTheDocument();
  });
});

describe('audit log', () => {
  it('names the task that acted', async () => {
    const entry = (changes: Partial<Schemas['AuditEntryView']>): Schemas['AuditEntryView'] => ({
      id: '0192f1a0-0000-7000-8000-0000000000a9',
      occurredAt: '2026-10-03T12:20:00Z',
      action: 'server.restart',
      outcome: 'success',
      actorType: 'schedule',
      actorUserId: null,
      actorDisplay: 'Nightly restart',
      targetType: null,
      targetId: null,
      ipAddress: null,
      metadata: {},
      ...changes,
    });
    fakeApi({
      'GET /api/v1/auth/session': session({ ...adminUser, permissions: ['audit.view'] }),
      'GET /api/v1/audit': () =>
        json({
          entries: [
            entry({}),
            entry({ id: '0192f1a0-0000-7000-8000-0000000000aa', actorType: 'system', actorDisplay: 'system' }),
          ],
          nextBefore: null,
        }),
    });

    renderAt('/admin/audit');

    expect(await screen.findByText('Schedule: Nightly restart')).toBeInTheDocument();
    expect(screen.getByText('System')).toBeInTheDocument();
  });
});
