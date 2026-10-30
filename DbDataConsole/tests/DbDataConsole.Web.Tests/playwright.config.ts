import { defineConfig, devices } from '@playwright/test'
import { execFileSync } from 'node:child_process'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const __dirname = path.dirname(fileURLToPath(import.meta.url))
const repoRoot = path.resolve(__dirname, '../../..')

// The console's directory for this run: cleared and recreated here, synchronously, before the server that uses it starts. Only in the process that starts the
// run — Playwright evaluates this file again in every worker, and clearing it then would delete the state the server is using.
export const scratchDirectory = path.join(os.tmpdir(), 'dbdatakit-console-e2e')
const isWorker = process.env.TEST_WORKER_INDEX !== undefined
if (!isWorker) {
  fs.rmSync(scratchDirectory, { recursive: true, force: true })
  fs.mkdirSync(scratchDirectory, { recursive: true })
}

// A Build server for the console to pair with: a real one, in a directory of its own, with a pairing code made before it starts and written where the test can read it.
export const buildDirectory = path.join(os.tmpdir(), 'dbdatakit-console-e2e-build')
export const buildPort = 8482
export const buildUrl = `https://localhost:${buildPort}`
const buildDll = path.join(repoRoot, 'DbDataBuild/src/DbDataBuild.Cli/bin/Debug/net10.0/dbdatabuild.dll')
if (!isWorker) {
  fs.rmSync(buildDirectory, { recursive: true, force: true })
  fs.mkdirSync(buildDirectory, { recursive: true })
  // a project for the server to host: the starter template, in a folder of its own
  const project = path.join(os.tmpdir(), 'dbdatakit-console-e2e-project')
  fs.rmSync(project, { recursive: true, force: true })
  execFileSync('dotnet', ['exec', buildDll, 'project', 'create', 'starter', project], { stdio: 'ignore' })
  // a git repository, which is what a workspace (a worktree of it) is made from
  for (const args of [['init', '-q'], ['add', '-A'], ['commit', '-qm', 'The starter project']])
    execFileSync('git', ['-c', 'user.name=console-e2e', '-c', 'user.email=e2e@example.invalid', ...args], { cwd: project, stdio: 'ignore' })
  execFileSync('dotnet', ['exec', buildDll, 'server', 'project', 'add', 'starter', '--path', project, '--repo', buildDirectory], { stdio: 'ignore' })
  // the console may grant every role there, which is what its administrators will hold
  const made = execFileSync('dotnet', ['exec', buildDll, 'server', 'pair', '--repo', buildDirectory, '--max-role', 'viewer,author,operator,approver'], { encoding: 'utf8' })
  fs.writeFileSync(path.join(scratchDirectory, 'build-pairing-code.txt'), /Pairing code: (\S+)/.exec(made)![1])
}

// A project for the deploy test, when there is a PostgreSQL to deploy to (`DBDATA_E2E_PG`, an Npgsql connection string to an empty database; CI starts one): the starter template set up for PostgreSQL, compiled and committed
// (a deploy applies from a clean working tree), with the tracking tables made and the raw tables seeded. The Build server gets the logins as the variables its commands read.
export const warehouseConnection = process.env.DBDATA_E2E_PG
if (!isWorker && warehouseConnection) {
  execFileSync(process.execPath, [path.join(__dirname, 'pgReset.cjs')], { stdio: 'inherit' })
  const project = path.join(os.tmpdir(), 'dbdatakit-console-e2e-warehouse')
  fs.rmSync(project, { recursive: true, force: true })
  execFileSync('dotnet', ['exec', buildDll, 'project', 'create', 'starter', project], { stdio: 'ignore' })
  // the warehouse is PostgreSQL: the starter's sqlserver connection file is replaced
  fs.rmSync(path.join(project, 'connections', 'sqlserver.yaml'))
  // the test database has no certificate to verify: a connection secures itself as its file says, and a file with no tls block encrypts and verifies
  fs.writeFileSync(path.join(project, 'connections', 'postgres.yaml'), 'schema: connection/1\nname: postgres\nengine: postgres\ntls:\n  encrypt: false\n  trust_server_certificate: true\n')
  const file = path.join(project, 'dbdatabuild.yml')
  fs.writeFileSync(file, fs.readFileSync(file, 'utf8')
    .replace('defaults: {connections: [sqlserver]}', 'defaults: {connections: [postgres]}')
    .replace('tracking: { connection: sqlserver }', 'tracking: { connection: postgres }')
    .replace('trailing_space: ignored', 'trailing_space: significant')
    .replace('default: { duckdb: NFC, sqlserver: Latin1_General_100_CS_AS }', 'default: { duckdb: NFC, postgres: C }'))
  const logins = { ...process.env, DBDATA_BUILD_POSTGRES_READ: warehouseConnection, DBDATA_BUILD_POSTGRES_WRITE: warehouseConnection }
  execFileSync('dotnet', ['exec', buildDll, 'project', 'compile'], { cwd: project, stdio: 'ignore' })
  execFileSync('dotnet', ['exec', buildDll, 'connection', 'init', 'postgres', '--apply'], { cwd: project, env: logins, stdio: 'ignore' })
  execFileSync('dotnet', ['exec', buildDll, 'connection', 'seed', 'postgres', '--apply'], { cwd: project, env: logins, stdio: 'ignore' })   // the raw tables the models read
  const git = (...args: string[]) => execFileSync('git', ['-c', 'user.name=console-e2e', '-c', 'user.email=e2e@example.invalid', ...args], { cwd: project, stdio: 'ignore' })
  git('init', '-q')
  git('add', '-A')
  git('commit', '-qm', 'The warehouse project')
  execFileSync('dotnet', ['exec', buildDll, 'server', 'project', 'add', 'warehouse', '--path', project, '--repo', buildDirectory], { stdio: 'ignore' })
}

