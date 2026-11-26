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
  // the console's delivery channel for subscriptions-delivery.spec.ts (plan 22a GP18 fold: a channel is a connection): `e2e-hook` is bound to the connection `e2e-hook-conn`, a signed webhook whose whole address is a secret read from a file that story writes
  // (a local listener, so its port is not known yet: the address stays a secret reference, shown as "address hidden") and whose signing secret is a file too; a console reads settings.yaml and connections/ when it starts
  const fileRef = (name: string) => `file:${path.join(scratchDirectory, name).replace(/\\/g, '/')}`
  fs.mkdirSync(path.join(scratchDirectory, 'connections'), { recursive: true })
  fs.writeFileSync(path.join(scratchDirectory, 'connections', 'e2e-hook-conn.yml'), `schema: connection/2\nkind: http\nname: e2e-hook-conn\nendpoint: ${fileRef('delivery-hook-url.txt')}\nwebhook:\n  format: signed\n  allow_private: [127.0.0.1]\nsign_in:\n  method: signed_webhook\n  key_secret: ${fileRef('delivery-hook-secret.txt')}\n`)
  fs.writeFileSync(path.join(scratchDirectory, 'bindings.yml'), 'bindings: 1\ndelivery:\n  e2e-hook: e2e-hook-conn\n')   // the channel is a binding (plan 27a IX23)
}

// A runner for the first-run story to pair with, see answer and forget: a real one, in a directory of its own (short: its task socket lives in it), with a pairing code made before it starts and written where the test can read it.
// (It was a Build server until plan 33 SC23 deleted the Build server; the Build screens of that story went with it.)
export const firstRunRunner = { directory: path.join(os.tmpdir(), 'ddk-e2e-first'), port: 8482, url: 'https://localhost:8482', dll: path.join(repoRoot, 'DbDataRunner/src/DbDataRunner.Server/bin/Debug/net10.0/dbdatarunner.dll') }
if (!isWorker) {
  fs.rmSync(firstRunRunner.directory, { recursive: true, force: true })
  fs.mkdirSync(firstRunRunner.directory, { recursive: true })
  const made = execFileSync('dotnet', ['exec', firstRunRunner.dll, 'server', 'pair', '--minutes', '120', '--repo', firstRunRunner.directory, '--max-role', 'viewer,operator,admin'], { encoding: 'utf8' })
  fs.writeFileSync(path.join(scratchDirectory, 'first-run-runner-pairing-code.txt'), /Pairing code: (\S+)/.exec(made)![1])
}

// A Gateway too: a real one, with its management listener on, a self-signed certificate of its own, one data source (nothing needs to answer behind it) and a pairing code that lets the console grant admin.
export const gatewayDirectory = path.join(os.tmpdir(), 'dbdatakit-console-e2e-gateway')
export const gatewayPort = 8484
export const gatewayManagementPort = 8485
const gatewayPublicPort = 8483
export const gatewayUrl = `https://localhost:${gatewayManagementPort}`
const gatewayDll = path.join(repoRoot, 'DbDataGateway/src/DbDataGateway.Server/bin/Debug/net10.0/dbdatagateway.dll')
if (!isWorker) {
  fs.rmSync(gatewayDirectory, { recursive: true, force: true })
  fs.mkdirSync(path.join(gatewayDirectory, 'config', 'connections'), { recursive: true })
  fs.writeFileSync(path.join(gatewayDirectory, 'config', 'server.yaml'), 'schemaVersion: 2\ntls: { mode: selfSigned }\n')
  fs.mkdirSync(path.join(gatewayDirectory, 'config', 'gateway'), { recursive: true })
  fs.writeFileSync(path.join(gatewayDirectory, 'config', 'gateway', 'auth.yaml'), 'jwt:\n  signingKey: console-e2e-signing-key-0123456789abcdef\n  audience: api://console-e2e\n')
  fs.writeFileSync(path.join(gatewayDirectory, 'config', 'connections', 'erp.yaml'), [
    'schema: connection/1', 'name: erp', 'engine: sqlserver', 'address: { host: localhost, database: Erp }',
    "auth: { mode: sql, user: reader, password_secret: 'env:ERP_DB_PASSWORD' }", ''].join('\n'))
  // a second data source made by hand (the story once handed both over to a Build server, which plan 33 SC23 deleted)
  fs.writeFileSync(path.join(gatewayDirectory, 'config', 'connections', 'legacy.yaml'), [
    'schema: connection/1', 'name: legacy', 'engine: sqlserver', 'address: { host: localhost, database: Legacy }',
    "auth: { mode: sql, user: reader, password_secret: 'env:LEGACY_DB_PASSWORD' }", ''].join('\n'))
  // one for the Connections story to copy (on the same Gateway, under another name)
  fs.writeFileSync(path.join(gatewayDirectory, 'config', 'connections', 'ledger.yaml'), [
    'schema: connection/1', 'name: ledger', 'engine: sqlserver', 'address: { host: localhost, database: Ledger }',
    "auth: { mode: sql, user: reader, password_secret: 'env:LEDGER_DB_PASSWORD' }", ''].join('\n'))
  const made = execFileSync('dotnet', ['exec', gatewayDll, 'server', 'pair', '--minutes', '120', '--repo', gatewayDirectory, '--max-role', 'admin'], { encoding: 'utf8' })
  fs.writeFileSync(path.join(scratchDirectory, 'gateway-pairing-code.txt'), /Pairing code: (\S+)/.exec(made)![1])
}

