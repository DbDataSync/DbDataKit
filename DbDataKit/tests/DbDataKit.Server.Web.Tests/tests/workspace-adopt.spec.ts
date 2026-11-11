import { expect, test } from '@playwright/test'
import { signInNewAdmin } from './people'
import { SERVER, mockWorkspace } from './workspace-fake'

// Plan 33, SC21f: a Store model published onto a data source the Gateway holds that was made by hand is refused until the person confirms, in the Workspace, that it may go on that data source. The confirmation names the data source and who
// made it; confirming publishes again with `adopt`, which the Gateway records. The Workspace server is mocked in the page (the real routes are DbDataWorkspace.Tests and the Gateway's ManagementAdoptModelTests). It signs in a new administrator, so its
// name sorts after `first-run` and `identity-home` (ci-lessons).
test.setTimeout(120_000)

test('a model goes onto a data source made by hand only after the person confirms it by name', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Workspace Adopter', `workspace-adopt-${Date.now()}@example.com`)
  const fake = await mockWorkspace(page)
  fake.adoptRefusal = 'mdm_sql'
  await page.goto(`/servers/${SERVER}`)
  await page.getByTestId('ws-new-project-open').click()
  await page.getByTestId('ws-new-project-id').fill('ledger')
  await page.getByTestId('ws-new-project-kind').selectOption('store')
  await page.getByTestId('ws-new-project-create').click()
  await page.getByTestId('ws-new-name-ledger').fill('adopt-edit')
  await page.getByTestId('ws-new-create-ledger').click()
  const listed = page.locator('[data-testid="ws-workspace"][data-workspace$="-adopt-edit"]')
  await page.getByTestId(`ws-open-${await listed.getAttribute('data-workspace')}`).click()
  await page.getByTestId('wsp-tab-builds').click()
  await page.getByTestId('wsp-build').click()
  const done = page.locator('[data-testid="wsp-build-detail"][data-status="succeeded"]')
  await expect(done).toBeVisible({ timeout: 15_000 })
  await done.getByTestId('wsp-publish-target').fill('edge')
  await done.getByTestId('wsp-publish').click()

  // refused: the confirmation names the data source and who made it, and nothing was adopted yet
  const ask = done.getByTestId('wsp-publish-adopt')
  await expect(ask).toBeVisible()
  await expect(ask).toHaveAttribute('data-data-source', 'mdm_sql')
  await expect(done.getByTestId('wsp-publish-adopt-text')).toContainText('made by hand or by an administrator')
  expect(fake.published.at(-1)?.body.adopt).toBeUndefined()

  // declining leaves it as it was
  await done.getByTestId('wsp-publish-adopt-cancel').click()
  await expect(ask).toHaveCount(0)
  await done.getByTestId('wsp-publish').click()
  await expect(ask).toBeVisible()

  // confirming publishes again with `adopt` naming that data source
  await done.getByTestId('wsp-publish-adopt-confirm').click()
  await expect(done.getByTestId('wsp-published-line')).toContainText('adopted mdm_sql, made by hand')
  expect(fake.published.at(-1)?.body.adopt).toEqual(['mdm_sql'])
})
