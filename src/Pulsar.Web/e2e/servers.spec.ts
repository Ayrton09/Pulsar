import { expect, test, type Page } from '@playwright/test';
import { execFileSync, spawn, type ChildProcess } from 'node:child_process';
import { mkdtempSync, rmSync } from 'node:fs';
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
  test.setTimeout(180_000);
  const cspViolations = watchCspViolations(page);
  page.on('dialog', (dialog) => void dialog.accept());
  await signInAsAdministrator(page);

  // A node with a development agent that has the test game server.
  await page.getByRole('link', { name: 'Nodes', exact: true }).click();
  await page.getByRole('button', { name: 'Add node' }).click();
  const createNode = page.getByRole('dialog', { name: 'Add node' });
  await createNode.getByLabel('Name').fill(nodeName);
  await createNode.getByRole('button', { name: 'Create' }).click();
  const command = (await page.getByLabel('Enrollment command').textContent()) ?? '';
  const [, panel = '', token = '', panelKey = ''] = /--panel (\S+) --token (\S+) --panel-key (\S+)/.exec(command) ?? [];
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

  // Delete: its folder goes with it.
  await page.getByRole('button', { name: 'Delete' }).click();
  await expect(page.getByRole('heading', { name: 'Servers' })).toBeVisible();
  await expect(page.getByRole('link', { name: serverName })).toHaveCount(0, { timeout: 30_000 });
  await expect.poll(() => agentOutput, { timeout: 15_000 }).toContain('deleted from this node');

  expect(testGameProcesses()).toBe(0);
  expect(agentOutput).not.toMatch(/\] (ERR|FTL)/);
  expect(cspViolations, 'Content Security Policy violations').toEqual([]);
});
