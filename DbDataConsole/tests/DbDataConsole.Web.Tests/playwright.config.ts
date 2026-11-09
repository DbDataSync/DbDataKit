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
export const buildDll = path.join(repoRoot, 'DbDataBuild/src/DbDataBuild.Cli/bin/Debug/net10.0/dbdatabuild.dll')
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
  const made = execFileSync('dotnet', ['exec', buildDll, 'server', 'pair', '--minutes', '120', '--repo', buildDirectory, '--max-role', 'viewer,author,operator,approver'], { encoding: 'utf8' })
  fs.writeFileSync(path.join(scratchDirectory, 'build-pairing-code.txt'), /Pairing code: (\S+)/.exec(made)![1])
  // a second code for the same server: the story forgets the first pairing (testing that), and pairs the server again for the publishing steps, which link it to a Gateway (the admin role on the Build server)
  const madeAgain = execFileSync('dotnet', ['exec', buildDll, 'server', 'pair', '--minutes', '120', '--repo', buildDirectory, '--max-role', 'viewer,author,operator,approver,admin'], { encoding: 'utf8' })
  fs.writeFileSync(path.join(scratchDirectory, 'build-pairing-code-again.txt'), /Pairing code: (\S+)/.exec(madeAgain)![1])
  // the read login of the starter's SQL Server connection, in the server's own store (what publishing it to a Gateway sends; nothing connects to it)
  execFileSync('dotnet', ['exec', buildDll, 'secret', 'set', 'dbdata:build:connection:sqlserver:read', '--stdin', '--repo', buildDirectory],
    { input: 'Server=db.example;User Id=reader;Password=console-e2e-read;Encrypt=True\n', stdio: ['pipe', 'ignore', 'inherit'] })
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
  const made = execFileSync('dotnet', ['exec', syncDll, 'server', 'pair', '--minutes', '120', '--repo', syncDirectory, '--max-role', 'admin'], { encoding: 'utf8' })
  fs.writeFileSync(path.join(scratchDirectory, 'sync-pairing-code.txt'), /Pairing code: (\S+)/.exec(made)![1])
}

// A Gateway too: a real one, with its management listener on, a self-signed certificate of its own, one data source (nothing needs to answer behind it) and a pairing code that lets the console grant admin.
export const gatewayDirectory = path.join(os.tmpdir(), 'dbdatakit-console-e2e-gateway')
export const gatewayPort = 8484
export const gatewayManagementPort = 8485
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
  // a second data source made by hand: the story hands `erp` over from the Build screen and this one from the Gateway's own page (plan 13a, T13)
  fs.writeFileSync(path.join(gatewayDirectory, 'config', 'connections', 'legacy.yaml'), [
    'schema: connection/1', 'name: legacy', 'engine: sqlserver', 'address: { host: localhost, database: Legacy }',
    "auth: { mode: sql, user: reader, password_secret: 'env:LEGACY_DB_PASSWORD' }", ''].join('\n'))
  // one nothing hands over, for the Connections story to copy to a Sync server (a handed-over one is republished by Build with its connection string in a secret, which Sync cannot read)
  fs.writeFileSync(path.join(gatewayDirectory, 'config', 'connections', 'ledger.yaml'), [
    'schema: connection/1', 'name: ledger', 'engine: sqlserver', 'address: { host: localhost, database: Ledger }',
    "auth: { mode: sql, user: reader, password_secret: 'env:LEDGER_DB_PASSWORD' }", ''].join('\n'))
  const made = execFileSync('dotnet', ['exec', gatewayDll, 'server', 'pair', '--minutes', '120', '--repo', gatewayDirectory, '--max-role', 'admin'], { encoding: 'utf8' })
  fs.writeFileSync(path.join(scratchDirectory, 'gateway-pairing-code.txt'), /Pairing code: (\S+)/.exec(made)![1])
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

// A Sync server for the quality story (plan 23a, FQ17): its state database is seeded by the story with a blocking check's results, and a replication whose target cannot be reached is evaluated on request. Its own directory and port
// (8531; 8481 to 8499, 8501, 8502 and 8511 to 8519 are taken), so nothing the other stories leave in the servers above is in its way.
export const qualitySync = { directory: path.join(os.tmpdir(), 'dbdatakit-console-e2e-quality-sync'), port: 8531 }
if (!isWorker) {
  fs.rmSync(qualitySync.directory, { recursive: true, force: true })
  fs.mkdirSync(qualitySync.directory, { recursive: true })
  const madeQuality = execFileSync('dotnet', ['exec', syncDll, 'server', 'pair', '--minutes', '120', '--repo', qualitySync.directory, '--max-role', 'admin'], { encoding: 'utf8' })
  fs.writeFileSync(path.join(scratchDirectory, 'quality-sync-pairing-code.txt'), /Pairing code: (\S+)/.exec(madeQuality)![1])
}

