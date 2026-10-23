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
// Its own Data Protection key ring: never the machine's default folder (%ProgramData%), which a database kept from an
// earlier run would no longer match once that folder changed.
const keysDir = fileURLToPath(new URL('../../artifacts/e2e-panel-keys', import.meta.url));

// The panel needs PostgreSQL: a connection string to a database used only by these tests (its content is disposable).
const connectionString = process.env.PULSAR_E2E_POSTGRES;
if (!connectionString) {
  throw new Error('Set PULSAR_E2E_POSTGRES to a connection string for a disposable database (see CONTRIBUTING.md).');
}

export default defineConfig({
  testDir: './e2e',
  forbidOnly: !!process.env.CI,
  // Specs share one database and the first to run may perform the initial setup: one worker, in file order.
  workers: 1,
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
      DataProtection__KeysPath: keysDir,
      InitialSetup__Token: E2E_SETUP_TOKEN,
      Security__AllowInsecureHttp: 'true',
      // Offers Pulsar's test game, which e2e/servers.spec.ts runs on a real agent.
      Pulsar__EnableTestGame: 'true',
      // Every spec signs in from the same address, and the two-factor one alone needs a dozen sign-in steps a minute
      // (the limit itself is covered by the API tests).
      RateLimiting__Auth__PermitLimit: '100',
      // Every page load asks for the session, and the web server's readiness is polled while it starts.
      RateLimiting__Auth__AnonymousPermitLimit: '10000',
    },
    url: `${baseURL}/health/ready`,
    reuseExistingServer: !process.env.CI,
    // The publish runs first: a Windows CI runner can take over three minutes for it.
    timeout: 360_000,
  },
});
