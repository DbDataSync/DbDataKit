import { test, expect, type Page } from '../fixtures'
import { execFileSync } from 'node:child_process'
import fs from 'node:fs'
import path from 'node:path'
import { consoleScratch } from '../console-paths'
import { screenshotDir } from '../screenshots'
import { syncDirectory, syncRunner } from '../playwright.config'
import { DB_NAME, MSSQL_PORT, querySql, runSql, SA_PASSWORD } from '../test-db'

const screenshotsDir = screenshotDir('replication-runner-move')

// Plan 28c E12 and E22: the browser story of a replication run by a real runner, against the real Sync server (through the console) and a real runner that runs the real engine (the runtime package of the config is the
// TaskRunner's build output, not a stand-in). The Runner is Sync's only scheduler (owner decision S2, 2026-11-14): a periodic replication of SQL Server to SQL Server (Change Tracking and merge, two tables of the suite's
// database) is published from the Runner screen with its slot bound to the runner's copy of the connection, and Run now is a drain of the runner: the rows arrive once each, the replication's Runner tab shows the runner's
// last run, and the Sync server's own history (runs with a process of its own) stays empty. (Until E22 this story also cut the replication over from this server's worker and rolled it back; the hand-over is gone.)
// It disables the other enabled replications for the time of the story (a published set holds every enabled replication of the repository) and enables them again.
const REPLICATION = 'move-demo'
const CONNECTION = 'move-db'
const SOURCE = 'MoveSrc'
const TARGET = 'MoveTgt'
const PACKAGE = 'move-set'
const CONFIG = path.join(syncDirectory, 'config')
const disabledByThisStory: string[] = []

const count = (table: string) => Number(querySql(`SET NOCOUNT ON; SELECT COUNT(*) FROM dbo.[${table}]`, DB_NAME).trim().split(/\s+/)[0])
const insert = (from: number, to: number) => runSql(`INSERT INTO dbo.[${SOURCE}] (Id, Name) SELECT v.n, CONCAT(N'row-', v.n) FROM (SELECT TOP (${to - from + 1}) ${from - 1} + ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n FROM sys.all_objects) v`, DB_NAME)

async function openRunnerTab(page: Page) {
  await page.goto(`/replications/${REPLICATION}/runner`)
  await expect(page.getByTestId('replication-runner')).toBeVisible()
}

test.afterEach(async ({ page }) => {
  for (const name of disabledByThisStory) await page.request.put(`/api/replications/${name}/enabled`, { data: { enabled: true } }).catch(() => undefined)
})

test.afterAll(() => {
  // the runner this story linked is the Sync server's for the rest of the run (`runner-link.json` beside its state): the story of `replication-runner-owner.spec.ts`, which sorts after this one, starts from "no runner is linked"
  fs.rmSync(path.join(syncDirectory, 'runner-link.json'), { force: true })
  fs.rmSync(path.join(syncRunner.directory, 'connections', `${CONNECTION}.yaml`), { force: true })
  try { runSql(`DROP TABLE IF EXISTS dbo.[${SOURCE}]; DROP TABLE IF EXISTS dbo.[${TARGET}];`, DB_NAME) } catch { /* the database may be gone */ }
})