// Servers for the monitoring story (plan 13a, M12): a Build server that runs schedules and hosts a project whose one refresh plan runs every minute and fails (no login is set for its connection, so a run ends
// with an exit code), a Sync server for a replication made in the story, and a Gateway the story sends failed logins to. They are separate from the servers above, whose pairing codes the first-run story spends.
export const monitorBuild = { directory: path.join(os.tmpdir(), 'dbdatakit-console-e2e-monitor-build'), port: 8489, project: 'scheduled', plan: 'minutely', connection: 'sqlserver' }
export const monitorSync = { directory: path.join(os.tmpdir(), 'dbdatakit-console-e2e-monitor-sync'), port: 8490 }
export const monitorGateway = { directory: path.join(os.tmpdir(), 'dbdatakit-console-e2e-monitor-gateway'), flightPort: 8491, managementPort: 8492 }
if (!isWorker) {
  // the Build project: two models, one with a refresh plan that is scheduled every minute; compiled, so the server finds the plan in rendered/
  fs.rmSync(monitorBuild.directory, { recursive: true, force: true })
  fs.mkdirSync(monitorBuild.directory, { recursive: true })
  const project = path.join(os.tmpdir(), 'dbdatakit-console-e2e-monitor-project')
  fs.rmSync(project, { recursive: true, force: true })
  const write = (relative: string, text: string) => { const file = path.join(project, relative); fs.mkdirSync(path.dirname(file), { recursive: true }); fs.writeFileSync(file, text) }
  const columns = 'grain: [event_id]\ncolumns:\n  - {name: event_id, type: BIGINT, nullable: false}\n  - {name: event_ts, type: TIMESTAMP, nullable: false}\n  - {name: amount, type: "DECIMAL(14, 2)"}\n'
  write('dbdatabuild.yml', 'defaults: {connections: [sqlserver]}\nstring_semantics:\n  case: sensitive\n  trailing_space: ignored\n  collations:\n    default: { duckdb: NFC, sqlserver: Latin1_General_100_CS_AS }\n' +
    `refresh:\n  plans:\n    ${monitorBuild.plan}: { schedule: "* * * * *", time_zone: UTC }\n`)
  write('models/staging/events.yml', 'name: staging.events\nkind: mapped\n' + columns)
  write('models/marts/fct_events.yml', 'name: marts.fct_events\nkind: {incremental_by_time_range: {time_column: event_ts}}\n' + columns +
    'loads:\n  incremental:\n    default: true\n    strategy: watermark_append\n    watermark: {column: event_ts, resolver: target_max, on_null: initial, initial: "2000-01-01 00:00:00"}\n' +
    `refresh: { ${monitorBuild.plan}: incremental }\n`)
  write('models/marts/fct_events.sql', 'SELECT e.event_id, e.event_ts, e.amount FROM staging.events e\n')
  execFileSync('dotnet', ['exec', buildDll, 'project', 'compile', '--project', project], { stdio: 'ignore' })
  execFileSync('dotnet', ['exec', buildDll, 'server', 'project', 'add', monitorBuild.project, '--path', project, '--repo', monitorBuild.directory], { stdio: 'ignore' })
  const madeBuild = execFileSync('dotnet', ['exec', buildDll, 'server', 'pair', '--minutes', '120', '--repo', monitorBuild.directory, '--max-role', 'viewer,author,operator,approver'], { encoding: 'utf8' })
  fs.writeFileSync(path.join(scratchDirectory, 'monitor-build-pairing-code.txt'), /Pairing code: (\S+)/.exec(madeBuild)![1])

  fs.rmSync(monitorSync.directory, { recursive: true, force: true })
  fs.mkdirSync(monitorSync.directory, { recursive: true })
  const madeSync = execFileSync('dotnet', ['exec', syncDll, 'server', 'pair', '--minutes', '120', '--repo', monitorSync.directory, '--max-role', 'admin'], { encoding: 'utf8' })
  fs.writeFileSync(path.join(scratchDirectory, 'monitor-sync-pairing-code.txt'), /Pairing code: (\S+)/.exec(madeSync)![1])

  fs.rmSync(monitorGateway.directory, { recursive: true, force: true })
  fs.mkdirSync(path.join(monitorGateway.directory, 'config', 'gateway'), { recursive: true })
  fs.mkdirSync(path.join(monitorGateway.directory, 'config', 'connections'), { recursive: true })
  fs.writeFileSync(path.join(monitorGateway.directory, 'config', 'server.yaml'), 'schemaVersion: 2\ntls: { mode: selfSigned }\n')
  fs.writeFileSync(path.join(monitorGateway.directory, 'config', 'gateway', 'auth.yaml'), 'jwt:\n  signingKey: console-e2e-signing-key-0123456789abcdef\n  audience: api://console-e2e\n')
  fs.copyFileSync(path.join(gatewayDirectory, 'config', 'connections', 'erp.yaml'), path.join(monitorGateway.directory, 'config', 'connections', 'erp.yaml'))
  const madeGateway = execFileSync('dotnet', ['exec', gatewayDll, 'server', 'pair', '--minutes', '120', '--repo', monitorGateway.directory, '--max-role', 'admin'], { encoding: 'utf8' })
  fs.writeFileSync(path.join(scratchDirectory, 'monitor-gateway-pairing-code.txt'), /Pairing code: (\S+)/.exec(madeGateway)![1])
}

// The connection inventory story (plan 28c, E23): a Build server hosting a project with a connection file (`connections/sqlserver.yaml`) and a plan scheduled once a day, and a Sync server the story gives a connection and a replication that uses it. Neither runs anything:
// what is shown is what each reports about its connections and who uses them.
export const inventoryBuild = { directory: path.join(os.tmpdir(), 'dbdatakit-console-e2e-inventory-build'), port: 8501, project: 'ledger', plan: 'daily', connection: 'sqlserver' }
export const inventorySync = { directory: path.join(os.tmpdir(), 'dbdatakit-console-e2e-inventory-sync'), port: 8502 }
if (!isWorker) {
  fs.rmSync(inventoryBuild.directory, { recursive: true, force: true })
  fs.mkdirSync(inventoryBuild.directory, { recursive: true })
  const project = path.join(os.tmpdir(), 'dbdatakit-console-e2e-inventory-project')
  fs.rmSync(project, { recursive: true, force: true })
  const write = (relative: string, text: string) => { const file = path.join(project, relative); fs.mkdirSync(path.dirname(file), { recursive: true }); fs.writeFileSync(file, text) }
  const columns = 'grain: [event_id]\ncolumns:\n  - {name: event_id, type: BIGINT, nullable: false}\n  - {name: event_ts, type: TIMESTAMP, nullable: false}\n  - {name: amount, type: "DECIMAL(14, 2)"}\n'
  write('dbdatabuild.yml', 'defaults: {connections: [sqlserver]}\nstring_semantics:\n  case: sensitive\n  trailing_space: ignored\n  collations:\n    default: { duckdb: NFC, sqlserver: Latin1_General_100_CS_AS }\n' +
    `refresh:\n  plans:\n    ${inventoryBuild.plan}: { schedule: "0 3 * * *", time_zone: UTC }\n`)
  write('models/staging/events.yml', 'name: staging.events\nkind: mapped\n' + columns)
  write('models/marts/fct_events.yml', 'name: marts.fct_events\nkind: {incremental_by_time_range: {time_column: event_ts}}\n' + columns +
    'loads:\n  incremental:\n    default: true\n    strategy: watermark_append\n    watermark: {column: event_ts, resolver: target_max, on_null: initial, initial: "2000-01-01 00:00:00"}\n' +
    `refresh: { ${inventoryBuild.plan}: incremental }\n`)
  write('models/marts/fct_events.sql', 'SELECT e.event_id, e.event_ts, e.amount FROM staging.events e\n')
  // a project's connection is a file; it holds no address and no login
  write(`connections/${inventoryBuild.connection}.yaml`, `schema: connection/1\nname: ${inventoryBuild.connection}\nengine: sqlserver\n`)
  execFileSync('dotnet', ['exec', buildDll, 'project', 'compile', '--project', project], { stdio: 'ignore' })
  execFileSync('dotnet', ['exec', buildDll, 'server', 'project', 'add', inventoryBuild.project, '--path', project, '--repo', inventoryBuild.directory], { stdio: 'ignore' })
  const madeBuild = execFileSync('dotnet', ['exec', buildDll, 'server', 'pair', '--minutes', '120', '--repo', inventoryBuild.directory, '--max-role', 'viewer,author,operator,approver'], { encoding: 'utf8' })
  fs.writeFileSync(path.join(scratchDirectory, 'inventory-build-pairing-code.txt'), /Pairing code: (\S+)/.exec(madeBuild)![1])

  fs.rmSync(inventorySync.directory, { recursive: true, force: true })
  fs.mkdirSync(inventorySync.directory, { recursive: true })
  const madeSync = execFileSync('dotnet', ['exec', syncDll, 'server', 'pair', '--minutes', '120', '--repo', inventorySync.directory, '--max-role', 'admin'], { encoding: 'utf8' })
  fs.writeFileSync(path.join(scratchDirectory, 'inventory-sync-pairing-code.txt'), /Pairing code: (\S+)/.exec(madeSync)![1])
}

