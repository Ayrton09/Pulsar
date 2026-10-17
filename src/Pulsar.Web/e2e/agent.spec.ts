import { expect, test } from '@playwright/test';
import { execFileSync, spawn, type ChildProcess } from 'node:child_process';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { signInAsAdministrator, watchCspViolations } from './session';

// The Phase 2 "done" scenario (docs/architecture/09) with the real PulsarAgent.exe: the node is created in the UI,
// the command it shows enrolls the agent, the node comes online with its inventory, answers a signed ping, and
// revoking its credential cuts it off. Development mode: user key store, no service, no administrator rights.
// (Reconnection after network cuts and session renewal are covered by Pulsar.Api.Tests/Agents/RealAgentTests.)

test.skip(process.platform !== 'win32', 'The agent runs on Windows only.');
test.describe.configure({ mode: 'serial' });

const agentProject = fileURLToPath(new URL('../../Pulsar.Agent/Pulsar.Agent.csproj', import.meta.url));
const agentDir = fileURLToPath(new URL('../../../artifacts/e2e-agent', import.meta.url));
const agentExe = join(agentDir, 'PulsarAgent.exe');
const nodeName = `e2e-node-${Date.now().toString(36)}`;

let dataDir = '';
let agent: ChildProcess | undefined;
let agentOutput = '';

test.beforeAll(() => {
  test.setTimeout(240_000);
  execFileSync('dotnet', ['publish', agentProject, '-c', 'Release', '-o', agentDir], { stdio: 'inherit' });
  dataDir = mkdtempSync(join(tmpdir(), 'pulsar-e2e-agent-'));
});

test.afterAll(() => {
  agent?.kill();
  if (dataDir) {
    // Deletes the node key from the user's key store; the database is disposable.
    try {
      execFileSync(agentExe, ['unenroll', '--data-dir', dataDir], { stdio: 'inherit' });
    } finally {
      rmSync(dataDir, { recursive: true, force: true });
    }
  }
});

function runAgent(args: string[]): string {
  return execFileSync(agentExe, [...args, '--data-dir', dataDir], { encoding: 'utf8' });
}

test('a real agent pairs, comes online, answers pings and is cut off when revoked', async ({ page }) => {
  test.setTimeout(120_000);
  const cspViolations = watchCspViolations(page);
  await signInAsAdministrator(page);

  // Create the node and take the command the UI shows, exactly as an administrator would copy it.
  await page.getByRole('link', { name: 'Nodes', exact: true }).click();
  await page.getByRole('button', { name: 'Add node' }).click();
  const createDialog = page.getByRole('dialog', { name: 'Add node' });
  await createDialog.getByLabel('Name').fill(nodeName);
  await createDialog.getByRole('button', { name: 'Create' }).click();
  const enrollDialog = page.getByRole('dialog', { name: `Enroll ${nodeName}` });
  // The node code for PulsarAgentSetup.exe comes first; the script's command is the advanced path.
  await expect(enrollDialog.getByLabel('Node code', { exact: true })).toContainText('pulsar-node-1.');
  await enrollDialog.getByRole('button', { name: 'Advanced: install with PowerShell' }).click();
  const command = (await enrollDialog.getByLabel('Enrollment command', { exact: true }).textContent()) ?? '';
  await page.screenshot({ path: 'test-results/screens/node-enroll.png', fullPage: true });
  // The installer's command; the test runs the development agent with the same values.
  const match =
    /^powershell -ExecutionPolicy Bypass -File \.\\Install-Pulsar\.ps1 -Mode Agent -PanelUrl (\S+) -Token (\S+) -PanelKey (\S+)$/.exec(
      command.trim(),
    );
  expect(match, `enrollment command: ${command}`).not.toBeNull();
  const [, panel = '', token = '', panelKey = ''] = match ?? [];

  const enrolled = runAgent(['enroll', '--panel', panel, '--token', token, '--panel-key', panelKey, '--dev']);
  expect(enrolled).toContain(`Enrolled with ${panel}`);

  agent = spawn(agentExe, ['run', '--data-dir', dataDir]);
  agent.stdout?.on('data', (chunk: Buffer) => (agentOutput += chunk.toString()));
  agent.stderr?.on('data', (chunk: Buffer) => (agentOutput += chunk.toString()));

  // The node page shows the agent online with what it reported about this machine.
  await enrollDialog.getByRole('link', { name: 'Open the node page' }).click();
  await expect(page.getByRole('heading', { name: nodeName })).toBeVisible();
  await expect(page.getByText('Online', { exact: true })).toBeVisible({ timeout: 20_000 });
  // COMPUTERNAME is the NetBIOS name, which is what the agent reports (Environment.MachineName).
  await expect(page.getByText(process.env.COMPUTERNAME ?? 'missing', { exact: true })).toBeVisible();
  await expect(page.getByRole('meter', { name: 'CPU' })).toBeVisible();
  // The credential the panel shows is the key the agent holds.
  const status = runAgent(['status']);
  const fingerprint = /Node key:\s+(sha256:\S+)/.exec(status)?.[1] ?? 'missing';
  await expect(page.getByText(fingerprint)).toBeVisible();

  // A signed command makes the full round trip.
  await page.getByRole('button', { name: 'Test connection' }).click();
  await expect(page.getByText(/^Answered in \d+ ms$/)).toBeVisible();
  await page.screenshot({ path: 'test-results/screens/node-online.png', fullPage: true });

  // Rotating the key: the agent registers a new one and switches to it; the old credential is replaced.
  await page.getByRole('button', { name: 'Rotate key' }).click();
  await expect(page.getByText('Replaced', { exact: true })).toBeVisible({ timeout: 20_000 });
  const rotatedStatus = runAgent(['status']);
  const rotatedFingerprint = /Node key:\s+(sha256:\S+)/.exec(rotatedStatus)?.[1] ?? 'missing';
  expect(rotatedFingerprint).not.toBe(fingerprint);
  await expect(page.getByRole('row', { name: new RegExp(rotatedFingerprint) })).toContainText('Active');
  await expect(page.getByText('Online', { exact: true })).toBeVisible({ timeout: 20_000 });
  await page.screenshot({ path: 'test-results/screens/node-rotated.png', fullPage: true });

  // Revoking the credential disconnects the agent at once, and it cannot come back.
  await page.getByRole('button', { name: 'Revoke' }).click();
  const revokeDialog = page.getByRole('dialog', { name: 'Revoke this credential?' });
  await revokeDialog.getByLabel('Reason (optional)').fill('E2E');
  await revokeDialog.getByRole('button', { name: 'Revoke' }).click();
  await expect(page.getByText('Offline', { exact: true })).toBeVisible({ timeout: 15_000 });
  await expect(page.getByText('Revoked', { exact: true })).toBeVisible();
  await expect.poll(() => agentOutput, { timeout: 15_000 }).toContain('The panel refused this node');
  await page.screenshot({ path: 'test-results/screens/node-revoked.png', fullPage: true });

  expect(cspViolations, 'Content Security Policy violations').toEqual([]);
});
