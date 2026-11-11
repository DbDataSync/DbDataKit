import { expect, type Page } from '@playwright/test'
import { SERVER, mockStore } from './store-fake'

const territories = [
  'schemaVersion: 1', 'name: territories', 'storage: {connection: mdm}', 'approval: {changes: steward}', 'entities:', '  territory:', '    key: [code]', '    attributes:',
  '      code: {type: VARCHAR(10), required: true}', '      name: {type: VARCHAR(100), required: true}', '      quota: {type: "DECIMAL(10,2)", min: 0}', '',
].join('\n')
const budgets = [
  'schemaVersion: 1', 'name: budgets', 'storage: {connection: mdm}', 'approval: {changes: owner}', 'entities:', '  budget:', '    key: [code]', '    attributes:',
  '      code: {type: VARCHAR(10), required: true}', '      amount: {type: "DECIMAL(10,2)"}', '',
].join('\n')

const row = (page: Page, key: string) => page.locator(`[data-testid="sui-row"][data-key="${key}"]`)
const entity = (page: Page, model: string, name: string) => page.goto(`/servers/${SERVER}/models/${model}/${name}`)

/**
 * Plan 33, UE14: the review of change requests, in the console, over a Store server answering through the relay (`store-fake.ts`). A model that asks for approval keeps a person's write as a request: the entity screen says it is not applied
 * and links to the requests; the reviewer reads the proposed row (against what the row holds now), approves it and the row is there; a second change is rejected with a reason and the row is as it was; an agent's request that breaks a rule is
 * refused on approval, stays waiting and says why; on a model whose owner approves, a person who is not the owner sees Approve and Reject off with the role that is needed.
 */