test('a replication published to a real runner is run by its drain on Run now, and no row is lost or doubled', async ({ page }) => {
  test.setTimeout(600_000)

  // ---- the data: two tables of the suite's database, Change Tracking on the source
  runSql(`DROP TABLE IF EXISTS dbo.[${SOURCE}]; DROP TABLE IF EXISTS dbo.[${TARGET}];
    CREATE TABLE dbo.[${SOURCE}] (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(50) NOT NULL); ALTER TABLE dbo.[${SOURCE}] ENABLE CHANGE_TRACKING;
    CREATE TABLE dbo.[${TARGET}] (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(50) NOT NULL);`, DB_NAME)
  insert(1, 200)

  // ---- a published set holds every enabled replication of the repository: the others are held back for this story
  const listed = await page.request.get('/api/replications')
  for (const r of (await listed.json()) as { name: string; enabled: boolean }[]) {
    if (r.name !== REPLICATION && r.enabled) {
      expect((await page.request.put(`/api/replications/${r.name}/enabled`, { data: { enabled: false } })).ok()).toBe(true)
      disabledByThisStory.push(r.name)
    }
  }

  // ---- the connection and the replication, made as the screens make them; the runner's copy of the connection is the file Sync wrote (its login reaches the runner as an environment secret of the config)
  const connection = await page.request.put(`/api/connections/${CONNECTION}`, {
    data: { name: CONNECTION, driverType: 'MsSql', host: '127.0.0.1', port: MSSQL_PORT, database: DB_NAME, authMode: 'SqlAuth', userId: 'sa', password: SA_PASSWORD, properties: { TrustServerCertificate: 'true', Encrypt: 'false' } },
  })
  expect(connection.ok(), await connection.text()).toBe(true)
  fs.mkdirSync(path.join(syncRunner.directory, 'connections'), { recursive: true })
  fs.copyFileSync(path.join(CONFIG, 'connections', `${CONNECTION}.yaml`), path.join(syncRunner.directory, 'connections', `${CONNECTION}.yaml`))
  const made = await page.request.put(`/api/replications/${REPLICATION}`, {
    data: {
      name: REPLICATION, enabled: true,
      endpoints: { source: { connectionName: CONNECTION, database: DB_NAME }, target: { connectionName: CONNECTION, database: DB_NAME } },
      scheduling: { mode: 'Periodic', cronExpression: '0 3 * * *' },   // never due while the story runs: it runs when Run now is pressed
      changeProcessing: { reader: { kind: 'MsSqlChangeTracking', options: {} }, cache: { kind: 'MsSqlStagingTable' }, writer: { kind: 'MsSqlMerge' } },
    },
  })
  expect(made.ok(), await made.text()).toBe(true)
  const mapping = await page.request.put(`/api/replications/${REPLICATION}/table-mappings/main`, {
    data: {
      name: 'main', sources: [{ schema: 'dbo', table: SOURCE }], targets: [{ schema: 'dbo', table: TARGET }],
      columnMappings: [{ sourceColumn: 'Id', targetColumn: 'Id' }, { sourceColumn: 'Name', targetColumn: 'Name' }], verification: [],
    },
  })
  expect(mapping.ok(), await mapping.text()).toBe(true)
  expect((await page.request.post(`/api/replications/${REPLICATION}/table-mappings/main/refresh-metadata`)).ok()).toBe(true)

  // ---- 1. the runner is set and the replication set published to it, from the Runner screen
  const code = fs.readFileSync(path.join(consoleScratch, 'sync-runner-move-pairing-code.txt'), 'utf8').trim()
  execFileSync('dotnet', ['exec', syncRunner.dll, 'package', 'pair', syncRunner.url, '--code', code, '--name', 'move-runner', '--client-dir', syncRunner.moveClientDirectory], { stdio: 'ignore' })
  await page.goto('/runner')
  await expect(page.getByTestId('runner-page')).toBeVisible()
  await page.getByTestId('runner-link-server').fill('move-runner')
  await page.getByTestId('runner-link-channel').fill('prod')
  await page.getByTestId('runner-link-package').fill(PACKAGE)
  await page.getByTestId('runner-link-client-dir').fill(syncRunner.moveClientDirectory)
  await page.getByTestId('runner-link-save').click()
  await expect(page.getByTestId('runner-check-table')).toBeVisible({ timeout: 60_000 })
  await expect(page.getByTestId('runner-check-row-' + REPLICATION)).toContainText('periodic')
  await page.getByTestId('runner-slot-input-move_db').fill(CONNECTION)
  await page.getByTestId('runner-publish-stage').click()
  await expect(page.getByTestId('runner-publish-done')).toContainText(`Staged ${PACKAGE} on prod: version`, { timeout: 180_000 })
  await expect(page.getByTestId('runner-package-state')).toHaveAttribute('data-state', 'serving', { timeout: 180_000 })
  await page.screenshot({ path: path.join(screenshotsDir, '01-published.png'), fullPage: true })

  // ---- 2. the replication's Runner tab: the runner runs it, and lists its task
  const syncRuns = async () => ((await (await page.request.get(`/api/replications/${REPLICATION}/runs?limit=200`)).json()) as { runs: { runId: string; pid: number | null; rowsRead: number }[] }).runs.filter((r) => r.pid !== null)
  await openRunnerTab(page)
  await expect(page.getByTestId('runner-owner')).toContainText('runner')
  await expect(page.getByTestId('runner-row')).toBeVisible({ timeout: 60_000 })
  await page.screenshot({ path: path.join(screenshotsDir, '02-on-the-runner.png'), fullPage: true })

  // ---- 3. Run now is a run of the runner's drain: the rows arrive, once each, and this server's own history has no run of its own
  insert(201, 700)
  expect((await page.request.post(`/api/replications/${REPLICATION}/runs`)).status()).toBe(202)
  await expect.poll(() => count(TARGET), { timeout: 180_000 }).toBe(700)
  await page.reload()
  await expect(page.getByTestId('runner-row-last')).toContainText('succeeded', { timeout: 120_000 })
  expect((await syncRuns()).length).toBe(0)
  await page.screenshot({ path: path.join(screenshotsDir, '03-moved-on-the-runner.png'), fullPage: true })

  expect(querySql(`SET NOCOUNT ON; SELECT COUNT(DISTINCT Id) FROM dbo.[${TARGET}]`, DB_NAME).trim()).toBe('700')
})
