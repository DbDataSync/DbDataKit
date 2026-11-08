import { expect, test } from '@playwright/test'
import { createHash, randomUUID } from 'node:crypto'
import fs from 'node:fs'
import path from 'node:path'
import { DatabaseSync } from 'node:sqlite'
import { qualitySync, scratchDirectory } from '../playwright.config'
import { signInNewAdmin } from './people'

// Plan 23a, FQ17: the console's Quality page over a real Sync server. The server's state database is seeded with what an evaluation leaves (a failing `block` check, its block, and two results with the keys of the rows that failed;
// the result tables are the `quality` module's, which the server creates when it starts), and a replication whose only target cannot be reached holds the same check. The page lists the dataset as blocked with the measure; opening
// the check shows its history and the sample keys; evaluating on the Sync server writes an `error` result (the target is down) that does not lift the block (plan 23a QF10), and the history gets a third row. It sorts after
// `identity-home` (it invites an administrator, which uses the first-run invitation up when it sorts before).
const dataset = 'dbo.orders'
const checkId = 'name_present'
const replication = 'orders-checks'

const at = (minutesAgo: number) => new Date(Date.now() - minutesAgo * 60_000).toISOString().replace('Z', '0000Z')

/** What an evaluation leaves in the install-scope tables of the `quality` module on SQLite (`dbdatakit_quality_<table>`): two results, the first passing and the second failing, and the status that the second set. */
async function seed() {
  const hash = createHash('sha256').update(dataset).digest('hex')
  const db = new DatabaseSync(path.join(qualitySync.directory, 'state.db'))
  try {
    db.exec('PRAGMA busy_timeout = 30000')
    const write = () => {
      db.exec('BEGIN IMMEDIATE')
      try {
        const result = db.prepare('INSERT INTO dbdatakit_quality_check_result (id, dataset_hash, check_id, dataset, status, measured, threshold, sample_keys, observed, message, run_id, evaluated_at) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)')
        result.run(randomUUID(), hash, checkId, dataset, 'pass', 0, 0, null, null, null, 'run-1', at(120))
        const failing = randomUUID()
        result.run(failing, hash, checkId, dataset, 'fail', 1, 0, JSON.stringify(['Id=2']), null, null, 'run-2', at(60))
        db.prepare('INSERT INTO dbdatakit_quality_check_status (dataset_hash, check_id, dataset, severity, on_error, status, status_changed_at, last_result_id, measured, threshold, evaluated_at, blocking, blocked_at, updated_at) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)')
          .run(hash, checkId, dataset, 'block', 'alert', 'fail', at(60), failing, 1, 0, at(60), 1, at(60), at(60))
        db.exec('COMMIT')
      } catch (e) {
        try { db.exec('ROLLBACK') } catch { /* nothing open */ }
        throw e
      }
    }
    for (let attempt = 1; ; attempt++) {
      try { write(); break } catch (e) {
        if (attempt >= 10 || !/database is locked|busy/i.test(String(e))) throw e
        await new Promise((r) => setTimeout(r, 500))   // the server holds the file for its own writes; the wait is not what the story asserts on
      }
    }
  } finally { db.close() }
}