// A Build server for the authoring story (plan 13a, W07): it hosts no project, so the story makes one from a template. Its connection `origin` (added by the story to the project it makes) is a DuckDB file, which needs no engine:
// the tables of the adventureworks template, seeded, with the login for it (the file's path) in the server's own store. The story imports some of them as mapped models.
export const authoringBuild = { directory: path.join(os.tmpdir(), 'dbdatakit-console-e2e-authoring-build'), port: 8493, origin: path.join(os.tmpdir(), 'dbdatakit-console-e2e-origin.duckdb') }
if (!isWorker) {
  fs.rmSync(authoringBuild.directory, { recursive: true, force: true })
  fs.mkdirSync(authoringBuild.directory, { recursive: true })
  const source = path.join(os.tmpdir(), 'dbdatakit-console-e2e-origin-project')
  fs.rmSync(source, { recursive: true, force: true })
  fs.rmSync(authoringBuild.origin, { force: true })
  execFileSync('dotnet', ['exec', buildDll, 'project', 'create', 'adventureworks', source], { stdio: 'ignore' })
  execFileSync('dotnet', ['exec', buildDll, 'project', 'seed', '--project', source, '--out', authoringBuild.origin], { stdio: 'ignore' })
  execFileSync('dotnet', ['exec', buildDll, 'secret', 'set', 'dbdata:build:connection:origin:read', '--stdin', '--repo', authoringBuild.directory], { input: authoringBuild.origin + '\n', stdio: ['pipe', 'ignore', 'inherit'] })
  const made = execFileSync('dotnet', ['exec', buildDll, 'server', 'pair', '--minutes', '120', '--repo', authoringBuild.directory, '--max-role', 'viewer,author,operator,approver'], { encoding: 'utf8' })
  fs.writeFileSync(path.join(scratchDirectory, 'authoring-build-pairing-code.txt'), /Pairing code: (\S+)/.exec(made)![1])
}

// A Build server for the end-to-end story (plan 13a, W10): it hosts no project, so the story makes one from the starter template, edits, commits and merges it, and (when there is a PostgreSQL, DBDATA_E2E_PG) plans, approves and applies it
// there. Its own directory and port, so nothing the other stories leave in the servers above is in its way. Its commands read the PostgreSQL login from the environment the server is started with, as the first Build server's do.
export const endToEndBuild = { directory: path.join(os.tmpdir(), 'dbdatakit-console-e2e-end-to-end-build'), port: 8494 }
if (!isWorker) {
  fs.rmSync(endToEndBuild.directory, { recursive: true, force: true })
  fs.mkdirSync(endToEndBuild.directory, { recursive: true })
  const made = execFileSync('dotnet', ['exec', buildDll, 'server', 'pair', '--minutes', '120', '--repo', endToEndBuild.directory, '--max-role', 'viewer,author,operator,approver,admin'], { encoding: 'utf8' })
  fs.writeFileSync(path.join(scratchDirectory, 'end-to-end-build-pairing-code.txt'), /Pairing code: (\S+)/.exec(made)![1])
}

