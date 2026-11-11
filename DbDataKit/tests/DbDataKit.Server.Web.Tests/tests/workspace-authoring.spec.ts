import { expect, test, type Page } from '@playwright/test'
import { signInNewAdmin } from './people'
import { SERVER, mockWorkspace, type FakeWorkspace } from './workspace-fake'

// Plan 33, SC20f, SC21d and SC22d: the authoring flow of the three kinds that became project kinds of the Workspace, through the console: make a project of each kind, make a workspace, edit (a Store model as a form,
// a Sync connection and task, a Build model as text), see the kind's own check as the text changes or on demand, fix what it found, and publish to a target. The Workspace server is mocked in the page (`workspace-fake.ts`;
// the real routes are DbDataWorkspace.Tests and the library's tests), so the story needs the signed-in console only and takes no port. It signs in a new administrator, so its name sorts after `first-run` and
// `identity-home` (ci-lessons: a spec that signs in a new administrator sorts after them).
test.setTimeout(120_000)

async function makeProject(page: Page, id: string, kind: string, workspace: string) {
  await page.getByTestId('ws-new-project-open').click()
  await page.getByTestId('ws-new-project-id').fill(id)
  await page.getByTestId('ws-new-project-kind').selectOption(kind)
  await expect(page.getByTestId('ws-new-project-kind-help')).toContainText('Published to')
  await page.getByTestId('ws-new-project-create').click()
  await expect(page.locator(`[data-testid="ws-project"][data-project="${id}"]`)).toBeVisible()
  await page.getByTestId(`ws-new-name-${id}`).fill(workspace)
  await page.getByTestId(`ws-new-create-${id}`).click()
  const listed = page.locator(`[data-testid="ws-workspace"][data-workspace$="-${workspace}"]`)
  await expect(listed).toBeVisible()
  await page.getByTestId(`ws-open-${await listed.getAttribute('data-workspace')}`).click()
  await expect(page.getByTestId('wsp-page')).toBeVisible()
}

const openFile = (page: Page, path: string) => page.locator(`[data-testid="wsp-file"][data-path="${path}"]`).click()
const live = (page: Page) => page.getByTestId('wsp-live')

/** Builds the draft and publishes it to a target; the answer line is the server's. */
async function publish(page: Page, target: string, channel?: string) {
  await page.getByTestId('wsp-tab-builds').click()
  await page.getByTestId('wsp-build').click()
  const done = page.locator('[data-testid="wsp-build-detail"][data-status="succeeded"]')
  await expect(done).toBeVisible({ timeout: 15_000 })
  await done.getByTestId('wsp-publish-target').fill(target)
  if (channel) await done.getByTestId('wsp-publish-channel').fill(channel)
  await done.getByTestId('wsp-publish').click()
  await expect(done.getByTestId('wsp-published')).toBeVisible()
  await expect(done.locator('[data-testid="wsp-publish-record"][data-ok="true"]')).toContainText(`tester to ${target}`)
}

async function storeFlow(page: Page, fake: FakeWorkspace) {
  await makeProject(page, 'sales', 'store', 'edit')
  // the model of the template opens as a form from the Store's own schema; the draft is checked and is sound
  await openFile(page, 'models/sales.yaml')
  await expect(page.getByTestId('wsp-structured')).toBeVisible()
  await expect(live(page)).toHaveAttribute('data-state', 'current', { timeout: 10_000 })
  await expect(live(page)).toHaveAttribute('data-errors', '0')
  // a type the models do not know: saved into the draft a moment after the last keystroke, and the Store's sentence comes back with its line
  await page.getByTestId('form-field-entities.item.attributes.code.type').fill('NOT_A_TYPE')
  const finding = page.locator('[data-testid="wsp-live-finding"][data-code="DDM-204"]')
  await expect(finding).toBeVisible({ timeout: 10_000 })
  await expect(finding).toContainText('line')
  await expect.poll(() => fake.files('sales', 'edit')?.get('models/sales.yaml') ?? '').toContain('NOT_A_TYPE')
  await page.getByTestId('form-field-entities.item.attributes.code.type').fill('VARCHAR(20)')
  await expect(live(page)).toHaveAttribute('data-errors', '0', { timeout: 10_000 })
  await expect(finding).toHaveCount(0)
  // a new model from the starting text, named as you type
  await page.getByTestId('wsp-new-model-name').fill('Ledger')
  await expect(page.getByTestId('wsp-new-model-why')).toContainText('lower-case')
  await expect(page.getByTestId('wsp-new-model-go')).toBeDisabled()
  await page.getByTestId('wsp-new-model-name').fill('ledger')
  await page.getByTestId('wsp-new-model-go').click()
  await expect(page.getByTestId('wsp-editor')).toHaveAttribute('data-path', 'models/ledger.yaml')
  expect(fake.files('sales', 'edit')?.get('models/ledger.yaml')).toContain('schemaVersion: 2')
  // make the models and publish them to a Gateway; a Runner's channel is not asked for
  await publish(page, 'edge')
  expect(fake.published.at(-1)).toMatchObject({ project: 'sales', body: { target: 'edge' } })
}