// Two runners that announce themselves to the console instead of being paired with a code (plan 13a, L03): each is told where the console takes announcements and which address to give,
// and waits as pending until the test confirms or rejects it. A runner with nothing published is enough: what is tested is the console's list and the pairing a confirmation makes. (They were Build servers until plan 33 SC23.)
export const announceListen = 'http://127.0.0.1:8486'
const announcerDll = path.join(repoRoot, 'DbDataRunner/src/DbDataRunner.Server/bin/Debug/net10.0/dbdatarunner.dll')
export const announcers = [
  { name: 'Announcing confirm', port: 8487, directory: path.join(os.tmpdir(), 'ddk-e2e-ann-c') },
  { name: 'Announcing reject', port: 8488, directory: path.join(os.tmpdir(), 'ddk-e2e-ann-r') },
]
if (!isWorker) {
  for (const announcer of announcers) {
    fs.rmSync(announcer.directory, { recursive: true, force: true })
    fs.mkdirSync(announcer.directory, { recursive: true })
  }
}

// A server for the monitoring story (plan 13a, M12): a Gateway the story sends failed logins to (the Build server with a failing schedule and the Sync server with a lagging replication went with those servers, plan 33 SC23).
// It is separate from the servers above, whose pairing codes the first-run story spends.
export const monitorGateway = { directory: path.join(os.tmpdir(), 'dbdatakit-console-e2e-monitor-gateway'), flightPort: 8491, managementPort: 8492, publicPort: 8489 }
if (!isWorker) {
  fs.rmSync(monitorGateway.directory, { recursive: true, force: true })
  fs.mkdirSync(path.join(monitorGateway.directory, 'config', 'gateway'), { recursive: true })
  fs.mkdirSync(path.join(monitorGateway.directory, 'config', 'connections'), { recursive: true })
  fs.writeFileSync(path.join(monitorGateway.directory, 'config', 'server.yaml'), 'schemaVersion: 2\ntls: { mode: selfSigned }\n')
  fs.writeFileSync(path.join(monitorGateway.directory, 'config', 'gateway', 'auth.yaml'), 'jwt:\n  signingKey: console-e2e-signing-key-0123456789abcdef\n  audience: api://console-e2e\n')
  fs.copyFileSync(path.join(gatewayDirectory, 'config', 'connections', 'erp.yaml'), path.join(monitorGateway.directory, 'config', 'connections', 'erp.yaml'))
  const madeGateway = execFileSync('dotnet', ['exec', gatewayDll, 'server', 'pair', '--minutes', '120', '--repo', monitorGateway.directory, '--max-role', 'admin'], { encoding: 'utf8' })
  fs.writeFileSync(path.join(scratchDirectory, 'monitor-gateway-pairing-code.txt'), /Pairing code: (\S+)/.exec(madeGateway)![1])
}