// Plan 33 UE09 and UE12: two Build servers and a Gateway for the refresh story (`refresh-run.spec.ts`) and the published-models story (`semantic-models.spec.ts`). Ports 8541 to 8544 (a port table: 8541 refresh Build,
// 8542 and 8543 the Gateway's Flight SQL and management listeners, 8544 models Build). Both stories need the test PostgreSQL (DBDATA_E2E_PG) and skip without it: the refresh runs loads on it, and a Gateway publishes a
// model only when its data source answers. Each server has its own directory, and each project is compiled and committed here (a deploy applies from a clean working tree).
export const refreshBuild = { directory: path.join(os.tmpdir(), 'dbdatakit-console-e2e-refresh-build'), port: 8541, project: 'refreshing', plan: 'nightly' }
export const modelsGateway = { directory: path.join(os.tmpdir(), 'dbdatakit-console-e2e-models-gateway'), flightPort: 8542, managementPort: 8543 }
export const modelsBuild = { directory: path.join(os.tmpdir(), 'dbdatakit-console-e2e-models-build'), port: 8544, project: 'shop', model: 'shop_sales', projectDirectory: path.join(os.tmpdir(), 'dbdatakit-console-e2e-shop-project') }
// Plan 22a GP03 (the owner's decision of 2026-11-08: who may read a data source is part of the published definition): a Build server and a Gateway for the data source grants story (`gateway-data-source-grants.spec.ts`). Ports
// 8581 to 8583 (a port table: 8581 grants Build, 8582 and 8583 the Gateway's Flight SQL and management listeners; 8541 to 8544, 8551, 8561 and up and 8571 and up belong to other stories). No PostgreSQL is needed: a publish only
// writes the data source file, and the project's connection `postgres` carries a read login that is never used to connect.
export const grantsBuild = { directory: path.join(os.tmpdir(), 'dbdatakit-console-e2e-grants-build'), port: 8581, project: 'grantsdemo', connection: 'postgres', projectDirectory: path.join(os.tmpdir(), 'dbdatakit-console-e2e-grantsdemo-project') }
export const grantsGateway = { directory: path.join(os.tmpdir(), 'dbdatakit-console-e2e-grants-gateway'), flightPort: 8582, managementPort: 8583 }
// Plan 13c D19 (the owner's decision of 2026-11-09: the serving policy is part of the published definition; adopting a data source made by hand): a Build server and a Gateway for the serving policy story (`publish-policy.spec.ts`). Ports
// 8584 to 8586 (a port table: 8584 policy Build, 8585 and 8586 the Gateway's Flight SQL and management listeners; 8581 to 8583 grants, 8541 to 8544, 8551, 8561 and up and 8571 and up belong to other stories). No PostgreSQL is needed.
export const policyBuild = { directory: path.join(os.tmpdir(), 'dbdatakit-console-e2e-policy-build'), port: 8584, project: 'policydemo', connection: 'postgres', projectDirectory: path.join(os.tmpdir(), 'dbdatakit-console-e2e-policydemo-project') }
export const policyGateway = { directory: path.join(os.tmpdir(), 'dbdatakit-console-e2e-policy-gateway'), flightPort: 8585, managementPort: 8586 }
if (!isWorker) {
  const settings = (plan: string) => 'defaults: {connections: [postgres]}\ntracking: { connection: postgres }\nstring_semantics:\n  case: sensitive\n  trailing_space: significant\n  collations:\n    default: { duckdb: NFC, postgres: C }\n' + plan
  const makeProject = (directory: string, name: string, files: Record<string, string>, connectionFile = 'schema: connection/1\nname: postgres\nengine: postgres\ntls:\n  encrypt: false\n  trust_server_certificate: true\n') => {
    const project = path.join(os.tmpdir(), `dbdatakit-console-e2e-${name}-project`)
    fs.rmSync(project, { recursive: true, force: true })
    fs.rmSync(directory, { recursive: true, force: true })
    fs.mkdirSync(directory, { recursive: true })
    for (const [relative, text] of Object.entries(files)) { const file = path.join(project, relative); fs.mkdirSync(path.dirname(file), { recursive: true }); fs.writeFileSync(file, text) }
    // the test database has no certificate to verify: a connection file that says so (the same as the deploy test's)
    fs.mkdirSync(path.join(project, 'connections'), { recursive: true })
    fs.writeFileSync(path.join(project, 'connections', 'postgres.yaml'), connectionFile)
    execFileSync('dotnet', ['exec', buildDll, 'project', 'compile', '--project', project], { stdio: 'ignore' })
    for (const args of [['init', '-q'], ['add', '-A'], ['commit', '-qm', 'The project']])
      execFileSync('git', ['-c', 'user.name=console-e2e', '-c', 'user.email=e2e@example.invalid', ...args], { cwd: project, stdio: 'ignore' })
    execFileSync('dotnet', ['exec', buildDll, 'server', 'project', 'add', name, '--path', project, '--repo', directory], { stdio: 'ignore' })
  }
  const pairBuild = (directory: string, file: string) => {
    const made = execFileSync('dotnet', ['exec', buildDll, 'server', 'pair', '--minutes', '120', '--repo', directory, '--max-role', 'viewer,author,operator,approver,admin'], { encoding: 'utf8' })
    fs.writeFileSync(path.join(scratchDirectory, file), /Pairing code: (\S+)/.exec(made)![1])
  }
  const columns = 'grain: [event_id]\ncolumns:\n  - {name: event_id, type: BIGINT, nullable: false}\n  - {name: event_ts, type: TIMESTAMP, nullable: false}\n  - {name: amount, type: "DECIMAL(14, 2)"}\n'
  makeProject(refreshBuild.directory, refreshBuild.project, {
    'dbdatabuild.yml': settings(`refresh:\n  plans:\n    ${refreshBuild.plan}: { schedule: "0 3 * * *", time_zone: UTC }\n`),
    'models/staging/events.yml': 'name: staging.events\nkind: mapped\n' + columns,
    // a check that one of the three rows the story inserts breaks (amount 99 is above 50), and one that passes
    'models/marts/fct_events.yml': 'schemaVersion: 2\nname: marts.fct_events\nkind: {incremental_by_time_range: {time_column: event_ts}}\n' + columns +
      'loads:\n  incremental:\n    default: true\n    strategy: watermark_append\n    watermark: {column: event_ts, resolver: target_max, on_null: initial, initial: "2000-01-01 00:00:00"}\n' +
      `refresh: { ${refreshBuild.plan}: incremental }\n` +
      'checks:\n  - {id: amount_not_negative, kind: range, column: amount, min: 0}\n  - {id: amount_small, kind: range, column: amount, max: 50}\n',
    'models/marts/fct_events.sql': 'SELECT e.event_id, e.event_ts, e.amount FROM staging.events e\n',
  })
  pairBuild(refreshBuild.directory, 'refresh-build-pairing-code.txt')

  makeProject(modelsBuild.directory, modelsBuild.project, {
    'dbdatabuild.yml': settings(''),
    'models/sales/facts.yml': 'name: sales.facts\nkind: mapped\ngrain: [sale_id]\ncolumns:\n  - {name: sale_id, type: BIGINT, nullable: false}\n  - {name: region, type: "VARCHAR(20)"}\n  - {name: amount, type: "DECIMAL(14, 2)"}\n',
    'semantic/shop_sales.yml': `version: "0.3"\nname: ${modelsBuild.model}\ndescription: Sales by region.\n` +
      'dimensions:\n  sales:\n    attributes:\n      region:\n        type: string\n        bindings:\n          default: {table: sales.facts, column: region}\n' +
      'measures:\n  sales.amount:\n    type: number\n    source: default\n    agg: sum\n    of: {table: sales.facts, column: amount}\n    semantics: {rollup: {default: sum}, spread: blank, status: governed}\n',
  })
  pairBuild(modelsBuild.directory, 'models-build-pairing-code.txt')
  // the read login a publish sends with the model is the one stored in the server's own store (the environment variables are what a command run reads, not what the publish sends)
  if (warehouseConnection) execFileSync('dotnet', ['exec', buildDll, 'secret', 'set', 'dbdata:build:connection:postgres:read', '--stdin', '--repo', modelsBuild.directory], { input: warehouseConnection + ';SSL Mode=Disable\n', stdio: ['pipe', 'ignore', 'inherit'] })

  // the grants story: the project's connection says who may read the data source it is published as (connection/2 with grants), and the read login is a made-up one in the server's own store
  makeProject(grantsBuild.directory, grantsBuild.project, {
    'dbdatabuild.yml': settings(''),
    'models/sales/facts.yml': 'name: sales.facts\nkind: mapped\ngrain: [sale_id]\ncolumns:\n  - {name: sale_id, type: BIGINT, nullable: false}\n',
  }, 'schema: connection/2\nname: postgres\nengine: postgres\ntls:\n  encrypt: false\n  trust_server_certificate: true\ngrants:\n  groups: [finance]\n')
  pairBuild(grantsBuild.directory, 'grants-build-pairing-code.txt')
  execFileSync('dotnet', ['exec', buildDll, 'secret', 'set', 'dbdata:build:connection:postgres:read', '--stdin', '--repo', grantsBuild.directory], { input: 'Host=localhost;Database=ledger;Username=reader;Password=not-used;SSL Mode=Disable\n', stdio: ['pipe', 'ignore', 'inherit'] })
  fs.rmSync(grantsGateway.directory, { recursive: true, force: true })
  fs.mkdirSync(path.join(grantsGateway.directory, 'config', 'gateway'), { recursive: true })
  fs.mkdirSync(path.join(grantsGateway.directory, 'config', 'connections'), { recursive: true })
  fs.writeFileSync(path.join(grantsGateway.directory, 'config', 'server.yaml'), 'schemaVersion: 2\ntls: { mode: selfSigned }\n')
  fs.writeFileSync(path.join(grantsGateway.directory, 'config', 'gateway', 'auth.yaml'), 'jwt:\n  signingKey: console-e2e-signing-key-0123456789abcdef\n  audience: api://console-e2e\n')
  const madeGrantsGateway = execFileSync('dotnet', ['exec', gatewayDll, 'server', 'pair', '--minutes', '120', '--repo', grantsGateway.directory, '--max-role', 'admin'], { encoding: 'utf8' })
  fs.writeFileSync(path.join(scratchDirectory, 'grants-gateway-pairing-code.txt'), /Pairing code: (\S+)/.exec(madeGrantsGateway)![1])
  // the serving policy story: the project's connection carries the Gateway's serving policy (extensions.gateway), and the Gateway has a data source `erp` made by hand (with allow_writes) that the publish adopts
  makeProject(policyBuild.directory, policyBuild.project, {
    'dbdatabuild.yml': settings(''),
    'models/sales/facts.yml': 'name: sales.facts\nkind: mapped\ngrain: [sale_id]\ncolumns:\n  - {name: sale_id, type: BIGINT, nullable: false}\n',
  }, 'schema: connection/1\nname: postgres\nengine: postgres\ntls:\n  encrypt: false\n  trust_server_certificate: true\nextensions:\n  gateway:\n    batch_size: 500\n    classifier: warn\n')
  pairBuild(policyBuild.directory, 'policy-build-pairing-code.txt')
  execFileSync('dotnet', ['exec', buildDll, 'secret', 'set', 'dbdata:build:connection:postgres:read', '--stdin', '--repo', policyBuild.directory], { input: 'Host=localhost;Database=ledger;Username=reader;Password=not-used;SSL Mode=Disable\n', stdio: ['pipe', 'ignore', 'inherit'] })
  fs.rmSync(policyGateway.directory, { recursive: true, force: true })
  fs.mkdirSync(path.join(policyGateway.directory, 'config', 'gateway'), { recursive: true })
  fs.mkdirSync(path.join(policyGateway.directory, 'config', 'connections'), { recursive: true })
  fs.writeFileSync(path.join(policyGateway.directory, 'config', 'server.yaml'), 'schemaVersion: 2\ntls: { mode: selfSigned }\n')
  fs.writeFileSync(path.join(policyGateway.directory, 'config', 'gateway', 'auth.yaml'), 'jwt:\n  signingKey: console-e2e-signing-key-0123456789abcdef\n  audience: api://console-e2e\n')
  const madePolicyGateway = execFileSync('dotnet', ['exec', gatewayDll, 'server', 'pair', '--minutes', '120', '--repo', policyGateway.directory, '--max-role', 'admin'], { encoding: 'utf8' })
  fs.writeFileSync(path.join(scratchDirectory, 'policy-gateway-pairing-code.txt'), /Pairing code: (\S+)/.exec(madePolicyGateway)![1])
  fs.writeFileSync(path.join(policyGateway.directory, 'config', 'connections', 'erp.yaml'), "schema: connection/1\nname: erp\nengine: duckdb\nconnection_string: 'Data Source=:memory:'\nextensions:\n  gateway:\n    allow_writes: true\n    batch_size: 64\n    allowed_directories: ['/data']\n")

  fs.rmSync(modelsGateway.directory, { recursive: true, force: true })
  fs.mkdirSync(path.join(modelsGateway.directory, 'config', 'gateway'), { recursive: true })
  fs.mkdirSync(path.join(modelsGateway.directory, 'config', 'connections'), { recursive: true })
  fs.writeFileSync(path.join(modelsGateway.directory, 'config', 'server.yaml'), 'schemaVersion: 2\ntls: { mode: selfSigned }\n')
  fs.writeFileSync(path.join(modelsGateway.directory, 'config', 'gateway', 'auth.yaml'), 'jwt:\n  signingKey: console-e2e-signing-key-0123456789abcdef\n  audience: api://console-e2e\n')
  const madeModelsGateway = execFileSync('dotnet', ['exec', gatewayDll, 'server', 'pair', '--minutes', '120', '--repo', modelsGateway.directory, '--max-role', 'admin'], { encoding: 'utf8' })
  fs.writeFileSync(path.join(scratchDirectory, 'models-gateway-pairing-code.txt'), /Pairing code: (\S+)/.exec(madeModelsGateway)![1])
}

