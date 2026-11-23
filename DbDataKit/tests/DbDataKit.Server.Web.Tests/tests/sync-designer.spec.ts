import { expect, test, type Page } from '@playwright/test'
import YAML from 'yaml'
import { signInNewAdmin } from './people'
import { SERVER, mockWorkspace } from './workspace-fake'

// The two areas of a draft (Files and Designer, DbDataWorkspace/README.md) and the sync Designer, through the console against a Workspace server
// mocked in the page (`workspace-fake.ts`), so the story needs the signed-in console only and takes no port. It signs in a new administrator, so its
// name sorts after `first-run` and `identity-home` (ci-lessons: a spec that signs in a new administrator sorts after them).
test.setTimeout(120_000)

async function makeDraft(page: Page, id: string, kind: string, workspace: string) {
  await page.goto(`/servers/${SERVER}`)
  await expect(page.getByTestId('ws-projects')).toBeVisible()
  await page.getByTestId('ws-new-project-open').click()
  await page.getByTestId('ws-new-project-id').fill(id)
  await page.getByTestId('ws-new-project-kind').selectOption(kind)
  await page.getByTestId('ws-new-project-create').click()
  await expect(page.locator(`[data-testid="ws-project"][data-project="${id}"]`)).toBeVisible()
  await page.getByTestId(`ws-new-name-${id}`).fill(workspace)
  await page.getByTestId(`ws-new-create-${id}`).click()
  const listed = page.locator(`[data-testid="ws-workspace"][data-workspace$="-${workspace}"]`)
  await expect(listed).toBeVisible()
  await page.getByTestId(`ws-open-${await listed.getAttribute('data-workspace')}`).click()
  await expect(page.getByTestId('wsp-page')).toBeVisible()
}

const area = (page: Page) => page.getByTestId('wsp-page')

/**
 * The draft's catalog cache in the Designer's stories: `source-db` read from the Runner group `runners-eu` in `dev`, with the databases `App` and
 * `Archive` and three tables of `App`; `target-db` has not been read, so its card and pickers say the cache has nothing for it.
 */
const CACHE = [
  'schema: catalog-cache/2', "generated_at: '2026-11-20T10:00:00Z'", 'connections:', "  'source-db':", "    engine: 'sqlserver'",
  "    read: {server: 'runners-eu', product: 'runner', channel: 'dev', at: '2026-11-20T10:00:00Z'}",
  '    databases:', "      - {name: 'App', schemas: ['dbo']}", "      - {name: 'Archive', schemas: ['dbo']}", '    tables:',
  "      - database: 'App'", "        schema: 'dbo'", "        name: 'Customers'", '        kind: table', '        columns:',
  "          - {name: 'id', type: 'int', nullable: false, key: true}", "          - {name: 'name', type: 'nvarchar(100)', nullable: true}",
  "      - database: 'App'", "        schema: 'dbo'", "        name: 'Invoices'", '        kind: table', '        columns:',
  "          - {name: 'id', type: 'int', nullable: false, key: true}", "          - {name: 'customer_id', type: 'int', nullable: false}",
  "          - {name: 'total', type: 'decimal(12,2)', nullable: true}",
  "      - database: 'App'", "        schema: 'dbo'", "        name: 'Orders'", '        kind: table', '        columns:',
  "          - {name: 'id', type: 'int', nullable: false, key: true}", "          - {name: 'customer_id', type: 'int', nullable: false}",
  "          - {name: 'total', type: 'decimal(12,2)', nullable: true}", '',
].join('\n')

/** The cache with `target-db` read too: its database `dw` has `dbo.Orders` only, so a mapping of another table targets one provisioning creates. */
const CACHE_WITH_TARGET = CACHE + [
  "  'target-db':", "    engine: 'postgres'", "    read: {server: 'runners-eu', product: 'runner', channel: 'dev', at: '2026-11-20T10:00:00Z'}",
  '    databases:', "      - {name: 'dw', schemas: ['dbo', 'staging']}", '    tables:',
  "      - database: 'dw'", "        schema: 'dbo'", "        name: 'Orders'", '        kind: table', '        columns:',
  "          - {name: 'id', type: 'integer', nullable: false, key: true}", '',
].join('\n')

type Fake = Awaited<ReturnType<typeof mockWorkspace>>