test('the Quality page shows a blocked dataset with its history and sample keys, and an evaluation that cannot run leaves the block standing', async ({ page, context, baseURL }) => {
  // the test fixture `request` is a context of its own with no session; the page's browser context holds the signed-in cookie (as runner.spec.ts and monitoring.spec.ts use it)
  const request = context.request
  await signInNewAdmin(page, context, baseURL!, 'Quality Tester', `quality-${Date.now()}-${Math.random().toString(36).slice(2, 8)}@example.com`)
  await page.goto('/instances')
  await page.getByTestId('pair-url-input').fill(`https://localhost:${qualitySync.port}`)
  await page.getByTestId('pair-code-input').fill(fs.readFileSync(path.join(scratchDirectory, 'quality-sync-pairing-code.txt'), 'utf8').trim())
  await page.getByTestId('pair-name-input').fill('Quality sync')
  await page.getByTestId('pair-button').click()
  await expect(page.getByTestId('instance-row-Quality sync').getByTestId('instance-status')).toHaveText('Ready', { timeout: 30_000 })
  const servers = await (await request.get('/api/instances')).json() as { id: string; name: string }[]
  const relay = `/relay/${servers.find((s) => s.name === 'Quality sync')!.id}/api`

  // the replication that holds the check: its target is a port nothing listens on, so an evaluation cannot reach it
  const put = async (url: string, body: unknown) => { const r = await request.put(`${relay}${url}`, { data: body }); expect(r.ok(), `${url}: ${r.status()} ${await r.text()}`).toBe(true) }
  await put('/connections/orders-db', { name: 'orders-db', driverType: 'MsSql', host: '127.0.0.1', port: 1, database: 'Orders', properties: { TrustServerCertificate: 'true', 'Connect Timeout': '2' }, authMode: 'SqlAuth', userId: 'sa', password: 'unused' })
  await put(`/replications/${replication}`, {
    name: replication, enabled: false,
    endpoints: { source: { connectionName: 'orders-db', database: 'Src' }, target: { connectionName: 'orders-db', database: 'Orders' } },
    scheduling: { mode: 'Continuous', frequencySeconds: 3600 },
    changeProcessing: { reader: { kind: 'MsSqlCdc', options: {} }, cache: { kind: 'MsSqlStagingTable' }, writer: { kind: 'MsSqlMerge' } },
  })
  fs.appendFileSync(path.join(qualitySync.directory, 'config', 'replications', replication, 'task.yaml'), `\nchecks:\n  - {id: ${checkId}, kind: not_null, dataset: ${dataset}, column: Name, severity: block}\n`)
  await seed()

  await page.goto('/quality')
  await expect(page.getByTestId('quality-page')).toBeVisible()
  await expect(page.getByTestId('quality-server').filter({ hasText: 'Quality sync' })).toContainText('1 check')
  const row = page.getByTestId(`quality-dataset-${dataset}`)
  await expect(row).toHaveAttribute('data-state', 'blocked')
  await row.click()
  const check = page.getByTestId(`quality-check-${checkId}`)
  await expect(check).toHaveAttribute('data-blocking', 'true')
  await expect(check).toContainText('measured 1, allowed 0')   // (still the seeded `fail`: the server's tick has not evaluated yet, it waits a minute after first seeing a replication)

  // the history: both results, the newest first, and the keys of the row that failed
  await page.getByTestId(`quality-history-${checkId}`).click()
  await expect(page.getByTestId(`quality-block-${checkId}`)).toContainText('clears when the check passes')
  const results = page.getByTestId(`quality-results-${checkId}`).getByTestId('quality-result')
  await expect(results.first()).toBeVisible()
  // the two seeded results; the server's own tick may add one of its own (an `error`, the target is down) at any moment, so the count is only ever "at least"
  const before = await results.count()
  expect(before).toBeGreaterThanOrEqual(2)
  await expect(results.filter({ hasText: 'Id=2' })).toHaveCount(1)
  await expect(results.filter({ hasText: 'Id=2' }).getByTestId('quality-sample-keys')).toHaveText('Id=2')

  // evaluating on the server: the target cannot be reached, so the check is in error, which is recorded and does not lift the block
  const runButton = page.locator('[data-testid^="quality-run-"]').filter({ hasText: 'Evaluate on Quality sync' })
  await expect(runButton).toBeEnabled()
  await runButton.click()
  await expect(page.getByTestId('quality-run-result')).toContainText('1 could not run')
  await expect(page.getByTestId(`quality-status-${checkId}`)).toContainText('error')
  await expect(check).toHaveAttribute('data-blocking', 'true')
  // the open history is read again after an evaluation
  await expect.poll(() => results.count()).toBeGreaterThan(before)
})
