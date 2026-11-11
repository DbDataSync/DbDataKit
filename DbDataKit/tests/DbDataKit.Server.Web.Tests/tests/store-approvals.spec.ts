import { expect, test } from '@playwright/test'
import { signInNewAdmin } from './people'
import { SERVER, mockStore } from './store-fake'

// The console's Approvals page over a Store server answering through the relay (`store-fake.ts`, the pattern of `store-changes.spec.ts`): an agent's write waits as a change request, the page lists it with who proposed it and says it
// is an agent's, offers Review, and Review opens the model's Change requests tab where it is decided; once it is rejected there the page says nothing waits. It signs in a new administrator, so it sorts after `identity-home` and `first-run`.
const budgets = [
  'schemaVersion: 1', 'name: budgets', 'storage: {connection: mdm}', 'approval: {changes: steward}', 'entities:', '  budget:', '    key: [code]', '    attributes:',
  '      code: {type: VARCHAR(10), required: true}', '      amount: {type: "DECIMAL(10,2)"}', '',
].join('\n')

test('the Approvals page lists a waiting change request, opens the screen that decides it and then shows nothing waiting', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Approvals Tester', `approvals-${Date.now()}@example.com`)
  const store = await mockStore(page, { role: 'admin', models: { budgets }, created: ['budgets'] })
  store.propose('budgets', [{ action: 'insert', entity: 'budget', values: { code: 'B1', amount: 5 } }], 'Add budget B1')

  await page.goto('/approvals')
  await expect(page.getByTestId('approvals-page')).toBeVisible()
  const row = page.getByTestId('approvals-row')
  await expect(row).toHaveCount(1)
  await expect(row.getByTestId('approvals-kind')).toContainText('Change request (an agent)')
  await expect(row).toContainText('budgets')
  await expect(row).toContainText('Add budget B1')
  await expect(page.getByTestId('approvals-summary')).toContainText('1 waiting, 1 that you may decide')

  await row.getByTestId('approvals-open').click()
  await expect(page.getByTestId('store-changes')).toBeVisible()
  await expect(page).toHaveURL(new RegExp(`/servers/${SERVER}/model/budgets/changes$`))
  await page.getByTestId('change-row').first().click()
  await page.getByTestId('change-note-input').fill('Not needed')
  await page.getByTestId('change-reject').click()
  await expect(page.getByTestId('change-decided')).toContainText('Rejected')

  await page.goto('/approvals')        // a Store model's page has the Store's rail, not the console's, so the page is opened by its address
  await expect(page.getByTestId('approvals-none')).toBeVisible()
})

// SC21c: the same page decides in place. A rejection needs the reason written (its button is off until then), an approval applies the rows, and the page says nothing waits once the list is empty. Written, not run (it needs the console built).
test('the Approvals page approves and rejects a change request without opening the Store\'s screen', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Approvals Decider', `approvals-decide-${Date.now()}@example.com`)
  const store = await mockStore(page, { role: 'admin', models: { budgets }, created: ['budgets'] })
  store.propose('budgets', [{ action: 'insert', entity: 'budget', values: { code: 'B1', amount: 5 } }], 'Add budget B1')
  store.propose('budgets', [{ action: 'insert', entity: 'budget', values: { code: 'B2', amount: 7 } }], 'Add budget B2')

  await page.goto('/approvals')
  await expect(page.getByTestId('approvals-row')).toHaveCount(2)
  const first = page.getByTestId('approvals-row').first()
  await expect(first.getByTestId('approvals-reject')).toBeDisabled()
  await first.getByTestId('approvals-note').fill('Not this quarter')
  await expect(first.getByTestId('approvals-reject')).toBeEnabled()
  await first.getByTestId('approvals-reject').click()
  await expect(page.getByTestId('approvals-row')).toHaveCount(1)

  await page.getByTestId('approvals-approve').click()
  await expect(page.getByTestId('approvals-none')).toBeVisible()
})