/** A file of the draft as the fake Workspace server holds it (what was saved), parsed. */
function saved(workspace: Fake, draft: string, path: string): Record<string, any> {
  const text = workspace.files('plant', draft)?.get(path)
  expect(text, `${path} is saved`).toBeDefined()
  return YAML.parse(text!) as Record<string, any>
}

/** A picture of the screen for people reviewing the Designer, written only when DBDATAKIT_TEST_SHOTS names a folder (not in CI). */
async function shot(page: Page, name: string) {
  const folder = process.env.DBDATAKIT_TEST_SHOTS
  if (folder) await page.screenshot({ path: `${folder}/${name}.png`, fullPage: false })
}

test('a draft has two areas: an app draft shows Files only, a sync draft opens on its Designer, and an unsaved edit and the address survive moving between them', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Sync Designer', `sync-designer-${Date.now()}@example.com`)
  await mockWorkspace(page)

  // a kind without a Designer: only Files, and no Designer entries
  await makeDraft(page, 'site', 'app', 'site-edit')
  await expect(area(page)).toHaveAttribute('data-area', 'files')
  await expect(page).toHaveURL(/\/files$/)
  await expect(page.getByTestId('wsp-nav-designer')).toHaveCount(0)

  // a sync draft lands on its Designer, on its first replication; the navigation lists the replications under Designer and the files under Files
  await makeDraft(page, 'plant', 'sync', 'designer-edit')
  await expect(area(page)).toHaveAttribute('data-area', 'designer')
  await expect(page).toHaveURL(/\/designer\/replications\/orders$/)
  await expect(page.locator('[data-testid="sd-nav-replication"]')).toHaveCount(1)
  await expect(page.locator('[data-testid="sd-nav-replication"][data-name="orders"]')).toHaveAttribute('aria-current', 'page')
  await expect(page.locator('[data-testid="wsp-file"][data-path="config/replications/orders/task.yaml"]')).toBeVisible()
  await shot(page, '01-designer-landing')

  // an edit in Files left not saved is still there, not saved, after a click into the Designer and back
  await page.getByTestId('wsp-nav-files').click()
  await expect(area(page)).toHaveAttribute('data-area', 'files')
  await page.getByTestId('wsp-new-path').fill('notes.md')
  await page.getByTestId('wsp-new-file').click()
  await expect(page.getByTestId('wsp-editor')).toHaveAttribute('data-path', 'notes.md')
  await page.getByTestId('wsp-code').locator('.monaco-editor').click()
  await page.keyboard.insertText('Orders move every quarter hour.')
  await expect(page.getByTestId('wsp-dirty')).toBeVisible()
  await expect(page.getByTestId('wsp-unsaved')).toHaveAttribute('data-count', '1')
  await page.locator('[data-testid="sd-nav-replication"][data-name="orders"]').click()
  await expect(area(page)).toHaveAttribute('data-area', 'designer')
  await expect(page.getByTestId('wsp-unsaved')).toHaveAttribute('data-count', '1')
  await expect(page.locator('[data-testid="wsp-file"][data-path="notes.md"]')).toHaveAttribute('data-dirty', 'true')
  await page.locator('[data-testid="wsp-file"][data-path="notes.md"]').click()
  await expect(page.getByTestId('wsp-dirty')).toBeVisible()
  await expect(page.getByTestId('wsp-code')).toContainText('Orders move every quarter hour.')
  await shot(page, '02-files-unsaved-edit')

  // the chrome's Save is the same in both areas
  await page.getByTestId('wsp-save-all').click()
  await expect(page.getByTestId('wsp-unsaved')).toHaveCount(0)

  // a reload keeps the area the address names
  await page.reload()
  await expect(area(page)).toHaveAttribute('data-area', 'files')
  await expect(page.getByTestId('wsp-editor')).toHaveAttribute('data-path', 'notes.md')
  await page.locator('[data-testid="sd-nav-replication"][data-name="orders"]').click()
  await expect(page).toHaveURL(/\/designer\/replications\/orders$/)
  await page.reload()
  await expect(area(page)).toHaveAttribute('data-area', 'designer')
  await expect(page.getByTestId('sd-replication')).toHaveAttribute('data-name', 'orders')
})

