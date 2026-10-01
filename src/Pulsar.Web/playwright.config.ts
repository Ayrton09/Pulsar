import { defineConfig, devices } from '@playwright/test';
import { fileURLToPath } from 'node:url';
import { E2E_SETUP_TOKEN } from './e2e/constants';

// End-to-end tests run against the *published* panel serving the built SPA (run `npm run build` first).
// Publishing matters: `dotnet run` outside Development does not serve the precompressed static assets.
// The port differs from the dev profile (5180) so a running dev panel is never reused by mistake.
const port = Number(process.env.PULSAR_E2E_PORT ?? 5190);
const baseURL = `http://127.0.0.1:${port}`;
const apiProject = fileURLToPath(new URL('../Pulsar.Api/Pulsar.Api.csproj', import.meta.url));
const publishDir = fileURLToPath(new URL('../../artifacts/e2e-panel', import.meta.url));

// The panel needs PostgreSQL: a connection string to a database used only by these tests (its content is disposable).
const connectionString = process.env.PULSAR_E2E_POSTGRES;
if (!connectionString) {
  throw new Error('Set PULSAR_E2E_POSTGRES to a connection string for a disposable database (see CONTRIBUTING.md).');
}

export default defineConfig({
  testDir: './e2e',
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  reporter: process.env.CI ? 'github' : 'list',
  use: {
    baseURL,
    trace: 'retain-on-failure',
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
  webServer: {
    command:
      `dotnet publish "${apiProject}" -c Release -o "${publishDir}"` +
      ` && dotnet "${publishDir}/Pulsar.Api.dll" --contentRoot "${publishDir}" --urls ${baseURL}`,
    // Plain HTTP on 127.0.0.1 (Security:AllowInsecureHttp); the HTTPS cookie mode is covered by the API tests.
    env: {
      ConnectionStrings__Pulsar: connectionString,
      InitialSetup__Token: E2E_SETUP_TOKEN,
      Security__AllowInsecureHttp: 'true',
    },
    url: `${baseURL}/health/ready`,
    reuseExistingServer: !process.env.CI,
    timeout: 180_000,
  },
});
