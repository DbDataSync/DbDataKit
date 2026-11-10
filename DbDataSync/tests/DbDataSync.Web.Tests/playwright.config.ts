import { defineConfig, devices } from '@playwright/test'
import { execFileSync } from 'node:child_process'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { consoleScratch, consoleStorageState } from './console-paths'
import { SA_PASSWORD } from './test-db'

// Sync's Playwright suite (plan 13a, U08 and U11). Sync's server serves no web app any more: its screens are the console's, so the suite runs through it. The browser opens a console, signed in as an administrator,
// which has a real `dbdatasync server run` paired; the specs are written in the server's own addresses and `fixtures.ts` translates them to `/servers/<id>/...` and the console's relay.
// Run: `npx playwright test` (the engines from testenv, the builds of the API, the worker, the CLI and the console server, and the console's web app: `npm run build -w @dbdatakit/console` in DbDataKit/web).
const __dirname = path.dirname(fileURLToPath(import.meta.url))
const repoRoot = path.resolve(__dirname, '../..')
const kitRoot = path.resolve(repoRoot, '..')

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
  // DuckDB is installed the way a deployment gets it: a real `dbdatasync server run` installs it on its own (EnsureDuckDbInstalledAsync), but this suite starts the server with `dotnet exec` and a repository
  // prepared here, synchronously, before any server starts (globalSetup runs concurrently with the web servers, not before them). Nothing in the suite tests the install, only relies on DuckDB being there.
  execFileSync('dotnet', ['exec', syncDll, 'library', 'install', 'duckdb', '--version', '1.5.6', '--repo', syncDirectory], { stdio: 'inherit' })
  // A server must present a certificate to be paired with: the SDK's development certificate, exported.
  execFileSync('dotnet', ['dev-certs', 'https', '--export-path', certificate, '--password', certificatePassword, '--format', 'Pfx'], { stdio: 'inherit' })
  // The pairing code, made before the server starts; the console may grant admin.
  const made = execFileSync('dotnet', ['exec', syncDll, 'server', 'pair', '--repo', syncDirectory, '--max-role', 'admin'], { encoding: 'utf8' })
  fs.writeFileSync(path.join(consoleScratch, 'sync-pairing-code.txt'), /Pairing code: (\S+)/.exec(made)![1])
}

// Plan 28c E08: a real runner for the Runner screen's story (`runner-publish.spec.ts`), port 8641 (a port table: 8641 the Sync runner story; 8541 to 8544, 8551, 8561 and up, 8571 and up, 8581 to 8586, 8591, 8601 to 8603, 8611, 8621 and 8631 to 8634 belong to the
// console's stories, 8491 and 8493 to this suite's console and Sync server). The runtime package `dbdatasync-runtime` a replication set runs on is installed on it before it starts as a stand-in (the manifest `scripts/runtime-manifest.sh` writes, a placeholder for its command),
// so a staged version can be prepared and served: what is proved is the publish, the staging and the screen, not a drain (E06d, E12). The Sync server finds its pairing in `client directory` (the story's link names it), made by the runner's own command once the runner runs.
// The directories are short: the runner's task socket lives in its own.
export const syncRunner = {
  directory: path.join(os.tmpdir(), 'ddk-e2e-syncrunner'),
  clientDirectory: path.join(os.tmpdir(), 'ddk-e2e-syncrunner-client'),
  runtime: path.join(os.tmpdir(), 'ddk-e2e-syncrunner-runtime'),
  port: 8641,
  url: 'https://localhost:8641',
  dll: path.join(kitRoot, 'DbDataRunner/src/DbDataRunner.Server/bin/Debug/net10.0/dbdatarunner.dll'),
}
// The runner's own commands, run before any test: a failure says what the command printed (a bare "status 129" hid a runner that was never built in CI).
function runnerCommand(args: string[]): string {
  try {
    return execFileSync('dotnet', ['exec', syncRunner.dll, ...args], { encoding: 'utf8', stdio: 'pipe' })
  } catch (e) {
    const failure = e as { status?: number; stdout?: string; stderr?: string }
    throw new Error(`dbdatarunner ${args.slice(0, 2).join(' ')} failed (status ${failure.status}; is ${syncRunner.dll} built?)\nstdout: ${failure.stdout ?? ''}\nstderr: ${failure.stderr ?? ''}`)
  }
}
if (!isWorker) {
  for (const directory of [syncRunner.directory, syncRunner.clientDirectory, syncRunner.runtime]) {
    fs.rmSync(directory, { recursive: true, force: true })
    fs.mkdirSync(directory, { recursive: true })
  }
  // the stand-in runtime, of a version in the line of the Sync build that publishes (`2026.11.x`: the first two numbers of its version)
  const line = execFileSync('dotnet', ['exec', syncDll, 'version'], { encoding: 'utf8' }).trim().split('.').slice(0, 2).join('.')
  const rid = process.platform === 'win32' ? 'win-x64' : 'linux-x64'
  fs.mkdirSync(path.join(syncRunner.runtime, 'files', 'bin'), { recursive: true })
  fs.writeFileSync(path.join(syncRunner.runtime, 'files', 'bin', 'DbDataSync.TaskRunner.dll'), 'stand-in for the engine')
  fs.writeFileSync(path.join(syncRunner.runtime, 'package.yaml'), execFileSync('bash', [path.join(repoRoot, 'scripts/runtime-manifest.sh'), rid, `${line}.1`], { encoding: 'utf8' }))
  runnerCommand(['package', 'install', path.join(syncRunner.runtime, 'package.yaml'), path.join(syncRunner.runtime, 'files'), '--channel', 'prod', '--repo', syncRunner.directory])
  const made = runnerCommand(['server', 'pair', '--minutes', '120', '--repo', syncRunner.directory, '--max-role', 'viewer,operator,publisher,admin'])
  fs.writeFileSync(path.join(consoleScratch, 'sync-runner-pairing-code.txt'), /Pairing code: (\S+)/.exec(made)![1])
}

// The environment variables the spawned worker's secret store falls back to (this sandbox has no OS keychain): the connection passwords the specs save, for the spawned worker to resolve.
const secretEnv = {
  DBDATAKIT_SECRET_DBDATA_SYNC_CONNECTION_PLAYWRIGHT_SRC: SA_PASSWORD,
  DBDATAKIT_SECRET_DBDATA_SYNC_CONNECTION_PLAYWRIGHT_TGT: SA_PASSWORD,
}

export default defineConfig({
  testDir: '.',
  outputDir: 'test-results',
  timeout: process.env.CI ? 120_000 : 60_000,
  fullyParallel: false,
  workers: 1,
  retries: process.env.CI ? 2 : 0,
  reporter: process.env.CI
    ? [['list'], ['github'], ['json', { outputFile: 'test-results/results.json' }]]
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
    {
      // the runner of the Runner screen's story (`runner-publish.spec.ts`), on its own port and directory
      command: ['dotnet', 'exec', JSON.stringify(syncRunner.dll), 'server', 'run', '--repo', JSON.stringify(syncRunner.directory), '--listen', syncRunner.url, '--channels', 'prod'].join(' '),
      url: `${syncRunner.url}/health/live`,
      ignoreHTTPSErrors: true,
      reuseExistingServer: false,
      timeout: 90_000,
      stdout: 'pipe',
      stderr: 'pipe',
    },
  ],
})
