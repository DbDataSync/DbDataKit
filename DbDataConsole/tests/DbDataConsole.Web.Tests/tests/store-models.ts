import { expect, type Page } from '@playwright/test'
import { SERVER, mockStore } from './store-fake'

const ledger = [
  'schemaVersion: 1', 'name: ledger', 'storage: {connection: mdm}', 'entities:', '  account:', '    key: [code]', '    attributes:',
  '      code: {type: VARCHAR(10), required: true}', '      name: {type: VARCHAR(100), required: true}', '',
].join('\n')

const field = (page: Page, path: string) => page.getByTestId(`form-field-${path}`)

/**
 * Plan 33, UE13a: the Store's model screens, through the console (a Store server answering through the relay, `store-fake.ts`). A person makes a model from the starting text, breaks it and reads the Store's own diagnostics with
 * Save off and saying why, fixes it and saves; adds an attribute; removes the model (the storage and migration steps are UE13b's). Then a person who may only read,
 * and one who may write but not remove, see the buttons off with the reason, and a save of a model that somebody else saved meanwhile is refused with a way out.
 */
export async function modelsStory(page: Page) {
  const store = await mockStore(page, { role: 'admin' })
  await page.goto(`/servers/${SERVER}`)
  await expect(page.getByTestId('store-models-empty')).toBeVisible()
  await page.getByTestId('store-model-new').click()
  await expect(page.getByTestId('store-new-model')).toBeVisible()

  // the starting text is valid as it stands, but a model needs its name first and Save says so
  const save = page.getByTestId('model-save')
  await expect(save).toBeDisabled()
  await expect(page.getByTestId('model-save-why')).toContainText('Give the model a name')
  await page.getByTestId('model-name-input').fill('sales_territories')
  await expect(page.getByTestId('model-diagnostics')).toHaveAttribute('data-state', 'valid')
  await expect(save).toBeEnabled()

  // the YAML is the same text as the form
  await page.getByTestId('model-mode-yaml').click()
  await expect(page.getByTestId('model-yaml')).toBeVisible()
  await page.getByTestId('model-mode-form').click()

  // a type the models do not know: the Store's sentence and its line, Save off with the count
  const type = field(page, 'entities.item.attributes.code.type')
  await type.fill('NOT_A_TYPE')
  await expect(page.getByTestId('model-diagnostics')).toHaveAttribute('data-state', 'invalid')
  const first = page.getByTestId('model-diagnostic').first()
  await expect(first).toContainText('`NOT_A_TYPE` is not a type the models know')
  await expect(first).toContainText('line 9')
  await expect(save).toBeDisabled()
  await expect(page.getByTestId('model-save-why')).toContainText('The Store refuses this model: 1 problem to fix')

  // fixed, it saves, and the model has its own page
  await type.fill('VARCHAR(20)')
  await expect(page.getByTestId('model-diagnostics')).toHaveAttribute('data-state', 'valid')
  await save.click()
  await expect(page).toHaveURL(new RegExp(`/servers/${SERVER}/model/sales_territories$`))
  await expect(page.getByTestId('model-editor')).toBeVisible()
  expect(store.text('sales_territories')).toContain('name: sales_territories')
  await expect(page.getByTestId('model-save')).toBeDisabled()
  await expect(page.getByTestId('model-save-why')).toContainText('Nothing has changed')

  // an attribute is added in the form; the edit shows as a change and is saved
  await page.getByTestId('model-tab-definition').click()
  await page.getByTestId('form-newkey-entities.item.attributes').fill('region')
  await page.getByTestId('form-addkey-entities.item.attributes').click()
  await field(page, 'entities.item.attributes.region.type').fill('VARCHAR(40)')
  await expect(page.getByTestId('model-diagnostics')).toHaveAttribute('data-state', 'valid')
  await page.getByTestId('model-diff-toggle').click()
  await expect(page.getByTestId('model-diff')).toBeVisible()
  await page.getByTestId('model-save').click()
  await expect(page.getByTestId('model-saved')).toContainText('Saved as version')
  expect(store.text('sales_territories')).toContain('region')

  // removing the model asks first, says what it leaves, and the list is empty again
  await page.goto(`/servers/${SERVER}/model/sales_territories`)
  await page.getByTestId('model-remove').click()
  await expect(page.getByTestId('model-remove-dialog')).toContainText('nothing is dropped')
  await page.getByTestId('model-remove-cancel').click()
  await expect(page.getByTestId('model-remove-dialog')).toHaveCount(0)
  await page.getByTestId('model-remove').click()
  await page.getByTestId('model-remove-confirm').click()
  await expect(page.getByTestId('store-models-empty')).toBeVisible()
  expect(store.text('sales_territories')).toBeUndefined()

  // a person who may only read: everything is off, each with its reason
  store.edit('ledger', ledger)
  store.setRole('viewer')
  await page.goto(`/servers/${SERVER}/model/ledger`)
  await expect(page.getByTestId('model-save')).toBeDisabled()
  await expect(page.getByTestId('model-save-why')).toContainText('needs the author or administrator role')
  await expect(page.getByTestId('model-remove')).toBeDisabled()
  await expect(page.getByTestId('model-remove-why')).toContainText('needs the administrator role')
  // an author may write but not remove, and a save of a model somebody else saved meanwhile is refused with both ways out
  store.setRole('author')
  await page.goto(`/servers/${SERVER}/model/ledger`)
  await expect(page.getByTestId('model-remove')).toBeDisabled()
  await page.getByTestId('form-add-title').click()
  await field(page, 'title').fill('Ledger')
  store.edit('ledger', ledger.replace('VARCHAR(100)', 'VARCHAR(120)'))
  await expect(page.getByTestId('model-diagnostics')).toHaveAttribute('data-state', 'valid')
  await page.getByTestId('model-save').click()
  await expect(page.getByTestId('model-conflict')).toContainText('Someone saved this model after you opened it')
  await page.getByTestId('model-conflict-reload').click()
  await expect(page.getByTestId('model-saved')).toContainText('Loaded the version someone else saved')
  await expect(page.getByTestId('model-conflict')).toHaveCount(0)
}
