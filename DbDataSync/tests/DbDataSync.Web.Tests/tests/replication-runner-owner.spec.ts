import { test, expect, type Page } from '../fixtures'
import path from 'node:path'
import { screenshotDir } from '../screenshots'

const screenshotsDir = screenshotDir('replication-runner-owner')

const REPLICATION_NAME = 'runner-owner-demo'

/**
 * Plan 28c E12 — the Runner tab: who schedules the replication, the moves (observe, cut over, roll back) and the runner it moves to.
 *
 * **Stubbed at the network boundary**, following `replication-wide-provisioning.spec.ts`: what is under test is the screen — the owner and what it means, which moves each owner allows, the server's own refusal shown in its words with the
 * owner as it was, and the runner's link saved — not the move itself, which `ReplicationOwnershipTests` (API unit tests) and the runner conformance suite cover against a real runner.
 */

const TASK = {
  name: REPLICATION_NAME,
  enabled: true,
  scheduling: { mode: 'Continuous', frequencySeconds: 60, cronExpression: null },
  changeProcessing: {
    reader: { kind: 'MsSqlChangeTracking', options: {} },
    cache: { kind: 'MsSqlStagingTable', options: {} },
    writer: { kind: 'MsSqlMerge', options: {} },
  },
  endpoints: { source: { connectionName: 'src', database: 'Sales' }, target: { connectionName: 'tgt', database: 'DW' } },
  provisioning: { createTargetTableIfMissing: true, alterTargetTableColumnsIfMissingOrChanged: null },
}

const LINK = { server: 'runner-1', channel: 'prod', package: 'sales-set', clientDirectory: null }

const json = (body: unknown, status = 200) => ({ status, contentType: 'application/json', body: JSON.stringify(body) })

interface Rig { owner: string; link: typeof LINK | null; moves: Array<{ owner: string; waitSeconds: number | null }>; refuse: string | null; savedLinks: unknown[] }

async function stub(page: Page, rig: Rig) {
  const base = `/api/replications/${REPLICATION_NAME}`
  await page.route(`**${base}/table-mappings`, (route) => route.fulfill(json([])))
  await page.route(`**${base}/status`, (route) => route.fulfill(json({ running: false, enabled: true, paused: false, pauseNote: null, shouldRun: true })))
  await page.route(`**${base}/metrics*`, (route) => route.fulfill(json({ runs: 0, failures: 0, rowsWritten: 0, rowsRead: 0, buckets: [], processingP50Ms: 0, processingP95Ms: 0, processingMaxMs: 0, lastCompletedPassUtc: null })))
  await page.route(`**${base}/lag`, (route) => route.fulfill(json({ mappings: {}, lowestLagMs: null, highestLagMs: null, rangeIncludesEstimates: false })))
  await page.route('**/api/runner', (route) => {
    if (route.request().method() === 'PUT') {
      const body = route.request().postDataJSON()
      rig.savedLinks.push(body)
      rig.link = body
      return route.fulfill(json(body))
    }
    return route.fulfill(json(rig.link))
  })
  await page.route(`**${base}/owner`, (route) => {
    if (route.request().method() === 'PUT') {
      const body = route.request().postDataJSON() as { owner: string; waitSeconds: number | null }
      rig.moves.push(body)
      if (rig.refuse) return route.fulfill(json({ error: rig.refuse }, 409))
      const from = rig.owner
      rig.owner = body.owner
      return route.fulfill(json({ replication: REPLICATION_NAME, from, to: body.owner, task: REPLICATION_NAME, positionsHandedOver: 3, handledUpTo: null, paused: false, waited: true }))
    }
    return route.fulfill(json({
      replication: REPLICATION_NAME, owner: rig.owner, link: rig.link,
      runner: rig.owner === 'old' ? null : { task: REPLICATION_NAME, owner: rig.owner, nextDue: null, lastRun: null, running: 0, queued: 0, paused: false, pausedBy: null },
      runnerError: null,
    }))
  })
  await page.route(`**${base}`, (route) => route.fulfill(json(TASK)))
}