// Plan 32 DBR29: a Build server for the dashboards-and-reports story (`views-build.spec.ts`), port 8551 (a port table: 8551 views Build). The project is the retail template (its semantic model, sample dashboard and report) in a git
// repository, which is what a workspace is made from. No Gateway is paired with it: publishing is shown off with the reason, and tested by the server's own tests.
export const viewsBuild = { directory: path.join(os.tmpdir(), 'dbdatakit-console-e2e-views-build'), port: 8551, project: 'retail' }
if (!isWorker) {
  fs.rmSync(viewsBuild.directory, { recursive: true, force: true })
  fs.mkdirSync(viewsBuild.directory, { recursive: true })
  const project = path.join(os.tmpdir(), 'dbdatakit-console-e2e-views-project')
  fs.rmSync(project, { recursive: true, force: true })
  execFileSync('dotnet', ['exec', buildDll, 'project', 'create', 'retail', project], { stdio: 'ignore' })
  for (const args of [['init', '-q'], ['add', '-A'], ['commit', '-qm', 'The retail project']])
    execFileSync('git', ['-c', 'user.name=console-e2e', '-c', 'user.email=e2e@example.invalid', ...args], { cwd: project, stdio: 'ignore' })
  execFileSync('dotnet', ['exec', buildDll, 'server', 'project', 'add', viewsBuild.project, '--path', project, '--repo', viewsBuild.directory], { stdio: 'ignore' })
  const made = execFileSync('dotnet', ['exec', buildDll, 'server', 'pair', '--minutes', '120', '--repo', viewsBuild.directory, '--max-role', 'viewer,author,operator,approver,admin'], { encoding: 'utf8' })
  fs.writeFileSync(path.join(scratchDirectory, 'views-build-pairing-code.txt'), /Pairing code: (\S+)/.exec(made)![1])
}