test('a replication from New replication to its endpoints and schedule, Enabled and Delete, each change an unsaved edit Files shows', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Sync Replication', `sync-replication-${Date.now()}@example.com`)
  const workspace = await mockWorkspace(page, { syncFiles: { 'catalog-cache.yaml': CACHE } })
  await makeDraft(page, 'plant', 'sync', 'replication-edit')
  await expect(page).toHaveURL(/\/designer\/replications\/orders$/)

  // New replication, the navigation's last entry: a dialog with the name and the schedule; the task is made as an unsaved edit and its Overview opens
  await page.getByTestId('sd-new-replication').click()
  await expect(page.getByTestId('sd-new-replication-dialog')).toBeVisible()
  await page.getByTestId('replication-name-input').fill('invoices')
  await page.getByTestId('create-replication-button').click()
  await expect(page).toHaveURL(/\/designer\/replications\/invoices$/)
  await expect(page.getByTestId('sd-replication')).toHaveAttribute('data-name', 'invoices')
  await expect(page.getByTestId('sd-crumbs')).toContainText('invoices')
  await expect(page.getByTestId('replication-overview')).toBeVisible()
  await expect(page.getByTestId('enabled-toggle')).toHaveAttribute('aria-pressed', 'false')
  await expect(page.locator('[data-testid="wsp-file"][data-path="config/replications/invoices/task.yaml"]')).toHaveAttribute('data-dirty', 'true')

  // the two connections and a database: the databases are the catalog cache's, with where they were read; the target has no cache and says so
  await page.getByTestId('task-source-connection-select').selectOption('source-db')
  await expect(page.locator('[data-testid="task-source-database-select"] option')).toHaveText(['Select…', 'App', 'Archive'])
  await page.getByTestId('task-source-database-select').selectOption('App')
  await expect(page.getByTestId('task-source-read')).toContainText('read from runners-eu (runner) in dev')
  await page.getByTestId('task-target-connection-select').selectOption('target-db')
  await expect(page.getByTestId('task-target-no-cache')).toContainText('has nothing for the connection target-db')
  await expect(page.locator('[data-testid="task-target-database-select"] option')).toHaveText(['Select…'])
  await page.getByTestId('save-settings-button').click()
  await expect(page.getByTestId('sd-task-dirty')).toHaveCount(0)
  const before = saved(workspace, 'replication-edit', 'config/replications/invoices/task.yaml')
  expect(before.endpoints).toEqual({ source: { connectionName: 'source-db', database: 'App' }, target: { connectionName: 'target-db' } })
  expect(before.scheduling).toEqual({ mode: 'Continuous', frequencySeconds: 60 })
  await shot(page, '03-replication-overview')

  // the schedule to Periodic with a cron: an unsaved edit Files shows, still there after a click into Files and back
  await page.getByTestId('schedule-mode-select').selectOption('Periodic')
  await page.getByTestId('schedule-cron-input').fill('0 2 * * *')
  await expect(page.getByTestId('sd-task-dirty')).toBeVisible()
  await page.locator('[data-testid="wsp-file"][data-path="config/replications/invoices/task.yaml"]').click()
  await expect(area(page)).toHaveAttribute('data-area', 'files')
  await expect(page.getByTestId('wsp-dirty')).toBeVisible()
  await page.getByTestId('wsp-mode-yaml').click()
  await expect(page.getByTestId('wsp-code')).toContainText('cronExpression')
  await expect(page.getByTestId('wsp-code')).not.toContainText('frequencySeconds')
  await page.locator('[data-testid="sd-nav-replication"][data-name="invoices"]').click()
  await expect(area(page)).toHaveAttribute('data-area', 'designer')
  await expect(page.getByTestId('schedule-mode-select')).toHaveValue('Periodic')
  await expect(page.getByTestId('schedule-cron-input')).toHaveValue('0 2 * * *')
  await expect(page.getByTestId('wsp-unsaved')).toHaveAttribute('data-count', '1')

  // saved, only the schedule's keys changed: the other mode's setting is gone, everything else is as it was
  await page.getByTestId('wsp-save-all').click()
  await expect(page.getByTestId('wsp-unsaved')).toHaveCount(0)
  const after = saved(workspace, 'replication-edit', 'config/replications/invoices/task.yaml')
  expect(after.scheduling).toEqual({ mode: 'Periodic', cronExpression: '0 2 * * *' })
  expect({ ...after, scheduling: null }).toEqual({ ...before, scheduling: null })

  // Enabled off on another replication marks the draft not saved, and nothing is written until Save
  await page.locator('[data-testid="sd-nav-replication"][data-name="orders"]').click()
  await expect(page.getByTestId('sd-replication')).toHaveAttribute('data-name', 'orders')
  await expect(page.getByTestId('enabled-toggle')).toHaveAttribute('aria-pressed', 'true')
  await page.getByTestId('enabled-toggle').click()
  await expect(page.getByTestId('enabled-toggle')).toHaveAttribute('aria-pressed', 'false')
  await expect(page.getByTestId('sd-task-dirty')).toBeVisible()
  await expect(page.getByTestId('wsp-unsaved')).toHaveAttribute('data-count', '1')
  expect(saved(workspace, 'replication-edit', 'config/replications/orders/task.yaml').enabled).toBeUndefined()

  // Delete asks twice, then the replication's folder is gone from the draft and from Files
  await page.getByTestId('delete-replication-button').click()
  await expect(page.getByTestId('delete-replication-ask')).toHaveAttribute('data-step', '1')
  await page.getByTestId('delete-replication-confirm').click()
  await expect(page.getByTestId('delete-replication-ask')).toHaveAttribute('data-step', '2')
  expect(workspace.files('plant', 'replication-edit')?.has('config/replications/orders/task.yaml')).toBe(true)
  await page.getByTestId('delete-replication-confirm').click()
  await expect(page.locator('[data-testid="sd-nav-replication"][data-name="orders"]')).toHaveCount(0)
  await expect(page.locator('[data-testid="wsp-file"][data-path^="config/replications/orders/"]')).toHaveCount(0)
  expect([...workspace.files('plant', 'replication-edit')!.keys()].filter((p) => p.startsWith('config/replications/orders/'))).toEqual([])
  await expect(page.getByTestId('sd-replication')).toHaveAttribute('data-name', 'invoices')
})