async function syncFlow(page: Page, fake: FakeWorkspace) {
  await page.goto(`/servers/${SERVER}`)
  await makeProject(page, 'plant', 'sync', 'edit')
  await expect(page.getByTestId('wsp-sync-set')).toBeVisible()
  await expect(page.locator('[data-testid="wsp-sync-replication"]')).toHaveCount(1)
  await expect(page.locator('[data-testid="wsp-sync-connection"]')).toHaveCount(2)
  // a new connection names its secret; a name in use is refused as you type
  await page.getByTestId('wsp-new-connection-name').fill('source-db')
  await expect(page.getByTestId('wsp-new-connection-why')).toContainText('already exists')
  await page.getByTestId('wsp-new-connection-name').fill('third-db')
  await page.getByTestId('wsp-new-connection-go').click()
  await expect(page.locator('[data-testid="wsp-sync-connection"]')).toHaveCount(3)
  expect(fake.files('plant', 'edit')?.get('config/connections/third-db.yaml')).toContain('password_secret: secret:dbdata:sync:connection:third-db')
  // a new replication between two of them: a task and a mapping
  await page.getByTestId('wsp-new-replication-name').fill('stock')
  await page.getByTestId('wsp-new-replication-go').click()
  await expect(page.locator('[data-testid="wsp-sync-replication"]')).toHaveCount(2)
  expect([...fake.files('plant', 'edit')!.keys()]).toContain('config/replications/stock/table-mappings/stock.yaml')
  // a password where a reference belongs: refused at once with its line, and the check of the draft says the same
  await page.locator('[data-testid="wsp-sync-connection"][data-name="source-db"] [data-testid="wsp-sync-open-connection"]').click()
  await page.getByTestId('form-field-auth.password_secret').fill('hunter2')
  await expect(page.getByTestId('wsp-secret-finding')).toContainText('Line')
  await expect(page.locator('[data-testid="wsp-live-finding"][data-code="DBD-WS-SYNC-SECRET"]')).toBeVisible({ timeout: 10_000 })
  await page.getByTestId('form-field-auth.password_secret').fill('secret:dbdata:sync:connection:source-db')
  await expect(page.getByTestId('wsp-secret-finding')).toHaveCount(0)
  await expect(live(page)).toHaveAttribute('data-errors', '0', { timeout: 10_000 })
  // a form edit of a task that was read as format 1 writes the current format
  await page.locator('[data-testid="wsp-sync-replication"][data-name="orders"] [data-testid="wsp-sync-open-task"]').click()
  await page.getByTestId('form-field-scheduling.cronExpression').fill('0 * * * *')
  await expect.poll(() => fake.files('plant', 'edit')?.get('config/replications/orders/task.yaml') ?? '').toContain('schemaVersion: 2')
  // publish to a Runner: the channel is asked for
  await publish(page, 'plant', 'main')
  expect(fake.published.at(-1)).toMatchObject({ project: 'plant', body: { target: 'plant', channel: 'main' } })
}

async function buildFlow(page: Page) {
  await page.goto(`/servers/${SERVER}`)
  await makeProject(page, 'warehouse', 'build', 'edit')
  // the models by folder and the compiled plan, read from the draft
  await page.getByTestId('wsp-tab-models').click()
  await expect(page.locator('[data-testid="wsp-build-model"]')).toHaveCount(2)
  await expect(page.locator('[data-testid="wsp-build-refresh-plan"][data-name="hourly"]')).toContainText('2 loads')
  // SC20h: the load graph, read from the committed loads file of the draft (Build's own picture): a node per model, an edge for the one dependency; choosing one is Build's own behaviour
  await expect(page.getByTestId('build-graph-node-orders')).toBeVisible()
  await expect(page.getByTestId('build-graph-node-fct_sales')).toBeVisible()
  await expect(page.getByTestId('build-graph-edge')).toHaveCount(1)
  await expect(page.getByTestId('wsp-build-graph-count')).toContainText('2 models, 1 edge')
  // a model that reads a table nothing defines: the check names the model, its code and its line
  await page.getByTestId('wsp-tab-changes').click()
  await page.getByTestId('wsp-new-path').fill('models/staging/bad.sql')
  await page.getByTestId('wsp-new-file').click()
  await expect(page.getByTestId('wsp-editor')).toHaveAttribute('data-path', 'models/staging/bad.sql')
  await page.getByTestId('wsp-code').locator('.monaco-editor').click()
  await page.keyboard.insertText('SELECT * FROM nowhere')
  await page.getByTestId('wsp-save').click()
  await expect(page.getByTestId('wsp-dirty')).toHaveCount(0)
  await page.getByTestId('wsp-tab-models').click()
  await page.getByTestId('wsp-build-check').click()
  await expect(page.locator('[data-testid="wsp-build-model"][data-name="bad"]')).toHaveAttribute('data-errors', '1')
  await expect(page.locator('[data-testid="wsp-check-finding"][data-code="DDB-301"]')).toBeVisible()
  // take it out again: sound, and the package publishes to a Runner
  await openFile(page, 'models/staging/bad.sql')
  await page.getByTestId('wsp-delete').click()
  await page.getByTestId('wsp-delete-confirm').click()
  await page.getByTestId('wsp-build-check').click()
  await expect(page.locator('[data-testid="wsp-check-result"]')).toHaveAttribute('data-ok', 'true')
  await publish(page, 'plant', 'main')
}

test('a project of each kind is made in the Workspace, edited with the kind\'s own check beside it, fixed and published', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Workspace Author', `workspace-authoring-${Date.now()}@example.com`)
  const fake = await mockWorkspace(page)
  await page.goto(`/servers/${SERVER}`)
  await expect(page.getByTestId('ws-projects')).toBeVisible()
  await storeFlow(page, fake)
  await syncFlow(page, fake)
  await buildFlow(page)
})
