import { test, expect } from '../fixtures'
import fs from 'node:fs'
import path from 'node:path'
import { consoleScratch } from '../console-paths'
import { screenshotDir } from '../screenshots'

const screenshotsDir = screenshotDir('replication-runner-history')

// Plan 28c E13: the run history of a replication the runner owns, against the real Sync server through the console (no stub). The server keeps who owns a replication in `runner-owners.json` beside its state and reads it at every
// use, so this story writes the file the way a cutover leaves it (the owner `runner`, and a runner that nobody answers at) and the real server does the rest: its history says it could not ask the runner, in its own words, and
// does not look complete; the runs it holds of its own from before the move are still listed. The live lines and the merged history with a runner that answers are proved against the real hub and a real runner's endpoints
// (`RunnerOwnedRunsTests`, `RunReadApiTests`): a browser needs a paired runner with the package published to show them, which this suite does not start.
const REPLICATION = 'runner-history-demo'
const ownersFile = path.join(consoleScratch, 'sync', 'runner-owners.json')

test.afterEach(() => {
  fs.rmSync(ownersFile, { force: true })
})

test('a runner that does not answer is said in the history, which still lists this server\'s own runs, and never looks complete', async ({ page }) => {
  const made = await page.request.put(`/api/replications/${REPLICATION}`, {
    data: {
      name: REPLICATION, enabled: false,
      endpoints: { source: { connectionName: 'src', database: 'AppDb' }, target: { connectionName: 'tgt', database: 'Warehouse' } },
      scheduling: { mode: 'Continuous', frequencySeconds: 3600 },
      changeProcessing: { reader: { kind: 'MsSqlCdc', options: {} }, cache: { kind: 'MsSqlStagingTable' }, writer: { kind: 'MsSqlMerge' } },
    },
  })
  expect(made.ok(), await made.text()).toBe(true)

  // this server owns it: the history is its own and says nothing of a runner
  await page.goto(`/replications/${REPLICATION}/monitoring/history`)
  await expect(page.getByTestId('run-history-table')).toBeVisible()
  await expect(page.getByTestId('runner-history-unavailable')).toHaveCount(0)

  // the runner owns it, and the runner nobody answers at
  fs.writeFileSync(ownersFile, JSON.stringify({ link: { server: 'https://localhost:9', channel: 'prod', package: 'sales-set' }, owners: { [REPLICATION]: 'runner' } }, null, 2))
  const history = await page.request.get(`/api/replications/${REPLICATION}/runs`)
  expect(history.ok(), await history.text()).toBe(true)
  const body = (await history.json()) as { runs: unknown[]; runnerUnavailable: string | null }
  expect(body.runnerUnavailable).toMatch(/not paired with a runner|could not be reached/)

  await page.reload()
  await expect(page.getByTestId('run-history-table')).toBeVisible()
  const banner = page.getByTestId('runner-history-unavailable')
  await expect(banner).toBeVisible()
  await expect(banner).toContainText('incomplete')
  await expect(banner).toContainText('not paired with a runner')
  await page.screenshot({ path: path.join(screenshotsDir, '90-runner-history-unavailable.png'), fullPage: true })

  // the log of a run the runner would hold says the same, with a 502 and the reason, not an empty log
  const log = await page.request.get(`/api/runs/00000000-0000-4000-8000-000000000001/logs`)
  expect(log.status()).toBe(502)
  expect(((await log.json()) as { error: string }).error).toContain('not paired with a runner')
})