// A Sync server too, for the console to host its screens: a real one, https with a certificate exported from the SDK's development certificate (a server must present a certificate to be paired
// with), a pairing code that lets the console grant admin, and a repository of its own.
export const syncDirectory = path.join(os.tmpdir(), 'dbdatakit-console-e2e-sync')
export const syncPort = 8483
export const syncUrl = `https://localhost:${syncPort}`
const syncDll = path.join(repoRoot, 'DbDataSync/src/DbDataSync.Cli/bin/Debug/net10.0/DbDataSync.Cli.dll')
const syncCertificate = path.join(os.tmpdir(), 'dbdatakit-console-e2e-sync.pfx')
const syncCertificatePassword = 'console-e2e'
if (!isWorker) {
  fs.rmSync(syncDirectory, { recursive: true, force: true })
  fs.mkdirSync(syncDirectory, { recursive: true })
  execFileSync('dotnet', ['dev-certs', 'https', '--export-path', syncCertificate, '--password', syncCertificatePassword, '--format', 'Pfx'], { stdio: 'inherit' })
  const made = execFileSync('dotnet', ['exec', syncDll, 'server', 'pair', '--repo', syncDirectory, '--max-role', 'admin'], { encoding: 'utf8' })
  fs.writeFileSync(path.join(scratchDirectory, 'sync-pairing-code.txt'), /Pairing code: (\S+)/.exec(made)![1])
}

// A Proxy too: a real one, with its management listener on, a self-signed certificate of its own, one data source (nothing needs to answer behind it) and a pairing code that lets the console grant admin.
export const proxyDirectory = path.join(os.tmpdir(), 'dbdatakit-console-e2e-proxy')
export const proxyPort = 8484
export const proxyManagementPort = 8485
export const proxyUrl = `https://localhost:${proxyManagementPort}`
const proxyDll = path.join(repoRoot, 'DbDataProxy/src/DbDataProxy.Server/bin/Debug/net10.0/dbdataproxy.dll')
if (!isWorker) {
  fs.rmSync(proxyDirectory, { recursive: true, force: true })
  fs.mkdirSync(path.join(proxyDirectory, 'config', 'connections'), { recursive: true })
  fs.writeFileSync(path.join(proxyDirectory, 'config', 'server.yaml'), 'schemaVersion: 2\ntls: { mode: selfSigned }\n')
  fs.mkdirSync(path.join(proxyDirectory, 'config', 'proxy'), { recursive: true })
  fs.writeFileSync(path.join(proxyDirectory, 'config', 'proxy', 'auth.yaml'), 'jwt:\n  signingKey: console-e2e-signing-key-0123456789abcdef\n  audience: api://console-e2e\n')
  fs.writeFileSync(path.join(proxyDirectory, 'config', 'connections', 'erp.yaml'), [
    'schema: connection/1', 'name: erp', 'engine: sqlserver', 'address: { host: localhost, database: Erp }',
    "auth: { mode: sql, user: reader, password_secret: 'env:ERP_DB_PASSWORD' }", ''].join('\n'))
  const made = execFileSync('dotnet', ['exec', proxyDll, 'server', 'pair', '--repo', proxyDirectory, '--max-role', 'admin'], { encoding: 'utf8' })
  fs.writeFileSync(path.join(scratchDirectory, 'proxy-pairing-code.txt'), /Pairing code: (\S+)/.exec(made)![1])
}