// A runner for the runner story (plan 28a, R19): a real `dbdatarunner` with the sample package of the runner's tests (`DbDataKit.Runner.TestPackage`, published to a folder here) published on the `dev` channel by the story (as version 1, then 2), two tasks:
// `ok` (every hour, so it also has a next time) and `tally` (by hand, with a progress value `seen` its runs move). A pairing code lets the console grant admin; a second, for a publisher, is what the story's `package publish` of
// version 2 (which resets `seen`: a loss) uses. The runner's directory is short: its task socket lives in it and a Unix socket path is limited.
export const runnerServer = {
  directory: path.join(os.tmpdir(), 'ddk-e2e-runner'),
  port: 8496,
  url: 'https://localhost:8496',
  package: path.join(os.tmpdir(), 'ddk-e2e-runner-package'),
  manifests: path.join(os.tmpdir(), 'ddk-e2e-runner-manifests'),
  clientDirectory: path.join(os.tmpdir(), 'ddk-e2e-runner-publisher'),
  dll: path.join(repoRoot, 'DbDataRunner/src/DbDataRunner.Server/bin/Debug/net10.0/dbdatarunner.dll'),
}
if (!isWorker) {
  for (const directory of [runnerServer.directory, runnerServer.package, runnerServer.manifests, runnerServer.clientDirectory]) {
    fs.rmSync(directory, { recursive: true, force: true })
    fs.mkdirSync(directory, { recursive: true })
  }
  execFileSync('dotnet', ['publish', path.join(repoRoot, 'DbDataKit/tests/DbDataKit.Runner.TestPackage/DbDataKit.Runner.TestPackage.csproj'), '-c', 'Debug', '-o', runnerServer.package, '--nologo', '-v', 'q'], { stdio: 'ignore' })
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
  const madePublisher = execFileSync('dotnet', ['exec', runnerServer.dll, 'server', 'pair', '--minutes', '120', '--minutes', '120', '--repo', runnerServer.directory, '--max-role', 'publisher'], { encoding: 'utf8' })
  fs.writeFileSync(path.join(scratchDirectory, 'runner-publisher-code.txt'), /Pairing code: (\S+)/.exec(madePublisher)![1])
}

// A runner and a Build server for the runner-publishing stories (plan 33, UE05 and UE06): `runner-publish.spec.ts` publishes the Build project `orders` (one plan, scheduled every hour) from the Build screens to the runner, `runner-schedule.spec.ts` moves a task of the sample package between schedulers
// on the runner's screens. The runner has three pairing codes: the console's (admin), the Build server's as a publisher that may also set an owner and promote, and the story's own publisher for the sample package. The Build server finds its pairings in `clientDirectory` (the variable it is started with);
// the story pairs it by the runner's own command, because a pairing needs the runner to be running, which it is not when this file is read. The runtime package a Build package needs (`dbdatabuild-runtime`) is not published to this runner: the story stops at the staged version.
export const publishRunner = {
  directory: path.join(os.tmpdir(), 'ddk-e2e-pubrunner'),
  port: 8503,
  url: 'https://localhost:8503',
  clientDirectory: path.join(os.tmpdir(), 'ddk-e2e-pubrunner-sample-client'),
  dll: path.join(repoRoot, 'DbDataRunner/src/DbDataRunner.Server/bin/Debug/net10.0/dbdatarunner.dll'),
}
// Plan 22a GP34: `apps-authoring.spec.ts` starts a Gateway of its own on 8601 (identity home), 8602 (apps host) and 8603 (management); no entry here, the story spawns it.
export const publishBuild = { directory: path.join(os.tmpdir(), 'dbdatakit-console-e2e-publish-build'), port: 8504, project: 'orders', clientDirectory: path.join(os.tmpdir(), 'ddk-e2e-pubbuild-client') }
if (!isWorker) {
  for (const directory of [publishRunner.directory, publishRunner.clientDirectory, publishBuild.directory, publishBuild.clientDirectory]) {
    fs.rmSync(directory, { recursive: true, force: true })
    fs.mkdirSync(directory, { recursive: true })
  }
  const code = (file: string, output: string) => fs.writeFileSync(path.join(scratchDirectory, file), /Pairing code: (\S+)/.exec(output)![1])
  const pairRunner = (roles: string) => execFileSync('dotnet', ['exec', publishRunner.dll, 'server', 'pair', '--minutes', '120', '--minutes', '120', '--repo', publishRunner.directory, '--max-role', roles], { encoding: 'utf8' })
  code('publish-runner-console-code.txt', pairRunner('viewer,operator,admin'))
  code('publish-runner-build-code.txt', pairRunner('viewer,operator,publisher,admin'))
  code('publish-runner-sample-code.txt', pairRunner('publisher'))
  const project = path.join(os.tmpdir(), 'dbdatakit-console-e2e-publish-project')
  fs.rmSync(project, { recursive: true, force: true })
  const write = (relative: string, text: string) => { const file = path.join(project, relative); fs.mkdirSync(path.dirname(file), { recursive: true }); fs.writeFileSync(file, text) }
  const columns = 'grain: [event_id]\ncolumns:\n  - {name: event_id, type: BIGINT, nullable: false}\n  - {name: event_ts, type: TIMESTAMP, nullable: false}\n  - {name: amount, type: "DECIMAL(14, 2)"}\n'
  write('dbdatabuild.yml', 'defaults: {connections: [sqlserver]}\nstring_semantics:\n  case: sensitive\n  trailing_space: ignored\n  collations:\n    default: { duckdb: NFC, sqlserver: Latin1_General_100_CS_AS }\n' +
    'refresh:\n  plans:\n    hourly: { schedule: "15 * * * *", time_zone: UTC }\n')
  write('models/staging/events.yml', 'name: staging.events\nkind: mapped\n' + columns)
  write('models/marts/fct_events.yml', 'name: marts.fct_events\nkind: {incremental_by_time_range: {time_column: event_ts}}\n' + columns +
    'loads:\n  incremental:\n    default: true\n    strategy: watermark_append\n    watermark: {column: event_ts, resolver: target_max, on_null: initial, initial: "2000-01-01 00:00:00"}\n' +
    'refresh: { hourly: incremental }\n')
  write('models/marts/fct_events.sql', 'SELECT e.event_id, e.event_ts, e.amount FROM staging.events e\n')
  write('connections/sqlserver.yaml', 'schema: connection/1\nname: sqlserver\nengine: sqlserver\n')
  execFileSync('dotnet', ['exec', buildDll, 'project', 'compile', '--project', project], { stdio: 'ignore' })
  for (const args of [['init', '-q'], ['add', '-A'], ['commit', '-qm', 'The orders project']])
    execFileSync('git', ['-c', 'user.name=console-e2e', '-c', 'user.email=e2e@example.invalid', ...args], { cwd: project, stdio: 'ignore' })
  execFileSync('dotnet', ['exec', buildDll, 'server', 'project', 'add', publishBuild.project, '--path', project, '--repo', publishBuild.directory], { stdio: 'ignore' })
  code('publish-build-code.txt', execFileSync('dotnet', ['exec', buildDll, 'server', 'pair', '--minutes', '120', '--repo', publishBuild.directory, '--max-role', 'viewer,author,operator,approver,admin'], { encoding: 'utf8' }))
}

