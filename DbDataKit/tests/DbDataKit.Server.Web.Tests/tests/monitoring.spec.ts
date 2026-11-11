import { expect, test, type APIRequestContext, type BrowserContext, type Page } from '@playwright/test'
import { execFileSync } from 'node:child_process'
import fs from 'node:fs'
import http2 from 'node:http2'
import path from 'node:path'
import { DatabaseSync } from 'node:sqlite'
import { fileURLToPath } from 'node:url'
import { monitorBuild, monitorGateway, monitorSync, scratchDirectory } from '../playwright.config'

// Plan 13a, M12 (the done-when of M11): the console's Monitoring page across real servers. A Build server that runs schedules has a project whose plan runs every minute and fails (its connection has no login), a
// Sync server has a replication that is an hour behind, and a Gateway has locked a user out; the page lists the failed run, the lagging replication and the Gateway's notice together, failed first. Pausing the plan
// stops its next run, and releasing it lets the run happen again. The servers are the ones playwright.config.ts starts for this file (apart from the servers of the other stories, whose pairing codes those spend).
const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../../..')
const consoleDll = path.join(repoRoot, 'DbDataKit/src/DbDataKit.Server/bin/Debug/net10.0/dbdatakit.dll')

const buildName = 'Monitor build'
const syncName = 'Monitor sync'
const gatewayName = 'Monitor gateway'
const replication = 'hourly-orders'
const planSubject = `${monitorBuild.project}: ${monitorBuild.connection}/${monitorBuild.plan}`

async function signInAsAdmin(page: Page, context: BrowserContext, baseURL: string) {
  const cdp = await context.newCDPSession(page)
  await cdp.send('WebAuthn.enable')
  await cdp.send('WebAuthn.addVirtualAuthenticator', {
    options: { protocol: 'ctap2', transport: 'internal', hasResidentKey: true, hasUserVerification: true, isUserVerified: true, automaticPresenceSimulation: true },
  })
  const made = execFileSync('dotnet', ['exec', consoleDll, 'user', 'invite', '--email', `monitoring-${Date.now()}-${Math.random().toString(36).slice(2, 8)}@example.com`, '--role', 'admin', '--url', baseURL, '--repo', scratchDirectory], { encoding: 'utf8' })
  await page.goto(`/invite#${/\/invite#(\S+)/.exec(made)![1]}`)
  await page.getByTestId('invite-name-input').fill('Monitoring Tester')
  await page.getByTestId('invite-enrol-button').click()
  await expect(page.getByTestId('home-page')).toBeVisible()
}

/** Pairs a server with the code made for it, through the Servers page, and waits for it to answer. */
async function pair(page: Page, url: string, codeFile: string, name: string) {
  await page.goto('/instances')
  await expect(page.getByTestId('instances-page')).toBeVisible()
  await page.getByTestId('pair-url-input').fill(url)
  await page.getByTestId('pair-code-input').fill(fs.readFileSync(path.join(scratchDirectory, codeFile), 'utf8').trim())
  await page.getByTestId('pair-name-input').fill(name)
  await page.getByTestId('pair-button').click()
  await expect(page.getByTestId(`instance-row-${name}`).getByTestId('instance-status')).toHaveText('Ready', { timeout: 30_000 })
}

async function instanceId(request: APIRequestContext, name: string): Promise<string> {
  const list = await (await request.get('/api/instances')).json() as { id: string; name: string }[]
  return list.find((i) => i.name === name)!.id
}

interface Job { connection: string; plan: string; next_due: string | null; last_started: string | null; last_exit: number | null; paused: boolean }
const schedule = async (request: APIRequestContext, build: string): Promise<Job> => {
  const { jobs } = await (await request.get(`/relay/${build}/api/v1/projects/${monitorBuild.project}/schedule`)).json() as { jobs: Job[] }
  return jobs.find((j) => j.plan === monitorBuild.plan)!
}

