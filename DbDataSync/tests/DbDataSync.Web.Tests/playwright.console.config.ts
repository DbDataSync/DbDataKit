import { defineConfig, devices } from '@playwright/test'
import { execFileSync } from 'node:child_process'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { consoleScratch, consoleStorageState } from './console-paths'
import { SA_PASSWORD } from './test-db'

// Sync's own Playwright suite, run through the console (plan 13a, U08): the same specs as `playwright.config.ts`, but the browser opens the console, which is signed in as an administrator and
// has the Sync server paired; the specs' addresses are translated to `/servers/<id>/...` and the relay (see fixtures.ts). Nothing here is Sync's own address except the one the console pairs with.
// Run: `npx playwright test -c playwright.console.config.ts` (the engines, the builds of the API, the worker, the CLI and the console, and the console's web app are as for the direct run;
// `npm run build -w @dbdatakit/console` in DbDataKit/web).
const __dirname = path.dirname(fileURLToPath(import.meta.url))
const repoRoot = path.resolve(__dirname, '../..')
const kitRoot = path.resolve(repoRoot, '..')

process.env.DBDATASYNC_E2E_CONSOLE = '1'

// Only in the process that starts the run: Playwright evaluates this file again in every worker, and clearing a directory then would delete the state a server is using.
const isWorker = process.env.TEST_WORKER_INDEX !== undefined

export const consoleDirectory = path.join(consoleScratch, 'console')
export const syncDirectory = path.join(consoleScratch, 'sync')
// A passkey is bound to the host name and the port it was made on, and the console builds its list of allowed origins from the address it is told to listen on.
const consolePort = 8491
const origin = `https://localhost:${consolePort}`
export const syncUrl = 'https://localhost:8493'
const syncDll = path.join(repoRoot, 'src/DbDataSync.Cli/bin/Debug/net10.0/DbDataSync.Cli.dll')
const consoleDll = path.join(kitRoot, 'DbDataConsole/src/DbDataConsole.Server/bin/Debug/net10.0/dbdatakit.dll')
const certificate = path.join(os.tmpdir(), 'dbdatasync-console-e2e.pfx')
const certificatePassword = 'console-e2e'

if (!isWorker) {
  fs.rmSync(consoleScratch, { recursive: true, force: true })
  fs.mkdirSync(consoleDirectory, { recursive: true })
  fs.mkdirSync(syncDirectory, { recursive: true })
  // DuckDB is installed the way a deployment gets it (see playwright.config.ts for why this is not a spec's business).
  execFileSync('dotnet', ['exec', syncDll, 'library', 'install', 'duckdb', '--version', '1.5.6', '--repo', syncDirectory], { stdio: 'inherit' })
  // A server must present a certificate to be paired with: the SDK's development certificate, exported.
  execFileSync('dotnet', ['dev-certs', 'https', '--export-path', certificate, '--password', certificatePassword, '--format', 'Pfx'], { stdio: 'inherit' })
  // The pairing code, made before the server starts; the console may grant admin.
  const made = execFileSync('dotnet', ['exec', syncDll, 'server', 'pair', '--repo', syncDirectory, '--max-role', 'admin'], { encoding: 'utf8' })
  fs.writeFileSync(path.join(consoleScratch, 'sync-pairing-code.txt'), /Pairing code: (\S+)/.exec(made)![1])
}

// The environment variables the spawned worker's secret store falls back to (this sandbox has no OS keychain); see playwright.config.ts.
const secretEnv = {
  DBDATA_SECRET_DBDATA_SYNC_CONNECTION_PLAYWRIGHT_SRC: SA_PASSWORD,
  DBDATA_SECRET_DBDATA_SYNC_CONNECTION_PLAYWRIGHT_TGT: SA_PASSWORD,
}

export default defineConfig({
  testDir: '.',
  outputDir: 'test-results-console',
  timeout: process.env.CI ? 120_000 : 60_000,
  fullyParallel: false,
  workers: 1,
  retries: process.env.CI ? 2 : 0,
  reporter: process.env.CI
    ? [['list'], ['github'], ['json', { outputFile: 'test-results-console/results.json' }]]
    : [['list']],
  use: {
    baseURL: origin,
    ignoreHTTPSErrors: true,   // the console's own self-signed certificate
    trace: 'retain-on-failure',
  },
  projects: [
    { name: 'console-setup', testMatch: /console-setup\.setup\.ts/, use: { ...devices['Desktop Chrome'] } },
    {
      name: 'through-console',
      testMatch: /tests\/.*\.spec\.ts/,
      // Not run through the console, on purpose (plan 13a, U08): the Docs screens read the pages the Sync web app was built with (`/docs/*.md` and the pictures they show, files of the
      // Sync server's own web root), which a relayed `/api` call cannot reach; the console does not carry any product's docs yet. docs-viewer.spec.ts stays in the direct run.
      testIgnore: /docs-viewer\.spec\.ts/,
      dependencies: ['console-setup'],
      use: { ...devices['Desktop Chrome'], storageState: consoleStorageState },
    },
  ],
  globalSetup: './global-setup.ts',
  globalTeardown: './global-teardown.ts',
  webServer: [
    {
      // `dotnet exec` on the built DLL, not `dotnet run`: killing a wrapper does not reliably kill its child.
      command: ['dotnet', 'exec', JSON.stringify(consoleDll), 'server', 'run', '--repo', JSON.stringify(consoleDirectory), '--listen', origin,
        '--web-root', JSON.stringify(path.join(kitRoot, 'DbDataKit/web/console/dist'))].join(' '),
      url: `${origin}/health/live`,
      ignoreHTTPSErrors: true,
      reuseExistingServer: false,
      timeout: 60_000,
      stdout: 'pipe',
      stderr: 'pipe',
    },
    {
      command: ['dotnet', 'exec', JSON.stringify(syncDll), 'server', 'run', '--repo', JSON.stringify(syncDirectory), '--url', syncUrl].join(' '),
      url: `${syncUrl}/api/health`,
      ignoreHTTPSErrors: true,
      reuseExistingServer: false,
      timeout: 90_000,
      stdout: 'pipe',
      stderr: 'pipe',
      env: {
        ...secretEnv,
        Kestrel__Certificates__Default__Path: certificate,
        Kestrel__Certificates__Default__Password: certificatePassword,
      },
    },
  ],
})