export async function changesStory(page: Page) {
  const store = await mockStore(page, { role: 'admin', models: { territories, budgets }, created: ['territories', 'budgets'] })

  // a write that waits: the entity screen says it is not applied yet, and the grid has no new row
  await entity(page, 'territories', 'territory')
  await page.getByTestId('sui-new').click()
  await page.getByTestId('sui-field-code').fill('EU')
  await page.getByTestId('sui-field-name').fill('Europe')
  await page.getByTestId('sui-field-quota').fill('10')
  await page.getByTestId('sui-submit').click()
  await expect(page.getByTestId('sui-waiting')).toContainText('Not applied yet')
  await expect(page.getByTestId('sui-row')).toHaveCount(0)
  expect(store.rows('territories', 'territory')).toEqual([])

  // the request is waiting, with who proposed it; reading it shows the row it would add
  await page.getByTestId('sui-waiting-link').click()
  await expect(page).toHaveURL(new RegExp(`/servers/${SERVER}/model/territories/changes$`))
  await expect(page.getByTestId('changes-policy')).toHaveAttribute('data-can-decide', 'true')
  await expect(page.getByTestId('change-row')).toHaveCount(1)
  await expect(page.getByTestId('change-row').first()).toHaveAttribute('data-state', 'pending')
  await expect(page.getByTestId('change-row-proposer').first()).toHaveText('You')
  await expect(page.getByTestId('change-detail-empty')).toBeVisible()
  await page.getByTestId('change-row').first().click()
  await expect(page.getByTestId('change-item')).toHaveAttribute('data-action', 'insert')
  await expect(page.getByTestId('change-diff')).toContainText('Europe')

  // Reject needs the reason written; Approve does not
  await expect(page.getByTestId('change-reject')).toBeDisabled()
  await expect(page.getByTestId('change-reject-why')).toContainText('Say why in the note')
  await expect(page.getByTestId('change-approve')).toBeEnabled()
  await page.getByTestId('change-approve').click()
  await expect(page.getByTestId('change-decided')).toContainText('Approved')
  await expect(page.getByTestId('changes-empty')).toBeVisible()
  expect(store.rows('territories', 'territory')).toEqual([{ code: 'EU', name: 'Europe', quota: 10 }])
  await entity(page, 'territories', 'territory')
  await expect(row(page, 'EU')).toContainText('Europe')

  // a change to the row waits too; the review shows what the row holds now against what it would hold, and rejecting it with a reason leaves the row as it was
  await row(page, 'EU').click()
  await page.getByTestId('sui-field-name').fill('Europe West')
  await page.getByTestId('sui-submit').click()
  await expect(page.getByTestId('sui-waiting')).toBeVisible()
  await expect(row(page, 'EU')).toContainText('Europe')
  await expect(row(page, 'EU')).not.toContainText('West')
  await page.getByTestId('sui-waiting-link').click()
  await page.getByTestId('change-row').first().click()
  await expect(page.getByTestId('change-item')).toHaveAttribute('data-action', 'update')
  const changed = page.locator('[data-testid="change-field"][data-changed="true"]')
  await expect(changed).toHaveCount(1)
  await expect(changed.getByTestId('change-before')).toHaveText('Europe')
  await expect(changed.getByTestId('change-after')).toHaveText('Europe West')
  await page.getByTestId('change-note-input').fill('Keep it as Europe until the reorganisation')
  await page.getByTestId('change-reject').click()
  await expect(page.getByTestId('change-decided')).toContainText('Rejected')
  await expect(page.getByTestId('change-decided')).toContainText('until the reorganisation')
  expect(store.rows('territories', 'territory')).toEqual([{ code: 'EU', name: 'Europe', quota: 10 }])

  // the decided ones are listed under their own filters
  await page.getByTestId('changes-filter-rejected').click()
  await expect(page.getByTestId('change-row')).toHaveCount(1)
  await expect(page.getByTestId('change-row').first()).toHaveAttribute('data-state', 'rejected')
  await page.getByTestId('changes-filter-all').click()
  await expect(page.getByTestId('change-row')).toHaveCount(2)

  // an agent's request that breaks a rule: approving is refused in the Store's words, it stays waiting, and then it is rejected with a reason
  store.propose('territories', [{ action: 'insert', entity: 'territory', values: { code: 'NA', name: 'North America', quota: -1 } }], 'Add North America')
  await page.goto(`/servers/${SERVER}/model/territories/changes`)
  await expect(page.getByTestId('change-row-proposer').first()).toHaveText('Territory bot (agent)')
  await page.getByTestId('change-row').first().click()
  await expect(page.getByTestId('change-detail-head')).toContainText('Add North America')
  await page.getByTestId('change-approve').click()
  await expect(page.getByTestId('change-refusal')).toContainText('so nothing was written')
  await expect(page.getByTestId('change-violation')).toContainText('below the minimum 0')
  await expect(page.getByTestId('change-violation')).toHaveAttribute('data-rule', 'min')
  await expect(page.getByTestId('change-decision')).toHaveAttribute('data-state', 'pending')
  expect(store.rows('territories', 'territory')).toHaveLength(1)
  await page.getByTestId('change-note-input').fill('The quota cannot be negative')
  await page.getByTestId('change-reject').click()
  await expect(page.getByTestId('change-decided')).toContainText('Rejected')

  // a model whose owner approves: an author reads the request and cannot decide it, and the screen says which role can
  store.setRole('author')
  store.propose('budgets', [{ action: 'insert', entity: 'budget', values: { code: 'B1', amount: 5 } }], 'Add budget B1')
  await page.goto(`/servers/${SERVER}/model/budgets/changes`)
  await expect(page.getByTestId('changes-policy')).toHaveAttribute('data-can-decide', 'false')
  await expect(page.getByTestId('changes-policy')).toContainText('needs the admin role')
  await page.getByTestId('change-row').first().click()
  await expect(page.getByTestId('change-diff')).toContainText('B1')
  await expect(page.getByTestId('change-approve')).toBeDisabled()
  await expect(page.getByTestId('change-approve-why')).toContainText('needs the admin role')
  await expect(page.getByTestId('change-reject')).toBeDisabled()

  // the administrator may
  store.setRole('admin')
  await page.reload()
  await page.getByTestId('change-row').first().click()
  await expect(page.getByTestId('change-approve')).toBeEnabled()
  await page.getByTestId('change-approve').click()
  await expect(page.getByTestId('change-decided')).toContainText('Approved')
  expect(store.rows('budgets', 'budget')).toEqual([{ code: 'B1', amount: 5 }])
}
