import { test, expect, type Page } from '../fixtures'

// Plan 33 UE07: the Quality Checks tab of a replication, against the real Sync server through the console. A replication that needs no engine (it is disabled and nothing reads its source) gets a freshness
// check from the screen; after a reload the check is there; a check that is not one (an accepted-values check with no column) is refused with the strict reader's diagnostic, its file and line, and nothing is written;
// the check is edited and removed. Nothing here evaluates a check: that is the console's Quality page (plan 23a FQ17).
const REPLICATION = 'checks-editor-demo'

async function openChecks(page: Page) {
  await page.goto(`/replications/${REPLICATION}/checks`)
  await expect(page.getByTestId('replication-checks')).toBeVisible()
  await expect(page.getByTestId('replication-checks-loading')).toHaveCount(0)
}

test('add a freshness check, reload and see it, break it and see the diagnostic, edit it and remove it', async ({ page }) => {
  const made = await page.request.put(`/api/replications/${REPLICATION}`, {
    data: {
      name: REPLICATION, enabled: false,
      endpoints: { source: { connectionName: 'src', database: 'AppDb' }, target: { connectionName: 'tgt', database: 'Warehouse' } },
      scheduling: { mode: 'Continuous', frequencySeconds: 3600 },
      changeProcessing: { reader: { kind: 'MsSqlCdc', options: {} }, cache: { kind: 'MsSqlStagingTable' }, writer: { kind: 'MsSqlMerge' } },
    },
  })
  expect(made.ok(), await made.text()).toBe(true)

  await openChecks(page)
  await expect(page.getByTestId('replication-checks-empty')).toBeVisible()
  await expect(page.getByTestId('replication-checks-save')).toBeDisabled()
  await expect(page.getByTestId('replication-checks-save-why')).toHaveText('Nothing has changed.')

  // a freshness check, the kind the form opens with
  await page.getByTestId('replication-check-add').click()
  await page.getByTestId('replication-check-id-input').fill('orders_fresh')
  await page.getByTestId('replication-check-dataset-input').fill('dbo.orders')
  await page.getByTestId('replication-check-maxAge-input').fill('10m')
  await page.getByTestId('replication-check-severity-select').selectOption('block')
  await page.getByTestId('replication-check-apply').click()
  await expect(page.getByTestId('replication-check-orders_fresh')).toContainText('dbo.orders: no older than 10m')
  await expect(page.getByTestId('replication-checks-save')).toBeEnabled()
  await page.getByTestId('replication-checks-save').click()
  await expect(page.getByTestId('replication-checks-saved')).toBeVisible()

  // it is in the file: a reload reads it back
  await page.reload()
  await expect(page.getByTestId('replication-checks')).toBeVisible()
  const row = page.getByTestId('replication-check-orders_fresh')
  await expect(row).toHaveAttribute('data-kind', 'freshness')
  await expect(row).toHaveAttribute('data-severity', 'block')

  // a check that is not one: the reader refuses it with its file and line, and the list the file has is unchanged
  await page.getByTestId('replication-check-add').click()
  await page.getByTestId('replication-check-id-input').fill('status_known')
  await page.getByTestId('replication-check-kind-select').selectOption('accepted_values')
  await page.getByTestId('replication-check-dataset-input').fill('dbo.orders')
  await page.getByTestId('replication-check-values-input').fill('open\nclosed')
  await page.getByTestId('replication-check-apply').click()
  await page.getByTestId('replication-checks-save').click()
  const faults = page.getByTestId('replication-checks-diagnostics')
  await expect(faults).toBeVisible()
  await expect(faults.getByTestId('replication-checks-fault').first()).toContainText('task.yaml:')
  await expect(faults.getByTestId('replication-checks-fault').first()).toContainText('column')
  await page.getByTestId('replication-checks-discard').click()
  await expect(page.getByTestId('replication-checks-diagnostics')).toHaveCount(0)
  await page.reload()
  await expect(page.getByTestId('replication-check-orders_fresh')).toBeVisible()
  await expect(page.getByTestId('replication-check-status_known')).toHaveCount(0)

  // change it, save, reload
  await page.getByTestId('replication-check-edit-orders_fresh').click()
  await expect(page.getByTestId('replication-check-id-input')).toBeDisabled()            // a check keeps its id: its results are kept by it
  await page.getByTestId('replication-check-maxAge-input').fill('1h')
  await page.getByTestId('replication-check-apply').click()
  await page.getByTestId('replication-checks-save').click()
  await expect(page.getByTestId('replication-checks-saved')).toBeVisible()
  await page.reload()
  await expect(page.getByTestId('replication-check-orders_fresh')).toContainText('no older than 1h')

  // remove it: the section goes from the file, and the replication still reads
  await page.getByTestId('replication-check-remove-orders_fresh').click()
  await page.getByTestId('replication-checks-save').click()
  await expect(page.getByTestId('replication-checks-saved')).toBeVisible()
  await page.reload()
  await expect(page.getByTestId('replication-checks-empty')).toBeVisible()
  expect((await page.request.get(`/api/replications/${REPLICATION}`)).ok()).toBe(true)
})