// A runner for the runner story (plan 28a, R19): a real `dbdatarunner` with the sample package of the runner's tests (`DbDataKit.Runner.TestPackage`, published to a folder here) published on the `dev` channel by the story (as version 1, then 2), two tasks:
// `ok` (every hour, so it also has a next time) and `tally` (by hand, with a progress value `seen` its runs move). A pairing code lets the console grant admin; the story publishes both versions through the console (version 2 resets
// `seen`: a loss). The runner's directory is short: its task socket lives in it and a Unix socket path is limited.
export const runnerServer = {
  directory: path.join(os.tmpdir(), 'ddk-e2e-runner'),
  port: 8496,
  url: 'https://localhost:8496',
  package: path.join(os.tmpdir(), 'ddk-e2e-runner-package'),
  manifests: path.join(os.tmpdir(), 'ddk-e2e-runner-manifests'),
  dll: path.join(repoRoot, 'DbDataRunner/src/DbDataRunner.Server/bin/Debug/net10.0/dbdatarunner.dll'),
}
if (!isWorker) {
  for (const directory of [runnerServer.directory, runnerServer.package, runnerServer.manifests]) {
    fs.rmSync(directory, { recursive: true, force: true })
    fs.mkdirSync(directory, { recursive: true })
  }
  execFileSync('dotnet', ['publish', path.join(repoRoot, 'Shared/tests/DbDataKit.Runner.TestPackage/DbDataKit.Runner.TestPackage.csproj'), '-c', 'Debug', '-o', runnerServer.package, '--nologo', '-v', 'q'], { stdio: 'ignore' })
  const manifest = (seenReset: boolean) => [
    'schema: package/1', 'name: sample',
    'launch: { kind: assembly, assembly: DbDataKit.Runner.TestPackage.dll }',
    'built_against: { dotnet: "10.0", kit: "1", protocol: dbdatakit.runner.v1 }',
    'tasks:',
    '  - name: ok', '    entry: { type: T }', '    schedule: { every: 1h }',
    '  - name: tally', '    entry: { type: T }', '    progress:', '      checkpoints:', '        - name: seen', ...(seenReset ? ['          reset: true'] : []),
    '          parts:', `            - { name: value, type: int${seenReset ? ', reset: true' : ''} }`, ''].join('\n')
  fs.writeFileSync(path.join(runnerServer.manifests, 'v1.yaml'), manifest(false))
  fs.writeFileSync(path.join(runnerServer.manifests, 'v2.yaml'), manifest(true))
  const made = execFileSync('dotnet', ['exec', runnerServer.dll, 'server', 'pair', '--minutes', '120', '--minutes', '120', '--repo', runnerServer.directory, '--max-role', 'viewer,operator,admin'], { encoding: 'utf8' })
  fs.writeFileSync(path.join(scratchDirectory, 'runner-pairing-code.txt'), /Pairing code: (\S+)/.exec(made)![1])
}

// A runner for the runner-scheduling story (plan 33, UE06): `runner-schedule.spec.ts` moves a task of the sample package between schedulers on the runner's screens. The runner has the console's pairing code (admin); the story
// publishes the sample package through the console. (The Build server that published to it in `runner-publish.spec.ts` went with the Build server, plan 33 SC23.)
export const publishRunner = {
  directory: path.join(os.tmpdir(), 'ddk-e2e-pubrunner'),
  port: 8503,
  url: 'https://localhost:8503',
  dll: path.join(repoRoot, 'DbDataRunner/src/DbDataRunner.Server/bin/Debug/net10.0/dbdatarunner.dll'),
}
// Plan 22a GP34: `apps-authoring.spec.ts` starts a Gateway of its own on 8601 (identity home), 8602 (apps host) and 8603 (management); no entry here, the story spawns it.
if (!isWorker) {
  fs.rmSync(publishRunner.directory, { recursive: true, force: true })
  fs.mkdirSync(publishRunner.directory, { recursive: true })
  const made = execFileSync('dotnet', ['exec', publishRunner.dll, 'server', 'pair', '--minutes', '120', '--minutes', '120', '--repo', publishRunner.directory, '--max-role', 'viewer,operator,admin'], { encoding: 'utf8' })
  fs.writeFileSync(path.join(scratchDirectory, 'publish-runner-console-code.txt'), /Pairing code: (\S+)/.exec(made)![1])
}