/** A replication of a SQL Server source with change data capture, whose last applied change is an hour older than where the source has got to: the figures the lag endpoint reads are the state database's. */
async function makeLaggingReplication(request: APIRequestContext, sync: string) {
  const relay = `/relay/${sync}/api`
  const put = async (url: string, body: unknown) => { const r = await request.put(`${relay}${url}`, { data: body }); expect(r.ok(), `${url}: ${r.status()} ${await r.text()}`).toBe(true) }
  await put('/connections/lag-source', { name: 'lag-source', driverType: 'MsSql', host: 'localhost', database: 'AppDb', authMode: 'SqlAuth', userId: 'sa', password: 'not-used-the-lag-is-read-from-the-state-database' })
  await put(`/replications/${replication}`, {
    name: replication, enabled: false,
    endpoints: { source: { connectionName: 'lag-source', database: 'AppDb' }, target: { connectionName: 'lag-source', database: 'Warehouse' } },
    scheduling: { mode: 'Continuous', frequencySeconds: 3600 },
    changeProcessing: { reader: { kind: 'MsSqlCdc', options: {} }, cache: { kind: 'MsSqlStagingTable' }, writer: { kind: 'MsSqlMerge' } },
  })
  await put(`/replications/${replication}/table-mappings/orders`, { name: 'orders', sources: [{ schema: 'dbo', table: 'orders' }], targets: [{ schema: 'dbo', table: 'orders' }] })

  // What a pass and the polling gate would have written: the mapping applied a position that committed at 10:00, and the source's latest check says it is at 11:00.
  const applied = new Date(Date.now() - 2 * 3_600_000).toISOString()
  const current = new Date(Date.now() - 3_600_000).toISOString()
  // The Sync server is running and writes this file too (its scheduler and heartbeat); the file is not in WAL mode, so a second writer is refused at once unless it waits.
  // Wait for the lock (busy_timeout) and write everything in one immediate transaction, retried when the lock is still not ours: nothing here depends on how fast the machine is.
  const db = new DatabaseSync(path.join(monitorSync.directory, 'state.db'))
  try {
    db.exec('PRAGMA busy_timeout = 30000')
    // The position is a checkpoint of the progress module (plan 28c, E15): a head row and one row per part, under package `dbdatasync`, channel `local`, the replication as the task, and the key `mapping|source table`.
    // Times are the state store's SQLite text, UTC with seven fractional digits.
    const at = (iso: string) => iso.replace('Z', '0000Z')
    const key = ['dbdatasync', 'local', replication, 'watermark', 'orders|lag-source/[AppDb]/[dbo].[orders]']
    const write = () => {
      db.exec('BEGIN IMMEDIATE')
      try {
        db.prepare('INSERT OR REPLACE INTO dbdatakit_runner_progress_main_checkpoint_head (package, channel, task, name, item_key, row_version, updated_at, updated_by) VALUES (?, ?, ?, ?, ?, ?, ?, ?)')
          .run(...key, 1, at(applied), 'dbdatasync')
        const part = db.prepare('INSERT OR REPLACE INTO dbdatakit_runner_progress_main_checkpoint_part (package, channel, task, name, item_key, part, value_type, value_text, value_at) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)')
        part.run(...key, 'position', 'Text', '0x0000002A000000100001', null)
        part.run(...key, 'position_time', 'Time', null, at(applied))
        part.run(...key, 'intent', 'Text', 'Changes', null)
        part.run(...key, 'hold', 'Text', 'None', null)
        db.prepare('INSERT INTO dbdatakit_sync_state_main_change_check (connection_name, source_database, source_kind, value_text, checked_at, source_at) VALUES (?, ?, ?, ?, ?, ?)')
          .run('lag-source', 'AppDb', 'MsSqlCdc', '0x0000002A000000200001', at(current), at(current))
        db.exec('COMMIT')
      } catch (e) {
        try { db.exec('ROLLBACK') } catch { /* nothing open */ }
        throw e
      }
    }
    for (let attempt = 1; ; attempt++) {
      try { write(); break } catch (e) {
        if (attempt >= 10 || !/database is locked|busy/i.test(String(e))) throw e
        await new Promise(r => setTimeout(r, 500))   // the file is held by the server's own write; try again, the wait is not what the test asserts on
      }
    }
  } finally { db.close() }
}

/** Failed logins to the Gateway's Flight port: the handshake with a wrong password, as many times as the lockout counts. Nothing else of Flight is needed. */
async function failLogins(port: number, user: string, times: number) {
  const session = http2.connect(`https://localhost:${port}`, { rejectUnauthorized: false })
  try {
    for (let i = 0; i < times; i++) {
      await new Promise<void>((resolve, reject) => {
        const call = session.request({
          ':method': 'POST', ':path': '/arrow.flight.protocol.FlightService/Handshake', 'content-type': 'application/grpc', te: 'trailers',
          authorization: `Basic ${Buffer.from(`${user}:wrong-${i}`).toString('base64')}`,
        })
        call.on('error', reject)
        call.on('close', () => resolve())
        call.resume()
        call.end(Buffer.from([0, 0, 0, 0, 0]))
      })
    }
  } finally { session.close() }
}