// Plan 24a UC08 and UC09: a Build server for the upgrades story (`upgrades.spec.ts`), port 8591 (a port table: 8591 upgrades Build). It runs by hand, not as a service, so the console shows what is available for it and says why it is not
// upgraded from here; a mirror folder (made by the story) stands in for the release feed. Its own directory and port, so nothing the other stories leave in the servers above is in its way.
// a server started by hand, as a person types it: a CI runner agent is a systemd unit and its children inherit these, which says nothing about the child
function byHandEnvironment() {
  const env = { ...(process.env as Record<string, string>) }
  for (const name of ['INVOCATION_ID', 'JOURNAL_STREAM', 'SYSTEMD_EXEC_PID']) delete env[name]
  return env
}
export const upgradeBuild = { directory: path.join(os.tmpdir(), 'dbdatakit-console-e2e-upgrade-build'), port: 8591, mirror: path.join(os.tmpdir(), 'dbdatakit-console-e2e-upgrade-mirror') }
if (!isWorker) {
  fs.rmSync(upgradeBuild.directory, { recursive: true, force: true })
  fs.mkdirSync(upgradeBuild.directory, { recursive: true })
  const made = execFileSync('dotnet', ['exec', buildDll, 'server', 'pair', '--minutes', '120', '--repo', upgradeBuild.directory, '--max-role', 'viewer,author,operator,approver,admin'], { encoding: 'utf8' })
  fs.writeFileSync(path.join(scratchDirectory, 'upgrade-build-pairing-code.txt'), /Pairing code: (\S+)/.exec(made)![1])
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
    {
      command: ['dotnet', 'exec', JSON.stringify(buildDll), 'server', 'run', '--repo', JSON.stringify(authoringBuild.directory), '--listen', `https://localhost:${authoringBuild.port}`].join(' '),
      url: `https://localhost:${authoringBuild.port}/health/live`,
      ignoreHTTPSErrors: true,
      reuseExistingServer: false,
      timeout: 60_000,
      stdout: 'pipe' as const,
      stderr: 'pipe' as const,
    },
    {
      command: ['dotnet', 'exec', JSON.stringify(buildDll), 'server', 'run', '--repo', JSON.stringify(endToEndBuild.directory), '--listen', `https://localhost:${endToEndBuild.port}`].join(' '),
      url: `https://localhost:${endToEndBuild.port}/health/live`,
      ignoreHTTPSErrors: true,
      reuseExistingServer: false,
      timeout: 60_000,
      stdout: 'pipe' as const,
      stderr: 'pipe' as const,
      env: warehouseConnection ? { DBDATA_BUILD_POSTGRES_READ: warehouseConnection, DBDATA_BUILD_POSTGRES_WRITE: warehouseConnection } : {},
    },
    {
      command: ['dotnet', 'exec', JSON.stringify(buildDll), 'server', 'run', '--repo', JSON.stringify(refreshBuild.directory), '--listen', `https://localhost:${refreshBuild.port}`].join(' '),
      url: `https://localhost:${refreshBuild.port}/health/live`,
      ignoreHTTPSErrors: true,
      reuseExistingServer: false,
      timeout: 60_000,
      stdout: 'pipe' as const,
      stderr: 'pipe' as const,
      env: warehouseConnection ? { DBDATA_BUILD_POSTGRES_READ: warehouseConnection, DBDATA_BUILD_POSTGRES_WRITE: warehouseConnection } : {},
    },
    {
      command: ['dotnet', 'exec', JSON.stringify(buildDll), 'server', 'run', '--repo', JSON.stringify(modelsBuild.directory), '--listen', `https://localhost:${modelsBuild.port}`].join(' '),
      url: `https://localhost:${modelsBuild.port}/health/live`,
      ignoreHTTPSErrors: true,
      reuseExistingServer: false,
      timeout: 60_000,
      stdout: 'pipe' as const,
      stderr: 'pipe' as const,
      env: warehouseConnection ? { DBDATA_BUILD_POSTGRES_READ: warehouseConnection, DBDATA_BUILD_POSTGRES_WRITE: warehouseConnection } : {},
    },
    {
      command: ['dotnet', 'exec', JSON.stringify(gatewayDll), 'server', 'run',
        `--Gateway:DataDirectory=${JSON.stringify(modelsGateway.directory)}`, '--Gateway:Listen:Address=127.0.0.1', `--Gateway:Listen:Port=${modelsGateway.flightPort}`,
        '--Gateway:Management:Enabled=true', '--Gateway:Management:Address=127.0.0.1', `--Gateway:Management:Port=${modelsGateway.managementPort}`].join(' '),
      url: `https://localhost:${modelsGateway.managementPort}/health/live`,
      ignoreHTTPSErrors: true,
      reuseExistingServer: false,
      timeout: 60_000,
      stdout: 'pipe' as const,
      stderr: 'pipe' as const,
    },
    {
      command: ['dotnet', 'exec', JSON.stringify(buildDll), 'server', 'run', '--repo', JSON.stringify(grantsBuild.directory), '--listen', `https://localhost:${grantsBuild.port}`].join(' '),
      url: `https://localhost:${grantsBuild.port}/health/live`,
      ignoreHTTPSErrors: true,
      reuseExistingServer: false,
      timeout: 60_000,
      stdout: 'pipe' as const,
      stderr: 'pipe' as const,
    },
    {
      command: ['dotnet', 'exec', JSON.stringify(gatewayDll), 'server', 'run',
        `--Gateway:DataDirectory=${JSON.stringify(grantsGateway.directory)}`, '--Gateway:Listen:Address=127.0.0.1', `--Gateway:Listen:Port=${grantsGateway.flightPort}`,
        '--Gateway:Management:Enabled=true', '--Gateway:Management:Address=127.0.0.1', `--Gateway:Management:Port=${grantsGateway.managementPort}`].join(' '),
      url: `https://localhost:${grantsGateway.managementPort}/health/live`,
      ignoreHTTPSErrors: true,
      reuseExistingServer: false,
      timeout: 60_000,
      stdout: 'pipe' as const,
      stderr: 'pipe' as const,
    },
    {
      command: ['dotnet', 'exec', JSON.stringify(buildDll), 'server', 'run', '--repo', JSON.stringify(policyBuild.directory), '--listen', `https://localhost:${policyBuild.port}`].join(' '),
      url: `https://localhost:${policyBuild.port}/health/live`,
      ignoreHTTPSErrors: true,
      reuseExistingServer: false,
      timeout: 60_000,
      stdout: 'pipe' as const,
      stderr: 'pipe' as const,
    },
    {
      command: ['dotnet', 'exec', JSON.stringify(gatewayDll), 'server', 'run',
        `--Gateway:DataDirectory=${JSON.stringify(policyGateway.directory)}`, '--Gateway:Listen:Address=127.0.0.1', `--Gateway:Listen:Port=${policyGateway.flightPort}`,
        '--Gateway:Management:Enabled=true', '--Gateway:Management:Address=127.0.0.1', `--Gateway:Management:Port=${policyGateway.managementPort}`].join(' '),
      url: `https://localhost:${policyGateway.managementPort}/health/live`,
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
    {
      command: ['dotnet', 'exec', JSON.stringify(buildDll), 'server', 'run', '--repo', JSON.stringify(publishBuild.directory), '--listen', `https://localhost:${publishBuild.port}`].join(' '),
      url: `https://localhost:${publishBuild.port}/health/live`,
      ignoreHTTPSErrors: true,
      reuseExistingServer: false,
      timeout: 60_000,
      stdout: 'pipe' as const,
      stderr: 'pipe' as const,
      env: { DBDATARUNNER_CLIENT_DIR: publishBuild.clientDirectory },
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
      command: ['dotnet', 'exec', JSON.stringify(gatewayDll), 'server', 'run',
        `--Gateway:DataDirectory=${JSON.stringify(gatewayDirectory)}`, '--Gateway:Listen:Address=127.0.0.1', `--Gateway:Listen:Port=${gatewayPort}`,
        '--Gateway:Management:Enabled=true', '--Gateway:Management:Address=127.0.0.1', `--Gateway:Management:Port=${gatewayManagementPort}`].join(' '),
      url: `${gatewayUrl}/health/live`,
      ignoreHTTPSErrors: true,
      reuseExistingServer: false,
      timeout: 60_000,
      stdout: 'pipe',
      stderr: 'pipe',
    },
    {
      command: ['dotnet', 'exec', JSON.stringify(buildDll), 'server', 'run', '--repo', JSON.stringify(monitorBuild.directory), '--listen', `https://localhost:${monitorBuild.port}`, '--scheduler'].join(' '),
      url: `https://localhost:${monitorBuild.port}/health/live`,
      ignoreHTTPSErrors: true,
      reuseExistingServer: false,
      timeout: 60_000,
      stdout: 'pipe' as const,
      stderr: 'pipe' as const,
    },
    {
      command: ['dotnet', 'exec', JSON.stringify(buildDll), 'server', 'run', '--repo', JSON.stringify(upgradeBuild.directory), '--listen', `https://localhost:${upgradeBuild.port}`].join(' '),
      url: `https://localhost:${upgradeBuild.port}/health/live`,
      env: { ...byHandEnvironment(), DBDATA_UPGRADE_MIRROR: upgradeBuild.mirror },   // the server prepares its own upgrade from the same mirror folder the console reads
      ignoreHTTPSErrors: true,
      reuseExistingServer: false,
      timeout: 60_000,
      stdout: 'pipe' as const,
      stderr: 'pipe' as const,
    },
    {
      command: ['dotnet', 'exec', JSON.stringify(buildDll), 'server', 'run', '--repo', JSON.stringify(viewsBuild.directory), '--listen', `https://localhost:${viewsBuild.port}`].join(' '),
      url: `https://localhost:${viewsBuild.port}/health/live`,
      ignoreHTTPSErrors: true,
      reuseExistingServer: false,
      timeout: 60_000,
      stdout: 'pipe' as const,
      stderr: 'pipe' as const,
    },
    {
      command: ['dotnet', 'exec', JSON.stringify(buildDll), 'server', 'run', '--repo', JSON.stringify(inventoryBuild.directory), '--listen', `https://localhost:${inventoryBuild.port}`].join(' '),
      url: `https://localhost:${inventoryBuild.port}/health/live`,
      ignoreHTTPSErrors: true,
      reuseExistingServer: false,
      timeout: 60_000,
      stdout: 'pipe' as const,
      stderr: 'pipe' as const,
    },
    {
      command: ['dotnet', 'exec', JSON.stringify(syncDll), 'server', 'run', '--repo', JSON.stringify(inventorySync.directory), '--url', `https://localhost:${inventorySync.port}`].join(' '),
      url: `https://localhost:${inventorySync.port}/api/health`,
      ignoreHTTPSErrors: true,
      reuseExistingServer: false,
      timeout: 90_000,
      stdout: 'pipe' as const,
      stderr: 'pipe' as const,
      env: { Kestrel__Certificates__Default__Path: syncCertificate, Kestrel__Certificates__Default__Password: syncCertificatePassword },
    },
    {
      command: ['dotnet', 'exec', JSON.stringify(gatewayDll), 'server', 'run',
        `--Gateway:DataDirectory=${JSON.stringify(monitorGateway.directory)}`, '--Gateway:Listen:Address=127.0.0.1', `--Gateway:Listen:Port=${monitorGateway.flightPort}`,
        '--Gateway:Management:Enabled=true', '--Gateway:Management:Address=127.0.0.1', `--Gateway:Management:Port=${monitorGateway.managementPort}`].join(' '),
      url: `https://localhost:${monitorGateway.managementPort}/health/live`,
      ignoreHTTPSErrors: true,
      reuseExistingServer: false,
      timeout: 60_000,
      stdout: 'pipe' as const,
      stderr: 'pipe' as const,
    },
    {
      command: ['dotnet', 'exec', JSON.stringify(syncDll), 'server', 'run', '--repo', JSON.stringify(monitorSync.directory), '--url', `https://localhost:${monitorSync.port}`].join(' '),
      url: `https://localhost:${monitorSync.port}/api/health`,
      ignoreHTTPSErrors: true,
      reuseExistingServer: false,
      timeout: 90_000,
      stdout: 'pipe' as const,
      stderr: 'pipe' as const,
      env: { Kestrel__Certificates__Default__Path: syncCertificate, Kestrel__Certificates__Default__Password: syncCertificatePassword },
    },
    {
      command: ['dotnet', 'exec', JSON.stringify(syncDll), 'server', 'run', '--repo', JSON.stringify(qualitySync.directory), '--url', `https://localhost:${qualitySync.port}`].join(' '),
      url: `https://localhost:${qualitySync.port}/api/health`,
      ignoreHTTPSErrors: true,
      reuseExistingServer: false,
      timeout: 90_000,
      stdout: 'pipe' as const,
      stderr: 'pipe' as const,
      env: { Kestrel__Certificates__Default__Path: syncCertificate, Kestrel__Certificates__Default__Password: syncCertificatePassword },
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