// Plan 24a UC08 and UC09: a runner for the upgrades story (`upgrades.spec.ts`), port 8591 (a port table: 8591 upgrades runner; a Build server until plan 33 SC23). It runs by hand, not as a service, so the console shows what is available for it and says why it is not
// upgraded from here; a mirror folder (made by the story) stands in for the release feed. Its own directory and port, so nothing the other stories leave in the servers above is in its way.
// a server started by hand, as a person types it: a CI runner agent is a systemd unit and its children inherit these, which says nothing about the child
function byHandEnvironment() {
  const env = { ...(process.env as Record<string, string>) }
  for (const name of ['INVOCATION_ID', 'JOURNAL_STREAM', 'SYSTEMD_EXEC_PID']) delete env[name]
  return env
}
export const upgradeRunner = { directory: path.join(os.tmpdir(), 'ddk-e2e-upgrade'), port: 8591, mirror: path.join(os.tmpdir(), 'dbdatakit-console-e2e-upgrade-mirror'), dll: path.join(repoRoot, 'DbDataRunner/src/DbDataRunner.Server/bin/Debug/net10.0/dbdatarunner.dll') }
if (!isWorker) {
  fs.rmSync(upgradeRunner.directory, { recursive: true, force: true })
  fs.mkdirSync(upgradeRunner.directory, { recursive: true })
  const made = execFileSync('dotnet', ['exec', upgradeRunner.dll, 'server', 'pair', '--minutes', '120', '--repo', upgradeRunner.directory, '--max-role', 'viewer,operator,admin'], { encoding: 'utf8' })
  fs.writeFileSync(path.join(scratchDirectory, 'upgrade-runner-pairing-code.txt'), /Pairing code: (\S+)/.exec(made)![1])
}

// Plan 22a GP31: a Workspace server for the workspaces story (`workspaces.spec.ts`), port 8611 (a port table: 8611 the Workspace server; 8541 to 8544, 8551, 8561 and up, 8571 and up, 8581 to 8586 and 8591 are other stories'). One project, `orders`,
// made by the server's own command; the console's pairing code lets it grant admin, author and approver (the story makes a workspace as an author and abandons it).
export const workspaceServer = {
  directory: path.join(os.tmpdir(), 'ddk-e2e-workspace'),
  port: 8611,
  url: 'https://localhost:8611',
  project: 'orders',
  dll: path.join(repoRoot, 'DbDataWorkspace/src/DbDataWorkspace.Server/bin/Debug/net10.0/dbdataworkspace.dll'),
}
if (!isWorker) {
  fs.rmSync(workspaceServer.directory, { recursive: true, force: true })
  fs.mkdirSync(workspaceServer.directory, { recursive: true })
  execFileSync('dotnet', ['exec', workspaceServer.dll, 'project', 'create', workspaceServer.project, '--title', 'Orders', '--repo', workspaceServer.directory], { stdio: 'ignore' })
  const made = execFileSync('dotnet', ['exec', workspaceServer.dll, 'server', 'pair', '--minutes', '120', '--repo', workspaceServer.directory, '--max-role', 'viewer,author,approver,admin'], { encoding: 'utf8' })
  fs.writeFileSync(path.join(scratchDirectory, 'workspace-pairing-code.txt'), /Pairing code: (\S+)/.exec(made)![1])
}

// Plan 22a GP35a: a second Workspace server for the editing story (`workspace-files.spec.ts`), port 8621 (a port table: 8611 the first Workspace server, 8621 this one; 8541 to 8544, 8551, 8561 and up, 8571 and up, 8581 to 8586 and 8591 are other
// stories'). It has its own directory so the first story's server is left as it was. One project, `orders`, from the server's own command; the story makes a workspace, writes a file, commits and merges, so it needs the approver role.
export const workspaceEditServer = {
  directory: path.join(os.tmpdir(), 'ddk-e2e-workspace-edit'),
  port: 8621,
  url: 'https://localhost:8621',
  project: 'orders',
  dll: workspaceServer.dll,
}
if (!isWorker) {
  fs.rmSync(workspaceEditServer.directory, { recursive: true, force: true })
  fs.mkdirSync(workspaceEditServer.directory, { recursive: true })
  execFileSync('dotnet', ['exec', workspaceEditServer.dll, 'project', 'create', workspaceEditServer.project, '--title', 'Orders', '--repo', workspaceEditServer.directory], { stdio: 'ignore' })
  const made = execFileSync('dotnet', ['exec', workspaceEditServer.dll, 'server', 'pair', '--minutes', '120', '--repo', workspaceEditServer.directory, '--max-role', 'viewer,author,approver,admin'], { encoding: 'utf8' })
  fs.writeFileSync(path.join(scratchDirectory, 'workspace-edit-pairing-code.txt'), /Pairing code: (\S+)/.exec(made)![1])
}

