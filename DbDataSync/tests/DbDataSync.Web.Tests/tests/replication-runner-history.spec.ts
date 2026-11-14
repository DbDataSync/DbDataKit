import { test, expect } from '../fixtures'
import fs from 'node:fs'
import path from 'node:path'
import { consoleScratch } from '../console-paths'
import { screenshotDir } from '../screenshots'

const screenshotsDir = screenshotDir('replication-runner-history')

// Plan 28c E13 and E22: the run history of a replication, against the real Sync server through the console (no stub). The Runner is Sync's only scheduler; the server keeps the runner it is linked to in `runner-link.json` beside its state
// and reads it at every use, so this story writes a link to a runner that nobody answers at and the real server does the rest: its history says it could not ask the runner, in its own words, and
// does not look complete; the runs it holds of its own from before the move are still listed. The live lines and the merged history with a runner that answers are proved against the real hub and a real runner's endpoints
// (`RunnerOwnedRunsTests`, `RunReadApiTests`): a browser needs a paired runner with the package published to show them, which this suite does not start.
const REPLICATION = 'runner-history-demo'
const linkFile = path.join(consoleScratch, 'sync', 'runner-link.json')

test.afterEach(() => {
  fs.rmSync(linkFile, { force: true })
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

  // no runner is linked: the history is this server's own and says nothing of a runner
  await page.goto(`/replications/${REPLICATION}/monitoring/history`)
  await expect(page.getByTestId('run-history-table')).toBeVisible()
  await expect(page.getByTestId('runner-history-unavailable')).toHaveCount(0)

  // a runner is linked, and nobody answers at it
  fs.writeFileSync(linkFile, JSON.stringify({ link: { server: 'https://localhost:9', channel: 'prod', package: 'sales-set' } }, null, 2))
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