test.describe('replication detail: the Runner tab (plan 28c E12)', () => {
  test('01 - an old replication says this server runs it, offers observe and cutover but no rollback', async ({ page }) => {
    const rig: Rig = { owner: 'old', link: LINK, moves: [], refuse: null, savedLinks: [] }
    await stub(page, rig)
    await page.goto(`/replications/${REPLICATION_NAME}/runner`)

    await expect(page.getByTestId('tab-runner')).toBeVisible()
    await expect(page.getByTestId('runner-owner')).toHaveAttribute('data-owner', 'old')
    await expect(page.getByTestId('runner-owner-text')).toContainText('own worker processes')
    await expect(page.getByTestId('runner-row-none')).toBeVisible()
    await expect(page.getByTestId('runner-observe')).toBeEnabled()
    await expect(page.getByTestId('runner-cutover')).toBeEnabled()
    await expect(page.getByTestId('runner-rollback')).toBeDisabled()
    await expect(page.getByTestId('runner-link-server')).toHaveValue('runner-1')

    await page.screenshot({ path: path.join(screenshotsDir, '90-runner-old.png'), fullPage: true })
  })

  test('02 - a cutover sends the move with the wait, says what it did, and the tab then shows the runner as the owner', async ({ page }) => {
    const rig: Rig = { owner: 'old', link: LINK, moves: [], refuse: null, savedLinks: [] }
    await stub(page, rig)
    await page.goto(`/replications/${REPLICATION_NAME}/runner`)

    await page.getByTestId('runner-wait').fill('120')
    await page.getByTestId('runner-cutover').click()

    await expect(page.getByTestId('runner-done')).toContainText('old to runner')
    await expect(page.getByTestId('runner-done')).toContainText('3 position(s) handed over')
    expect(rig.moves).toEqual([{ owner: 'runner', waitSeconds: 120 }])
    await expect(page.getByTestId('runner-owner')).toHaveAttribute('data-owner', 'runner')
    await expect(page.getByTestId('runner-row')).toBeVisible()
    await expect(page.getByTestId('runner-cutover')).toBeDisabled()
    await expect(page.getByTestId('runner-rollback')).toBeEnabled()

    await page.screenshot({ path: path.join(screenshotsDir, '91-runner-cutover.png'), fullPage: true })
  })

  test('03 - a refusal is shown in the server\'s words and the owner stays as it was', async ({ page }) => {
    const rig: Rig = { owner: 'old', link: LINK, moves: [], refuse: "The mapping 'orders' is loading; let it finish before the move. The owner is as it was.", savedLinks: [] }
    await stub(page, rig)
    await page.goto(`/replications/${REPLICATION_NAME}/runner`)

    await page.getByTestId('runner-cutover').click()

    await expect(page.getByText("The mapping 'orders' is loading")).toBeVisible()
    await expect(page.getByTestId('runner-owner')).toHaveAttribute('data-owner', 'old')
    await expect(page.getByTestId('runner-done')).toHaveCount(0)
  })

  test('04 - with no runner set the moves are disabled until it is saved', async ({ page }) => {
    const rig: Rig = { owner: 'old', link: null, moves: [], refuse: null, savedLinks: [] }
    await stub(page, rig)
    await page.goto(`/replications/${REPLICATION_NAME}/runner`)

    await expect(page.getByTestId('runner-cutover')).toBeDisabled()
    await expect(page.getByTestId('runner-link-save')).toBeDisabled()

    await page.getByTestId('runner-link-server').fill('runner-2')
    await page.getByTestId('runner-link-channel').fill('prod')
    await page.getByTestId('runner-link-package').fill('sales-set')
    await page.getByTestId('runner-link-save').click()

    await expect(page.getByTestId('runner-cutover')).toBeEnabled()
    expect(rig.savedLinks).toEqual([{ server: 'runner-2', channel: 'prod', package: 'sales-set', clientDirectory: null }])
  })
})
