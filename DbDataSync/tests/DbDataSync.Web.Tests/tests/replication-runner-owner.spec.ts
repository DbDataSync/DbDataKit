import { test, expect, type Page } from '../fixtures'
import fs from 'node:fs'
import path from 'node:path'
import { consoleScratch } from '../console-paths'
import { screenshotDir } from '../screenshots'

const screenshotsDir = screenshotDir('replication-runner-owner')

// Plan 28c E12 and E22: the Runner tab of a replication, against the real Sync server through the console (no stub). The Runner is Sync's only scheduler (owner decision S2, 2026-11-14), so the tab says the runner runs the
// replication, with no owner to move. A replication that needs no engine (disabled, nothing reads its source) is made through the API; with no runner linked the tab says so; the runner is saved from the form and is still
// there after a reload (the server keeps it in `runner-link.json` beside its state); and the runner's row of the replication, asked of a runner this machine is not paired with, is the real server's refusal in its own words.
const REPLICATION = 'runner-owner-demo'
const linkFile = path.join(consoleScratch, 'sync', 'runner-link.json')

test.afterEach(() => {
  fs.rmSync(linkFile, { force: true })
})

async function openRunner(page: Page) {
  await page.goto(`/replications/${REPLICATION}/runner`)
  await expect(page.getByTestId('replication-runner')).toBeVisible()
}

test('with no runner linked the tab says so, the runner is set and kept across a reload, and an unpaired runner is refused in words', async ({ page }) => {
  const made = await page.request.put(`/api/replications/${REPLICATION}`, {
    data: {
      name: REPLICATION, enabled: false,
      endpoints: { source: { connectionName: 'src', database: 'AppDb' }, target: { connectionName: 'tgt', database: 'Warehouse' } },
      scheduling: { mode: 'Continuous', frequencySeconds: 3600 },
      changeProcessing: { reader: { kind: 'MsSqlCdc', options: {} }, cache: { kind: 'MsSqlStagingTable' }, writer: { kind: 'MsSqlMerge' } },
    },
  })
  expect(made.ok(), await made.text()).toBe(true)

  await openRunner(page)
  await expect(page.getByTestId('tab-runner')).toBeVisible()
  await expect(page.getByTestId('runner-owner')).toContainText('runner')
  await expect(page.getByTestId('runner-row-error')).toContainText('No runner is linked')
  await expect(page.getByTestId('runner-link-save')).toBeDisabled()
  await page.screenshot({ path: path.join(screenshotsDir, '90-runner-none.png'), fullPage: true })

  await page.getByTestId('runner-link-server').fill('https://localhost:9')
  await page.getByTestId('runner-link-channel').fill('prod')
  await page.getByTestId('runner-link-package').fill('sales-set')
  await expect(page.getByTestId('runner-link-save')).toBeEnabled()
  const saved = page.waitForResponse((r) => r.request().method() === 'PUT' && new URL(r.url()).pathname.endsWith('/api/runner'))
  await page.getByTestId('runner-link-save').click()
  expect((await saved).ok()).toBe(true)

  // the server keeps it: a reload reads it back, and the runner's row is asked of a runner this machine is not paired with
  await page.reload()
  await expect(page.getByTestId('replication-runner')).toBeVisible()
  await expect(page.getByTestId('runner-link-server')).toHaveValue('https://localhost:9')
  await expect(page.getByTestId('runner-link-package')).toHaveValue('sales-set')
  await expect(page.getByTestId('runner-row-error')).toContainText('not paired with a runner')
  const link = await page.request.get('/api/runner')
  expect(((await link.json()) as { server: string }).server).toBe('https://localhost:9')
  await page.screenshot({ path: path.join(screenshotsDir, '91-runner-unpaired.png'), fullPage: true })
})