test('table mappings: Create mappings from two ticked tables, then Auto-map, the key and an overridden target schema on one, the same changes in Files', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Sync Mappings', `sync-mappings-${Date.now()}@example.com`)
  const workspace = await mockWorkspace(page, { syncFiles: { 'catalog-cache.yaml': CACHE_WITH_TARGET } })
  await makeDraft(page, 'plant', 'sync', 'mappings-edit')
  const mappingFile = (name: string) => `config/replications/orders/table-mappings/${name}.yaml`

  // the replication's endpoints, which every mapping made below inherits
  await page.getByTestId('task-source-connection-select').selectOption('source-db')
  await page.getByTestId('task-source-database-select').selectOption('App')
  await page.getByTestId('task-target-connection-select').selectOption('target-db')
  await page.getByTestId('task-target-database-select').selectOption('dw')
  await page.getByTestId('save-settings-button').click()
  await expect(page.getByTestId('sd-task-dirty')).toHaveCount(0)

  // the overview lists the source database's tables from the catalog cache, with how many mappings read each; the sidebar counts mapped columns
  await page.getByTestId('tab-mappings').click()
  await expect(page).toHaveURL(/\/designer\/replications\/orders\/mappings$/)
  await expect(page.locator('[data-testid^="select-table-"]')).toHaveCount(3)
  await expect(page.getByTestId('table-mapping-count-dbo.Orders')).toHaveText('1')
  await expect(page.getByTestId('table-mapping-count-dbo.Invoices')).toHaveText('unmapped')
  await expect(page.getByTestId('mapping-item-orders')).toContainText('0')

  // two tables ticked make two mapping files, unsaved; the target database has neither table, so the overview says provisioning creates them
  await page.getByTestId('select-table-dbo.Customers').check()
  await page.getByTestId('select-table-dbo.Invoices').check()
  await expect(page.getByTestId('create-mappings-button')).toHaveText('Create 2 mappings')
  await page.getByTestId('create-mappings-button').click()
  await expect(page.getByTestId('bulk-create-notes')).toContainText('it is created with these columns when the target is provisioned')
  await expect(page.getByTestId('mapping-item-dbo.Customers')).toContainText('2')
  await expect(page.getByTestId('mapping-item-dbo.Invoices')).toContainText('3')
  await expect(page.getByTestId('table-mapping-count-dbo.Invoices')).toHaveText('1')
  for (const name of ['dbo.Customers', 'dbo.Invoices']) await expect(page.locator(`[data-testid="wsp-file"][data-path="${mappingFile(name)}"]`)).toHaveAttribute('data-dirty', 'true')
  await expect(page.getByTestId('wsp-unsaved')).toHaveAttribute('data-count', '2')
  expect(workspace.files('plant', 'mappings-edit')?.has(mappingFile('dbo.Invoices'))).toBe(false)
  await shot(page, '04-mappings-overview')

  // on dbo.Invoices: two rows removed, then Auto-map by name maps the three columns again
  await page.getByTestId('mapping-item-dbo.Invoices').click()
  await expect(page.getByTestId('mapping-editor')).toHaveAttribute('data-name', 'dbo.Invoices')
  await expect(page.getByTestId('source-table-select').locator('option:checked')).toHaveText('dbo.Invoices')
  const rows = page.getByTestId('column-mapping-row')
  await expect(rows).toHaveCount(3)
  await page.getByTestId('column-mapping-remove-2').click()
  await page.getByTestId('column-mapping-remove-1').click()
  await expect(rows).toHaveCount(1)
  await page.getByTestId('auto-map-button').click()
  await expect(rows).toHaveCount(3)
  await expect(page.getByTestId('column-mapping-source-type-2')).toHaveText('decimal(12,2)')

  // the key: the source reports id; two columns ticked, then id unticked, leaves customer_id as the mapping's own key
  await expect(page.getByTestId('primary-key-source')).toContainText('The source reports a primary key: id.')
  await page.getByTestId('primary-key-column-id').check()
  await page.getByTestId('primary-key-column-customer_id').check()
  await page.getByTestId('primary-key-column-id').uncheck()
  await expect(page.getByTestId('primary-key-column-customer_id')).toBeChecked()
  await expect(page.getByTestId('primary-key-source')).toContainText('The columns chosen below replace it.')

  // the target side overridden for this table, into the schema staging, which the cache does not have: provisioning creates the table
  await page.getByTestId('target-override-toggle').click()
  await expect(page.getByTestId('target-connection-select')).toHaveValue('target-db')
  await page.getByTestId('target-schema-input').fill('staging')
  await expect(page.getByTestId('target-table-will-be-created')).toBeVisible()
  await expect(page.getByTestId('mapping-dirty')).toBeVisible()
  await shot(page, '05-mapping-editor')

  // Files shows the same file with the same changes, not saved; back in the Designer the edits are still there
  await page.locator(`[data-testid="wsp-file"][data-path="${mappingFile('dbo.Invoices')}"]`).click()
  await expect(area(page)).toHaveAttribute('data-area', 'files')
  await expect(page.getByTestId('wsp-dirty')).toBeVisible()
  await page.getByTestId('wsp-mode-yaml').click()
  await expect(page.getByTestId('wsp-code')).toContainText('staging')
  await expect(page.getByTestId('wsp-code')).toContainText('target-db')
  await page.locator('[data-testid="sd-nav-replication"][data-name="orders"]').click()
  await page.getByTestId('tab-mappings').click()
  await page.getByTestId('mapping-item-dbo.Invoices').click()
  await expect(page.getByTestId('mapping-dirty')).toBeVisible()
  await expect(page.getByTestId('target-schema-input')).toHaveValue('staging')
  await expect(page.getByTestId('primary-key-column-customer_id')).toBeChecked()

  // Save mapping writes that one file: what the server holds is what the screens showed
  await page.getByTestId('save-mapping-button').click()
  await expect(page.getByTestId('mapping-dirty')).toHaveCount(0)
  const invoices = saved(workspace, 'mappings-edit', mappingFile('dbo.Invoices'))
  expect(invoices.sources).toEqual([{ schema: 'dbo', table: 'Invoices' }])
  expect(invoices.targets).toEqual([{ connectionName: 'target-db', database: 'dw', schema: 'staging', table: 'Invoices' }])
  expect(invoices.columnMappings.map((c: { targetColumn: string }) => c.targetColumn)).toEqual(['id', 'customer_id', 'total'])
  expect(invoices.columnMappings.some((c: Record<string, unknown>) => 'targetType' in c)).toBe(false)
  expect(invoices.primaryKey).toEqual(['customer_id'])
  expect(workspace.files('plant', 'mappings-edit')?.has(mappingFile('dbo.Customers'))).toBe(false)
  await expect(page.getByTestId('wsp-unsaved')).toHaveAttribute('data-count', '1')
})