// Two more Build servers that announce themselves to the console instead of being paired with a code (plan 13a, L03): each is told where the console takes announcements and which address to give,
// and waits as pending until the test confirms or rejects it. A Build server with no project is enough: what is tested is the console's list and the pairing a confirmation makes.
export const announceListen = 'http://127.0.0.1:8486'
export const announcers = [
  { name: 'Announcing confirm', port: 8487, directory: path.join(os.tmpdir(), 'dbdatakit-console-e2e-announce-confirm') },
  { name: 'Announcing reject', port: 8488, directory: path.join(os.tmpdir(), 'dbdatakit-console-e2e-announce-reject') },
]
if (!isWorker) {
  for (const announcer of announcers) {
    fs.rmSync(announcer.directory, { recursive: true, force: true })
    fs.mkdirSync(announcer.directory, { recursive: true })
  }
}

// A passkey is bound to the host name and the port it was made on, and the server builds its list of allowed origins from the address it is told to listen on,
// so the address is fixed here and the test opens exactly that.
const port = 8481
const origin = `https://localhost:${port}`

export default defineConfig({
  testDir: './tests',
  timeout: process.env.CI ? 90_000 : 45_000,
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: process.env.CI ? [['list'], ['github']] : [['list']],
  use: {
    baseURL: origin,
    ignoreHTTPSErrors: true,   // the console's own self-signed certificate
    trace: 'retain-on-failure',
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
  webServer: [
    {
    // `dotnet exec` on the built DLL, not `dotnet run`: killing a wrapper does not reliably kill its child.
    command: [
      'dotnet', 'exec', path.join(repoRoot, 'DbDataConsole/src/DbDataConsole.Server/bin/Debug/net10.0/dbdatakit.dll'),
      'server', 'run', '--repo', JSON.stringify(scratchDirectory), '--listen', origin,
      '--web-root', JSON.stringify(path.join(repoRoot, 'DbDataKit/web/console/dist')),
      '--announce-listen', announceListen,
    ].join(' '),
    url: `${origin}/health/live`,
    ignoreHTTPSErrors: true,
    reuseExistingServer: false,
    timeout: 60_000,
    stdout: 'pipe',
    stderr: 'pipe',
    },
    {
      command: ['dotnet', 'exec', JSON.stringify(buildDll), 'server', 'run', '--repo', JSON.stringify(buildDirectory), '--listen', buildUrl].join(' '),
      url: `${buildUrl}/health/live`,
      ignoreHTTPSErrors: true,
      reuseExistingServer: false,
      timeout: 60_000,
      stdout: 'pipe',
      stderr: 'pipe',
      env: warehouseConnection ? { DBDATA_BUILD_POSTGRES_READ: warehouseConnection, DBDATA_BUILD_POSTGRES_WRITE: warehouseConnection } : {},
    },
    ...announcers.map((announcer) => ({
      command: ['dotnet', 'exec', JSON.stringify(buildDll), 'server', 'run', '--repo', JSON.stringify(announcer.directory), '--listen', `https://localhost:${announcer.port}`].join(' '),
      url: `https://localhost:${announcer.port}/health/live`,
      ignoreHTTPSErrors: true,
      reuseExistingServer: false,
      timeout: 60_000,
      stdout: 'pipe' as const,
      stderr: 'pipe' as const,
      env: { DBDATAKIT_CONSOLE_ANNOUNCE_URL: announceListen, DBDATAKIT_ADVERTISE_URL: `https://localhost:${announcer.port}`, DBDATAKIT_INSTANCE_NAME: announcer.name },
    })),
    {
      command: ['dotnet', 'exec', JSON.stringify(proxyDll), 'server', 'run',
        `--Proxy:DataDirectory=${JSON.stringify(proxyDirectory)}`, '--Proxy:Listen:Address=127.0.0.1', `--Proxy:Listen:Port=${proxyPort}`,
        '--Proxy:Management:Enabled=true', '--Proxy:Management:Address=127.0.0.1', `--Proxy:Management:Port=${proxyManagementPort}`].join(' '),
      url: `${proxyUrl}/health/live`,
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
        Kestrel__Certificates__Default__Path: syncCertificate,
        Kestrel__Certificates__Default__Password: syncCertificatePassword,
      },
    },
  ],
})
