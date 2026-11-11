import { expect, test, type Page } from '@playwright/test'
import { signInNewAdmin } from './people'
import { SERVER, mockWorkspace } from './workspace-fake'

// Plan 33, SC20m: the forms for Build's files in a draft of a Build project: a model's definition as a form from Build's own schema, a new model by kind (the two files `project model create` writes), a new connection with
// its grants and serving policy. The Workspace server is mocked in the page (`workspace-fake.ts`). It signs in a new administrator, so its name sorts after `first-run` and `identity-home`.
test.setTimeout(120_000)

async function openBuildDraft(page: Page, id: string, workspace: string) {
  await page.goto(`/servers/${SERVER}`)
  await page.getByTestId('ws-new-project-open').click()
  await page.getByTestId('ws-new-project-id').fill(id)
  await page.getByTestId('ws-new-project-kind').selectOption('build')
  await page.getByTestId('ws-new-project-create').click()
  await page.getByTestId(`ws-new-name-${id}`).fill(workspace)
  await page.getByTestId(`ws-new-create-${id}`).click()
  const listed = page.locator(`[data-testid="ws-workspace"][data-workspace$="-${workspace}"]`)
  await expect(listed).toBeVisible()
  await page.getByTestId(`ws-open-${await listed.getAttribute('data-workspace')}`).click()
  await expect(page.getByTestId('wsp-page')).toBeVisible()
}

test('the files of a Build project are edited as forms: a model, a new model by kind, a connection with grants and policy', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Build Forms', `workspace-build-forms-${Date.now()}@example.com`)
  const fake = await mockWorkspace(page)
  await openBuildDraft(page, 'forms', 'forms-edit')
  // a model's definition opens as a form made from Build's schema, with the YAML one click away
  await page.locator('[data-testid="wsp-file"][data-path="models/staging/orders.yml"]').click()
  await expect(page.getByTestId('wsp-structured')).toBeVisible()
  await expect(page.getByTestId('schema-form')).toBeVisible()
  await page.getByTestId('wsp-mode-yaml').click()
  await expect(page.getByTestId('wsp-yaml')).toBeVisible()
  // a new model by kind: the name is checked as you type, then the definition and the query are in the draft
  await page.getByTestId('wsp-build-new-model-name').fill('marts')
  await expect(page.getByTestId('wsp-build-new-model-why')).toContainText('schema_name.object_name')
  await expect(page.getByTestId('wsp-build-new-model-go')).toBeDisabled()
  await page.getByTestId('wsp-build-new-model-name').fill('marts.fct_returns')
  await page.getByTestId('wsp-build-new-model-kind').selectOption('incremental_by_unique_key')
  await page.getByTestId('wsp-build-new-model-go').click()
  await expect(page.getByTestId('wsp-editor')).toHaveAttribute('data-path', 'models/marts/fct_returns.yml')
  const files = fake.files('forms', 'forms-edit')!
  expect(files.get('models/marts/fct_returns.yml')).toContain('incremental_by_unique_key')
  expect(files.get('models/marts/fct_returns.sql')).toContain('SELECT CAST(1 AS BIGINT) AS id')
  // a name already in the draft is refused
  await page.getByTestId('wsp-build-new-model-name').fill('marts.fct_returns')
  await expect(page.getByTestId('wsp-build-new-model-why')).toContainText('exists already')
  // a new connection: its file holds no address and no login; the grants and the policy are edited in it
  await page.getByTestId('wsp-build-new-connection-name').fill('warehouse')
  await page.getByTestId('wsp-build-new-connection-engine').selectOption('postgres')
  await page.getByTestId('wsp-build-new-connection-go').click()
  await expect(page.getByTestId('wsp-editor')).toHaveAttribute('data-path', 'connections/warehouse.yaml')
  expect(files.get('connections/warehouse.yaml')).toContain('engine: postgres')
  await expect(page.getByTestId('build-grants')).toBeVisible()
  await page.getByTestId('build-grants-group-input').fill('analysts')
  await page.getByTestId('build-grants-add').click()
  await expect.poll(() => files.get('connections/warehouse.yaml') ?? '').toContain('analysts')
  expect(files.get('connections/warehouse.yaml')).toContain('connection/2')
})
