import { expect, test, type Page } from '@playwright/test';
import { execFileSync, spawn, type ChildProcess } from 'node:child_process';
import { existsSync, mkdtempSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { signInAsAdministrator, watchCspViolations } from './session';

// The Phase 3 "done" scenario (docs/architecture/09) with the real PulsarAgent.exe and Pulsar's test game server: a
// server is created, started, restarted, stopped, killed and deleted from the panel, and no process is left behind.
// Development mode: the agent runs as the current user, so the server does too (dedicated accounts need the agent
// service; they are checked by scripts/sandbox/agent-install and the SYSTEM tests in CI).

test.skip(process.platform !== 'win32', 'The agent runs on Windows only.');
test.describe.configure({ mode: 'serial' });

const agentProject = fileURLToPath(new URL('../../Pulsar.Agent/Pulsar.Agent.csproj', import.meta.url));
const testGameProject = fileURLToPath(
  new URL('../../../tests/Pulsar.TestGameServer/Pulsar.TestGameServer.csproj', import.meta.url),
);
const agentDir = fileURLToPath(new URL('../../../artifacts/e2e-agent', import.meta.url));
const testGameDir = fileURLToPath(new URL('../../../artifacts/e2e-testgame', import.meta.url));
const agentExe = join(agentDir, 'PulsarAgent.exe');
const suffix = Date.now().toString(36);
const nodeName = `e2e-servers-${suffix}`;
const serverName = `E2E server ${suffix}`;

let dataDir = '';
let agent: ChildProcess | undefined;
let agentOutput = '';

test.beforeAll(() => {
  test.setTimeout(300_000);
  execFileSync('dotnet', ['publish', agentProject, '-c', 'Release', '-o', agentDir], { stdio: 'inherit' });
  execFileSync('dotnet', ['publish', testGameProject, '-c', 'Release', '-o', testGameDir], { stdio: 'inherit' });
  dataDir = mkdtempSync(join(tmpdir(), 'pulsar-e2e-servers-'));
});

test.afterAll(() => {
  agent?.kill();
  if (dataDir) {
    try {
      execFileSync(agentExe, ['unenroll', '--data-dir', dataDir], { stdio: 'inherit' });
    } finally {
      rmSync(dataDir, { recursive: true, force: true });
    }
  }
});

/** Test game server processes running on this machine. */
function testGameProcesses(): number {
  const list = execFileSync('tasklist', ['/FI', 'IMAGENAME eq Pulsar.TestGameServer.exe', '/FO', 'CSV', '/NH'], {
    encoding: 'utf8',
  });
  return list.split('\n').filter((line) => line.includes('Pulsar.TestGameServer.exe')).length;
}

async function expectStatus(page: Page, status: string) {
  await expect(page.locator('header').getByText(status, { exact: true })).toBeVisible({ timeout: 30_000 });
}

test('a server is created, started, restarted, stopped, killed and deleted from the panel', async ({ page }) => {
  // Phase 8 waits for the scheduler's next minute.
  test.setTimeout(270_000);
  const cspViolations = watchCspViolations(page);
  page.on('dialog', (dialog) => void dialog.accept());
  await signInAsAdministrator(page);

  // A node with a development agent that has the test game server.
  await page.getByRole('link', { name: 'Nodes', exact: true }).click();
  await page.getByRole('button', { name: 'Add node' }).click();
  const createNode = page.getByRole('dialog', { name: 'Add node' });
  await createNode.getByLabel('Name').fill(nodeName);
  await createNode.getByRole('button', { name: 'Create' }).click();
  await page.getByRole('button', { name: 'Advanced: install with PowerShell' }).click();
  const command = (await page.getByLabel('Enrollment command', { exact: true }).textContent()) ?? '';
  const [, panel = '', token = '', panelKey = ''] = /-PanelUrl (\S+) -Token (\S+) -PanelKey (\S+)/.exec(command) ?? [];
  execFileSync(agentExe, [
    'enroll',
    '--panel',
    panel,
    '--token',
    token,
    '--panel-key',
    panelKey,
    '--dev',
    '--data-dir',
    dataDir,
  ]);
  agent = spawn(agentExe, ['run', '--data-dir', dataDir], {
    env: { ...process.env, Pulsar__TestGameDirectory: testGameDir },
  });
  agent.stdout?.on('data', (chunk: Buffer) => (agentOutput += chunk.toString()));
  agent.stderr?.on('data', (chunk: Buffer) => (agentOutput += chunk.toString()));
  await page.getByRole('link', { name: 'Open the node page' }).click();
  await expect(page.getByText('Online', { exact: true })).toBeVisible({ timeout: 20_000 });

  // Create: the panel provisions and installs it on the node.
  await page.getByRole('link', { name: 'Servers', exact: true }).click();
  await page.getByRole('button', { name: 'Create server' }).click();
  const createServer = page.getByRole('dialog', { name: 'Create server' });
  await createServer.getByLabel('Name').fill(serverName);
  await createServer.getByLabel('Node').selectOption({ label: nodeName });
  await createServer.getByLabel('Game').selectOption({ label: 'Pulsar test server 1.0.0' });
  // The wizard: the test game's settings keep their defaults; it listens on no port.
  await createServer.getByRole('button', { name: 'Next' }).click();
  await expect(createServer.getByLabel('Startup time (ms)')).toHaveValue('1000');
  await createServer.getByRole('button', { name: 'Next' }).click();
  await createServer.getByRole('button', { name: 'Create' }).click();
  await expect(page.getByRole('heading', { name: serverName })).toBeVisible();
  await expectStatus(page, 'Offline');
  const operations = page.getByRole('table', { name: 'Recent operations' });
  await expect(operations.getByRole('row', { name: /Install/ })).toContainText('Done');
  await expect(operations.getByRole('row', { name: /Prepare on the node/ })).toContainText('Done');

  // Start: the real test game server runs until it reports it is ready.
  const actions = page.getByRole('region', { name: 'Server actions' });
  await actions.getByRole('button', { name: 'Start', exact: true }).click();
  await expectStatus(page, 'Online');
  expect(testGameProcesses()).toBe(1);
  await page.screenshot({ path: 'test-results/screens/server-online.png', fullPage: true });

  // Phase 5: usage pushed by the agent, and the console in a real terminal (xterm) with a command typed here.
  await expect(page.getByLabel('Usage')).toContainText('Uptime', { timeout: 15_000 });
  await page.getByRole('tab', { name: 'Console' }).click();
  const output = page.getByTestId('console-output');
  await expect(output).toContainText('Test server ready', { timeout: 15_000 });
  const commandField = page.getByRole('textbox', { name: 'Command' });
  await commandField.fill('say hello from the browser');
  await commandField.press('Enter');
  await expect(output).toContainText('Test server: hello from the browser', { timeout: 15_000 });
  await expect(commandField).toHaveValue('');
  await page.screenshot({ path: 'test-results/screens/server-console.png', fullPage: true });
  await page.getByRole('tab', { name: 'Overview' }).click();

  // Restart: a graceful stop (quit, exit code 0) and a new start, as one unit.
  await actions.getByRole('button', { name: 'Restart' }).click();
  await expect
    .poll(() => agentOutput, { timeout: 30_000 })
    .toMatch(/Stopping → Offline[\s\S]*Offline → Starting[\s\S]*Starting → Running/);
  await expectStatus(page, 'Online');

  // Stop.
  await actions.getByRole('button', { name: 'Stop' }).click();
  await expectStatus(page, 'Offline');
  await expect.poll(testGameProcesses, { timeout: 10_000 }).toBe(0);

  // Kill: the whole tree dies at once.
  await actions.getByRole('button', { name: 'Start', exact: true }).click();
  await expectStatus(page, 'Online');
  await actions.getByRole('button', { name: 'Kill' }).click();
  await expectStatus(page, 'Offline');
  await expect(page.getByText('Killed.', { exact: true }).first()).toBeVisible();
  await expect.poll(testGameProcesses, { timeout: 10_000 }).toBe(0);
  await page.screenshot({ path: 'test-results/screens/server-killed.png', fullPage: true });

  // Phase 6: the file manager. A folder and a file created here, edited in the real editor (CodeMirror) and saved to
  // the node; an upload, a download, a rename and a deletion.
  await page.getByRole('tab', { name: 'Files' }).click();
  const files = page.getByRole('table', { name: 'Files' });
  await expect(files.getByRole('button', { name: 'bin/' })).toBeVisible();
  await page.getByRole('button', { name: 'New folder' }).click();
  await page.getByRole('dialog', { name: 'New folder' }).getByLabel('Name').fill('cfg');
  await page.getByRole('dialog', { name: 'New folder' }).getByRole('button', { name: 'Create' }).click();
  await files.getByRole('button', { name: 'cfg/' }).click();
  await page.getByRole('button', { name: 'New file' }).click();
  await page.getByRole('dialog', { name: 'New file' }).getByLabel('Name').fill('server.cfg');
  await page.getByRole('dialog', { name: 'New file' }).getByRole('button', { name: 'Create' }).click();
  const editor = page.getByRole('textbox', { name: 'Contents of server.cfg' });
  await editor.click();
  await page.keyboard.type('hostname "E2E" // edited in the browser');
  await expect(page.getByText('Unsaved changes')).toBeVisible();
  await page.keyboard.press('Control+s');
  await expect(page.getByText('All changes saved')).toBeVisible();
  await page.screenshot({ path: 'test-results/screens/server-file-editor.png', fullPage: true });
  const serverDir = join(dataDir, 'servers', readdirSync(join(dataDir, 'servers'))[0] ?? '');
  expect(readFileSync(join(serverDir, 'cfg', 'server.cfg'), 'utf8')).toBe('hostname "E2E" // edited in the browser');
  await page.getByRole('button', { name: 'Close' }).click();

  await page
    .getByLabel('Upload')
    .setInputFiles({ name: 'motd.txt', mimeType: 'text/plain', buffer: Buffer.from('Welcome') });
  await expect(page.getByText('Uploaded')).toBeVisible();
  const downloading = page.waitForEvent('download');
  await files.getByRole('link', { name: 'Download motd.txt' }).click();
  const download = await downloading;
  expect(download.suggestedFilename()).toBe('motd.txt');
  expect(readFileSync(await download.path(), 'utf8')).toBe('Welcome');
  await files.getByRole('button', { name: 'Rename motd.txt' }).click();
  await page.getByRole('dialog', { name: 'Rename motd.txt' }).getByLabel('Name').fill('welcome.txt');
  await page.getByRole('dialog', { name: 'Rename motd.txt' }).getByRole('button', { name: 'Rename' }).click();
  await files.getByRole('button', { name: 'Delete welcome.txt' }).click();
  await expect(files.getByText('welcome.txt')).toHaveCount(0);
  await expect(files.getByRole('button', { name: 'server.cfg', exact: true })).toBeVisible();
  await page.screenshot({ path: 'test-results/screens/server-files.png', fullPage: true });

  // Phase 7: a backup made on the node (the list updates live when it completes), downloaded, then restored over a
  // changed file.
  await page.getByRole('tab', { name: 'Backups' }).click();
  await page.getByRole('button', { name: 'Create backup' }).click();
  const createBackup = page.getByRole('dialog', { name: 'Create a backup' });
  await createBackup.getByLabel('Name').fill('Before the edit');
  await createBackup.getByRole('button', { name: 'Create backup' }).click();
  const backups = page.getByRole('table', { name: 'Backups' });
  const backupRow = backups.getByRole('row', { name: /Before the edit/ });
  await expect(backupRow).toContainText('Ready', { timeout: 30_000 });
  await expect(backupRow).toContainText(/\d+ files · \d+ s/);
  const savingBackup = page.waitForEvent('download');
  await backups.getByRole('link', { name: 'Download Before the edit' }).click();
  const backupFile = await savingBackup;
  expect(backupFile.suggestedFilename()).toMatch(/^E2E server .+-\d{8}-\d{6}\.zip$/);
  expect(
    readFileSync(await backupFile.path())
      .subarray(0, 2)
      .toString(),
  ).toBe('PK');
  // Phase 15: restored clean, the file made after the backup goes too.
  writeFileSync(join(serverDir, 'cfg', 'server.cfg'), 'hostname "changed after the backup"');
  writeFileSync(join(serverDir, 'cfg', 'later.cfg'), 'made after the backup');
  await backups.getByRole('button', { name: 'Restore Before the edit' }).click();
  const restoreBackup = page.getByRole('dialog', { name: 'Restore Before the edit' });
  await restoreBackup.getByRole('checkbox', { name: 'Delete the files made after this backup' }).check();
  await restoreBackup.getByRole('button', { name: 'Restore' }).click();
  await expect
    .poll(() => readFileSync(join(serverDir, 'cfg', 'server.cfg'), 'utf8'), { timeout: 30_000 })
    .toBe('hostname "E2E" // edited in the browser');
  await expect.poll(() => existsSync(join(serverDir, 'cfg', 'later.cfg')), { timeout: 30_000 }).toBe(false);
  await page.screenshot({ path: 'test-results/screens/server-backups.png', fullPage: true });
  await page.getByRole('tab', { name: 'Overview' }).click();
  await expect(operations.getByRole('row', { name: /Restore/ })).toContainText('Done');
  await expect(operations.getByRole('row', { name: /Backup/ })).toContainText('Done');

  // Phase 8: a task every minute, run by the panel's scheduler on its own (it wakes up when the task is saved, then at
  // the minute), sends a command the game answers in its console.
  await actions.getByRole('button', { name: 'Start', exact: true }).click();
  await expectStatus(page, 'Online');

  // Phase 15: a backup of the running game asks it to save first (pulsar-test's backup.save), then reads a snapshot
  // of its disk; a development agent is no administrator, so it reads the files live and says why.
  await page.getByRole('tab', { name: 'Backups' }).click();
  await page.getByRole('button', { name: 'Create backup' }).click();
  const runningBackup = page.getByRole('dialog', { name: 'Create a backup' });
  await expect(runningBackup).toContainText('The server is running');
  await runningBackup.getByLabel('Name').fill('While running');
  await runningBackup.getByRole('button', { name: 'Create backup' }).click();
  const runningRow = backups.getByRole('row', { name: /While running/ });
  await expect(runningRow).toContainText('Ready', { timeout: 60_000 });
  await expect(runningRow).toContainText(
    /Copied (from a snapshot of the disk|while the game ran, without a snapshot \(.+\)) · the game saved first/,
  );
  await page.getByRole('tab', { name: 'Console' }).click();
  await expect(page.getByTestId('console-output')).toContainText('Test server: autosave on', { timeout: 15_000 });
  await page.getByRole('tab', { name: 'Schedules' }).click();
  await page.getByRole('button', { name: 'New task' }).click();
  const newTask = page.getByRole('dialog', { name: 'New scheduled task' });
  await newTask.getByLabel('Name').fill('Every minute');
  await newTask.getByLabel('Action').selectOption({ label: 'Console command' });
  await newTask.getByLabel('Command').fill('say from the schedule');
  await newTask.getByLabel('Repeat').selectOption({ label: 'Custom (cron)' });
  await newTask.getByLabel('Cron expression').fill('* * * * *');
  await expect(newTask.getByRole('listitem')).toHaveCount(5);
  await newTask.getByRole('button', { name: 'Save' }).click();
  const taskRow = page.getByRole('table', { name: 'Scheduled tasks' }).getByRole('row', { name: /Every minute/ });
  await expect(taskRow).toContainText('Never');
  // The run shows up live (the live channel refreshes the list when it ends).
  await expect(taskRow).toContainText('Done', { timeout: 75_000 });
  await page.screenshot({ path: 'test-results/screens/server-schedules.png', fullPage: true });
  await page.getByRole('button', { name: 'History of Every minute' }).click();
  await expect(page.getByRole('dialog', { name: 'Runs of Every minute' }).getByText('Done').first()).toBeVisible();
  await page.getByRole('button', { name: 'Close' }).click();
  await page.getByRole('tab', { name: 'Console' }).click();
  await expect(page.getByTestId('console-output')).toContainText('Test server: from the schedule', { timeout: 15_000 });
  await page.getByRole('tab', { name: 'Overview' }).click();

  // Delete: its folder and its backups go with it.
  await page.getByRole('button', { name: 'Delete' }).click();
  await expect(page.getByRole('heading', { name: 'Servers' })).toBeVisible();
  await expect(page.getByRole('link', { name: serverName })).toHaveCount(0, { timeout: 30_000 });
  await expect.poll(() => agentOutput, { timeout: 15_000 }).toContain('deleted from this node');
  expect(readdirSync(join(dataDir, 'backups'))).toEqual([]);

  // The game runs from the shared test game folder, not the server's: it ends with its host's Job, a moment after the
  // host itself, so the count is waited for rather than read once.
  await expect.poll(() => testGameProcesses(), { timeout: 10_000 }).toBe(0);
  expect(agentOutput).not.toMatch(/\] (ERR|FTL)/);
  expect(cspViolations, 'Content Security Policy violations').toEqual([]);
});