// Plan 22a GP35b: the Workspace server of the publishing story (`workspace-publish.spec.ts`), port 8631 (a port table: 8631 this Workspace server, 8632 to 8634 the Gateway the story starts for it: 8632 the identity home,
// 8633 the apps host, 8634 the management API; 8541 to 8544, 8551, 8561 and up, 8571 and up, 8581 to 8586, 8591, 8601 to 8603, 8611 and 8621 are other stories'). The project is `sales`, made from the app template by the server's own
// command (the project's id is the app's name). The server's `apps:` section names the vendored package store, which the story makes with `app-store/build.mjs` before the first build (a build reads the settings again each time),
// and sets `isolation: auto` so a runner that cannot make a network namespace still builds, visibly: the story reads the isolation line either way.
export const workspacePublish = {
  directory: path.join(os.tmpdir(), 'ddk-e2e-workspace-publish'),
  port: 8631,
  url: 'https://localhost:8631',
  project: 'sales',
  packageStore: path.join(os.tmpdir(), 'ddk-e2e-workspace-publish-store'),
  gateway: { directory: path.join(os.tmpdir(), 'ddk-e2e-workspace-publish-gateway'), homePort: 8632, appsPort: 8633, managementPort: 8634 },
  dll: workspaceServer.dll,
}
if (!isWorker) {
  for (const directory of [workspacePublish.directory, workspacePublish.packageStore, workspacePublish.gateway.directory]) fs.rmSync(directory, { recursive: true, force: true })
  fs.mkdirSync(workspacePublish.directory, { recursive: true })
  execFileSync('dotnet', ['exec', workspacePublish.dll, 'project', 'create', workspacePublish.project, '--title', 'Sales', '--repo', workspacePublish.directory], { stdio: 'ignore' })
  const made = execFileSync('dotnet', ['exec', workspacePublish.dll, 'server', 'pair', '--minutes', '120', '--repo', workspacePublish.directory, '--max-role', 'viewer,author,approver,admin'], { encoding: 'utf8' })
  fs.writeFileSync(path.join(scratchDirectory, 'workspace-publish-pairing-code.txt'), /Pairing code: (\S+)/.exec(made)![1])
  const settings = path.join(workspacePublish.directory, 'server.yaml')
  fs.appendFileSync(settings, `${fs.existsSync(settings) && !fs.readFileSync(settings, 'utf8').endsWith('\n') ? '\n' : ''}apps:\n  packageStore: '${workspacePublish.packageStore}'\n  isolation: auto\n  buildTimeoutSeconds: 240\n`)
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
      'dotnet', 'exec', path.join(repoRoot, 'DbDataKit/src/DbDataKit.Server/bin/Debug/net10.0/dbdatakit.dll'),
      'server', 'run', '--repo', JSON.stringify(scratchDirectory), '--listen', origin,
      '--web-root', JSON.stringify(path.join(repoRoot, 'Shared/web/console/dist')),
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
      command: ['dotnet', 'exec', JSON.stringify(firstRunRunner.dll), 'server', 'run', '--repo', JSON.stringify(firstRunRunner.directory), '--listen', firstRunRunner.url].join(' '),
      url: `${firstRunRunner.url}/health/live`,
      ignoreHTTPSErrors: true,
      reuseExistingServer: false,
      timeout: 60_000,
      stdout: 'pipe' as const,
      stderr: 'pipe' as const,
    },
    {
      command: ['dotnet', 'exec', JSON.stringify(runnerServer.dll), 'server', 'run', '--repo', JSON.stringify(runnerServer.directory), '--listen', runnerServer.url].join(' '),
      url: `${runnerServer.url}/health/live`,
      ignoreHTTPSErrors: true,
      reuseExistingServer: false,
      timeout: 60_000,
      stdout: 'pipe' as const,
      stderr: 'pipe' as const,
    },
    {
      command: ['dotnet', 'exec', JSON.stringify(workspaceServer.dll), 'server', 'run', '--repo', JSON.stringify(workspaceServer.directory), '--listen', workspaceServer.url].join(' '),
      url: `${workspaceServer.url}/health/live`,
      ignoreHTTPSErrors: true,
      reuseExistingServer: false,
      timeout: 60_000,
      stdout: 'pipe' as const,
      stderr: 'pipe' as const,
    },
    {
      command: ['dotnet', 'exec', JSON.stringify(workspaceEditServer.dll), 'server', 'run', '--repo', JSON.stringify(workspaceEditServer.directory), '--listen', workspaceEditServer.url].join(' '),
      url: `${workspaceEditServer.url}/health/live`,
      ignoreHTTPSErrors: true,
      reuseExistingServer: false,
      timeout: 60_000,
      stdout: 'pipe' as const,
      stderr: 'pipe' as const,
    },
    {
      command: ['dotnet', 'exec', JSON.stringify(workspacePublish.dll), 'server', 'run', '--repo', JSON.stringify(workspacePublish.directory), '--listen', workspacePublish.url].join(' '),
      url: `${workspacePublish.url}/health/live`,
      ignoreHTTPSErrors: true,
      reuseExistingServer: false,
      timeout: 60_000,
      stdout: 'pipe' as const,
      stderr: 'pipe' as const,
    },
    {
      command: ['dotnet', 'exec', JSON.stringify(publishRunner.dll), 'server', 'run', '--repo', JSON.stringify(publishRunner.directory), '--listen', publishRunner.url].join(' '),
      url: `${publishRunner.url}/health/live`,
      ignoreHTTPSErrors: true,
      reuseExistingServer: false,
      timeout: 60_000,
      stdout: 'pipe' as const,
      stderr: 'pipe' as const,
    },
    ...announcers.map((announcer) => ({
      command: ['dotnet', 'exec', JSON.stringify(announcerDll), 'server', 'run', '--repo', JSON.stringify(announcer.directory), '--listen', `https://localhost:${announcer.port}`].join(' '),
      url: `https://localhost:${announcer.port}/health/live`,
      ignoreHTTPSErrors: true,
      reuseExistingServer: false,
      timeout: 60_000,
      stdout: 'pipe' as const,
      stderr: 'pipe' as const,
      env: { DBDATAKIT_ANNOUNCE_URL: announceListen, DBDATAKIT_ADVERTISE_URL: `https://localhost:${announcer.port}`, DBDATAKIT_INSTANCE_NAME: announcer.name },
    })),
    {
      command: ['dotnet', 'exec', JSON.stringify(gatewayDll), 'server', 'run',
        `--Gateway:DataDirectory=${JSON.stringify(gatewayDirectory)}`, `--Gateway:Listen=https://127.0.0.1:${gatewayPort}`, `--Gateway:Public:Listen=https://127.0.0.1:${gatewayPublicPort}`,
        '--Gateway:Management:Enabled=true', `--Gateway:Management:Listen=https://127.0.0.1:${gatewayManagementPort}`].join(' '),
      // the variable the ledger's login names, so the Connections story can bind it before its copy is taken (plan 30 PB02: the Gateway binds only a secret it holds)
      env: { ...byHandEnvironment(), LEDGER_DB_PASSWORD: 'e2e-not-a-password' },
      url: `${gatewayUrl}/health/live`,
      ignoreHTTPSErrors: true,
      reuseExistingServer: false,
      timeout: 60_000,
      stdout: 'pipe',
      stderr: 'pipe',
    },
    {
      command: ['dotnet', 'exec', JSON.stringify(upgradeRunner.dll), 'server', 'run', '--repo', JSON.stringify(upgradeRunner.directory), '--listen', `https://localhost:${upgradeRunner.port}`].join(' '),
      url: `https://localhost:${upgradeRunner.port}/health/live`,
      env: { ...byHandEnvironment(), DBDATAKIT_UPGRADE_MIRROR: upgradeRunner.mirror },   // the server prepares its own upgrade from the same mirror folder the console reads
      ignoreHTTPSErrors: true,
      reuseExistingServer: false,
      timeout: 60_000,
      stdout: 'pipe' as const,
      stderr: 'pipe' as const,
    },
    {
      command: ['dotnet', 'exec', JSON.stringify(gatewayDll), 'server', 'run',
        `--Gateway:DataDirectory=${JSON.stringify(monitorGateway.directory)}`, `--Gateway:Listen=https://127.0.0.1:${monitorGateway.flightPort}`, `--Gateway:Public:Listen=https://127.0.0.1:${monitorGateway.publicPort}`,
        '--Gateway:Management:Enabled=true', `--Gateway:Management:Listen=https://127.0.0.1:${monitorGateway.managementPort}`].join(' '),
      url: `https://localhost:${monitorGateway.managementPort}/health/live`,
      ignoreHTTPSErrors: true,
      reuseExistingServer: false,
      timeout: 60_000,
      stdout: 'pipe' as const,
      stderr: 'pipe' as const,
    },
  ],
})
