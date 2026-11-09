import { test, expect, type Page } from '../fixtures'
import path from 'node:path'
import { screenshotDir } from '../screenshots'

const screenshotsDir = screenshotDir('replication-runner-owner')

// Plan 28c E12: the Runner tab of a replication, against the real Sync server through the console (no stub). A replication that needs no engine (disabled, nothing reads its source) is made through the API; the tab shows that
// this server runs it, the moves are off until the runner is set, the runner is saved from the form and is still there after a reload (the server keeps it in `runner-owners.json`), and a cutover to a runner this machine is not paired
// with is refused by the real server in its own words, with the owner as it was. A move that succeeds needs a runner: that is the runner conformance suite's (plan 28c E12c), not a browser's.
const REPLICATION = 'runner-owner-demo'

async function openRunner(page: Page) {
  await page.goto(`/replications/${REPLICATION}/runner`)
  await expect(page.getByTestId('replication-runner')).toBeVisible()
}

test('set the runner, reload and see it, and a cutover to a runner nobody paired is refused in words and changes nothing', async ({ page }) => {
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
  await expect(page.getByTestId('runner-owner')).toHaveAttribute('data-owner', 'old')
  await expect(page.getByTestId('runner-owner-text')).toContainText('own worker processes')
  await expect(page.getByTestId('runner-row-none')).toBeVisible()
  // no runner is set yet: nothing can be moved, and the buttons say why
  for (const move of ['runner-observe', 'runner-cutover', 'runner-rollback']) await expect(page.getByTestId(move)).toBeDisabled()
  await expect(page.getByTestId('runner-link-save')).toBeDisabled()
  await page.screenshot({ path: path.join(screenshotsDir, '90-runner-old.png'), fullPage: true })

  await page.getByTestId('runner-link-server').fill('https://localhost:9')
  await page.getByTestId('runner-link-channel').fill('prod')
  await page.getByTestId('runner-link-package').fill('sales-set')
  await page.getByTestId('runner-link-save').click()
  await expect(page.getByTestId('runner-cutover')).toBeEnabled()
  await expect(page.getByTestId('runner-observe')).toBeEnabled()
  await expect(page.getByTestId('runner-rollback')).toBeDisabled()    // this server schedules it already

  // the server keeps it: a reload reads it back
  await page.reload()
  await expect(page.getByTestId('replication-runner')).toBeVisible()
  await expect(page.getByTestId('runner-link-server')).toHaveValue('https://localhost:9')
  await expect(page.getByTestId('runner-link-package')).toHaveValue('sales-set')

  // a cutover to a runner this machine is not paired with: the real server's refusal, shown as it is, and the owner is as it was
  await page.getByTestId('runner-cutover').click()
  await expect(page.getByText('not paired with a runner')).toBeVisible()
  await expect(page.getByTestId('runner-owner')).toHaveAttribute('data-owner', 'old')
  await expect(page.getByTestId('runner-done')).toHaveCount(0)
  const owner = await page.request.get(`/api/replications/${REPLICATION}/owner`)
  expect(((await owner.json()) as { owner: string }).owner).toBe('old')
  await page.screenshot({ path: path.join(screenshotsDir, '91-runner-refused.png'), fullPage: true })
})
