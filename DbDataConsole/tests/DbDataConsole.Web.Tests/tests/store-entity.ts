import { expect, type Page } from '@playwright/test'
import { SERVER, mockStore } from './store-fake'

const territories = [
  'schemaVersion: 1', 'name: territories', 'storage: {connection: mdm, instances: schema}', 'entities:', '  territory:', '    key: [code]', '    history: audit', '    attributes:',
  '      code: {type: VARCHAR(10), required: true}', '      name: {type: VARCHAR(100), required: true}', '      tier: {type: VARCHAR(10), allowed: [gold, silver]}', '      quota: {type: "DECIMAL(10,2)", min: 0}', '',
].join('\n')

const row = (page: Page, key: string) => page.locator(`[data-testid="sui-row"][data-key="${key}"]`)
const fill = async (page: Page, values: Record<string, string>) => {
  for (const [name, value] of Object.entries(values)) {
    if (name === 'tier') await page.getByTestId('sui-field-tier').selectOption(value)
    else await page.getByTestId(`sui-field-${name}`).fill(value)
  }
}

/**
 * Plan 33, UE18: the Store entity editor in the console (the rows of one entity, `@dbdatakit/store-ui` in the console's frame), over a Store server answering through the relay (`store-fake.ts`). A person opens an entity from the models list,
 * sees the Store's refusals (a required value against its attribute, a key that exists listed with its rule), creates a row, edits it, reads its history, is refused when somebody else changed the row meanwhile, deletes it, and picks an instance
 * whose rows are kept apart from the model's own copy. The links of the screen are the module's: they stay under the server's address.
 */
export async function entityStory(page: Page) {
  const store = await mockStore(page, { role: 'admin', models: { territories }, created: ['territories', 'territories/emea'], instances: { territories: ['emea'] } })
  await page.goto(`/servers/${SERVER}`)
  await expect(page.getByTestId('store-model')).toHaveAttribute('data-model', 'territories')
  await page.getByTestId('store-entity-link').click()
  await expect(page).toHaveURL(new RegExp(`/servers/${SERVER}/models/territories/territory$`))
  await expect(page.getByTestId('store-entity')).toBeVisible()
  await expect(page.getByTestId('sui-empty')).toHaveText('No rows.')

  // a new row sent empty: the Store names each attribute that is required, next to its field
  await page.getByTestId('sui-new').click()
  await expect(page.getByTestId('sui-form-title')).toHaveText('New territory')
  await page.getByTestId('sui-submit').click()
  await expect(page.getByTestId('sui-problem-code')).toContainText('is required')
  await expect(page.getByTestId('sui-problem-name')).toContainText('is required')

  // the browser holds a number to its minimum before anything is sent; a row whose key exists is the Store's refusal, listed with its rule
  await fill(page, { code: 'EU', name: 'Europe', tier: 'gold', quota: '10.5' })
  await expect(page.getByTestId('sui-field-quota')).toHaveAttribute('min', '0')
  await page.getByTestId('sui-submit').click()
  await expect(row(page, 'EU')).toContainText('Europe')
  await expect(page.getByTestId('sui-form')).toHaveCount(0)
  expect(store.rows('territories', 'territory')).toEqual([{ code: 'EU', name: 'Europe', tier: 'gold', quota: 10.5 }])
  await page.getByTestId('sui-new').click()
  await fill(page, { code: 'EU', name: 'Duplicate' })
  await page.getByTestId('sui-submit').click()
  await expect(page.getByTestId('sui-violation')).toContainText('already has a row')
  await expect(page.getByTestId('sui-violation')).toHaveAttribute('data-rule', 'key')
  await page.getByTestId('sui-cancel').click()
  expect(store.rows('territories', 'territory')).toHaveLength(1)

  // edit it: the key is fixed, a change is saved, and the history lists both versions
  await row(page, 'EU').click()
  await expect(page.getByTestId('sui-form-title')).toHaveText('Territory EU')
  await expect(page.getByTestId('sui-field-code')).toBeDisabled()
  await fill(page, { name: 'Europe West' })
  await page.getByTestId('sui-submit').click()
  await expect(row(page, 'EU')).toContainText('Europe West')
  await row(page, 'EU').click()
  await expect(page.getByTestId('sui-history')).toBeVisible()
  await expect(page.getByTestId('sui-version')).toHaveCount(2)

  // somebody else changed the row after it was read: the save is refused and says so
  store.touch('territories', 'territory', { code: 'EU' }, { quota: 99 })
  await fill(page, { name: 'Europe East' })
  await page.getByTestId('sui-submit').click()
  await expect(page.getByTestId('sui-conflict')).toContainText('changed by someone else')
  await expect(page.getByTestId('sui-conflict')).toContainText('Reload the row')
  await page.getByTestId('sui-cancel').click()
  await page.reload()
  await expect(row(page, 'EU')).toContainText('99')
  await expect(row(page, 'EU')).toContainText('Europe West')

  // delete
  await row(page, 'EU').click()
  await page.getByTestId('sui-delete').click()
  await expect(page.getByTestId('sui-empty')).toBeVisible()
  expect(store.rows('territories', 'territory')).toEqual([])

  // an instance has rows of its own: picked from the list, kept in the address, apart from the model's own copy
  await page.getByTestId('sui-instance-select').selectOption('emea')
  await expect(page).toHaveURL(/instance=emea$/)
  await expect(page.getByTestId('sui-empty')).toBeVisible()
  await page.getByTestId('sui-new').click()
  await fill(page, { code: 'ME', name: 'Middle East', quota: '3' })
  await page.getByTestId('sui-submit').click()
  await expect(row(page, 'ME')).toBeVisible()
  expect(store.rows('territories', 'territory', 'emea')).toHaveLength(1)
  await page.getByTestId('sui-instance-select').selectOption('')
  await expect(page).not.toHaveURL(/instance=/)
  await expect(page.getByTestId('sui-empty')).toBeVisible()
  expect(store.rows('territories', 'territory')).toEqual([])
}