test.describe.serial('monitoring across real servers', () => {
  let build = ''
  let sync = ''
  let gateway = ''

  test('a failed plan, a lagging replication and a Gateway notice are listed together, failed first', async ({ page, context, baseURL }) => {
    test.setTimeout(240_000)
    await signInAsAdmin(page, context, baseURL!)
    await pair(page, `https://localhost:${monitorBuild.port}`, 'monitor-build-pairing-code.txt', buildName)
    await pair(page, `https://localhost:${monitorSync.port}`, 'monitor-sync-pairing-code.txt', syncName)
    await pair(page, `https://localhost:${monitorGateway.managementPort}`, 'monitor-gateway-pairing-code.txt', gatewayName)
    build = await instanceId(page.request, buildName)
    sync = await instanceId(page.request, syncName)
    gateway = await instanceId(page.request, gatewayName)

    await makeLaggingReplication(page.request, sync)
    await failLogins(monitorGateway.flightPort, 'intruder', 5)

    // The scheduler notes the plan when it first sees it and runs it at the next minute; the run ends with an exit code because nothing is set up to log in with.
    await expect.poll(async () => (await schedule(page.request, build)).last_exit, { timeout: 150_000, intervals: [3_000] }).not.toBeNull()
    expect((await schedule(page.request, build)).last_exit).not.toBe(0)

    await page.goto('/monitoring')
    await expect(page.getByTestId('monitoring-page')).toBeVisible()
    const rows = page.getByTestId('monitoring-row')
    const failed = rows.filter({ hasText: planSubject })
    const lagging = rows.filter({ hasText: replication })
    const notice = rows.filter({ hasText: 'Login locked out' })
    await expect(failed).toHaveAttribute('data-state', 'failed')
    await expect(failed).toContainText(buildName)
    await expect(lagging).toHaveAttribute('data-state', 'lagging')
    await expect(lagging).toContainText('60 min behind')
    await expect(lagging).toContainText(syncName)
    await expect(notice).toHaveAttribute('data-state', 'event')
    await expect(notice).toContainText('intruder')
    await expect(notice).toContainText(gatewayName)
    await expect(notice.getByTestId('monitoring-toggle')).toHaveCount(0)

    // Together at the top: before them only other failures, and after them nothing but notices, holds and what is fine.
    const states = await rows.evaluateAll((all) => all.map((row) => row.getAttribute('data-state')))
    const subjects = await rows.evaluateAll((all) => all.map((row) => row.textContent ?? ''))
    const at = (text: string) => subjects.findIndex((s) => s.includes(text))
    expect(states[at(planSubject)]).toBe('failed')
    expect(states.slice(0, at(planSubject) + 1).every((s) => s === 'failed')).toBe(true)
    expect(at(planSubject)).toBeLessThan(at(replication))
    expect(states.slice(0, at(replication) + 1).every((s) => s === 'failed' || s === 'lagging')).toBe(true)
    expect(at(replication)).toBeLessThan(at('Login locked out'))
  })

  test('pausing the plan stops its next run, and resuming lets it run again', async ({ page, context, baseURL }) => {
    test.setTimeout(240_000)
    await signInAsAdmin(page, context, baseURL!)
    await page.goto('/monitoring')
    const row = page.getByTestId('monitoring-row').filter({ hasText: planSubject })
    await expect(row).toBeVisible()
    const before = (await schedule(page.request, build)).last_started
    expect(before).not.toBeNull()

    await row.getByTestId('monitoring-toggle').click()
    await expect(row.getByTestId('monitoring-toggle')).toHaveText('Resume')
    await expect(row.getByTestId('monitoring-state')).toContainText('held')
    expect((await schedule(page.request, build)).paused).toBe(true)

    // The plan is due every minute: after two minute marks have passed nothing has run.
    await page.waitForTimeout(125_000)
    const held = await schedule(page.request, build)
    expect(held.last_started).toBe(before)
    expect(held.paused).toBe(true)

    await row.getByTestId('monitoring-toggle').click()
    await expect(row.getByTestId('monitoring-toggle')).toHaveText('Pause')
    await expect.poll(async () => (await schedule(page.request, build)).last_started, { timeout: 100_000, intervals: [3_000] }).not.toBe(before)
  })
})
