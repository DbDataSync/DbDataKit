import { expect, test, type Page, type Route } from '@playwright/test'
import YAML from 'yaml'
import { signInNewAdmin } from './people'
import { RUNNER, SERVER, mockWorkspace } from './workspace-fake'

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

/** The cache with `target-db` read and a source table with no key, `dbo.Lines`, whose rows ship on a date. */
const CACHE_WITH_LINES = CACHE + [
  "      - database: 'App'", "        schema: 'dbo'", "        name: 'Lines'", '        kind: table', '        columns:',
  "          - {name: 'line_no', type: 'int', nullable: false}", "          - {name: 'order_id', type: 'int', nullable: false}",
  "          - {name: 'shipped_on', type: 'date', nullable: true}", '',
].join('\n') + CACHE_WITH_TARGET.slice(CACHE.length)

/** `orders` reading `App` into `dw` with the SCD Type 2 writer (whose natural key is a question per table), and a strategy that segments by month. */
const SCD2_TASK = [
  'schemaVersion: 2', 'name: orders', 'scheduling: {mode: Continuous, frequencySeconds: 60}', 'changeProcessing:', '  reader: {kind: BatchReload, options: {}}',
  '  cache: {kind: StagingTable, options: {}}', '  writer: {kind: Scd2, options: {}}', 'endpoints:', '  source: {connectionName: source-db, database: App}',
  '  target: {connectionName: target-db, database: dw}', 'segmentingStrategies:',
  "  - {name: by-month, kind: DuckDb, sql: \"SELECT month_start, month_end FROM range(DATE '2026-01-01', DATE '2027-01-01', INTERVAL 1 MONTH)\"}", '',
].join('\n')

test('the rest of a mapping: Create mappings with what is inferred, a verification and a segmenting by month, the Provisioning badge and link, the same changes in Files', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Sync Mapping Rest', `sync-mapping-rest-${Date.now()}@example.com`)
  const workspace = await mockWorkspace(page, { syncFiles: { 'catalog-cache.yaml': CACHE_WITH_LINES, 'config/replications/orders/task.yaml': SCD2_TASK } })
  await makeDraft(page, 'plant', 'sync', 'mapping-rest')
  const mappingFile = (name: string) => `config/replications/orders/table-mappings/${name}.yaml`

  // Create mappings asks the Workspace: dbo.Lines has no primary key, so no natural key can be derived; dbo.Customers's is its key
  await page.getByTestId('tab-mappings').click()
  await page.getByTestId('select-table-dbo.Lines').check()
  await page.getByTestId('select-table-dbo.Customers').check()
  await page.getByTestId('create-mappings-button').click()
  await expect(page.getByTestId('bulk-create-inferred-dbo.Lines')).toContainText('3 target types inferred')
  await expect(page.getByTestId('bulk-create-inferred-dbo.Lines')).toContainText('has no primary key')
  await expect(page.getByTestId('bulk-create-inferred-dbo.Customers')).toContainText('natural key id')
  await shot(page, '08-mappings-inferred')

  // Column Mapping shows each target type as inferred, and the file states none; the Pipeline tab shows the natural key, derived or why not
  await page.getByTestId('mapping-item-dbo.Lines').click()
  await expect(page.getByTestId('column-mapping-row')).toHaveCount(3)
  await expect(page.getByTestId('column-mapping-type-2-text')).toHaveText('date')
  await page.getByTestId('mapping-tab-pipeline').click()
  await expect(page).toHaveURL(/\/mappings\/dbo\.Lines\/pipeline$/)
  await expect(page.getByTestId('mapping-natural-key-problem')).toContainText('has no primary key')
  await page.getByTestId('mapping-item-dbo.Customers').click()
  await page.getByTestId('mapping-tab-pipeline').click()
  await expect(page.getByTestId('mapping-natural-key-derived')).toHaveText('id')

  // on dbo.Lines: a row-count verification, and the reload segmented by month through the replication's strategy
  await page.getByTestId('mapping-item-dbo.Lines').click()
  await page.getByTestId('mapping-tab-verify').click()
  await page.getByTestId('add-check-button').click()
  await page.getByTestId('check-name-input').fill('rows')
  await page.getByTestId('save-check-button').click()
  await expect(page.getByTestId('check-rows')).toHaveAttribute('data-kind', 'RowCount')
  await page.getByTestId('mapping-tab-segmenting').click()
  await page.getByTestId('default-segmenting-mode').selectOption('custom')
  await expect(page.getByTestId('default-segmenting-strategy')).toHaveValue('by-month')
  await page.getByTestId('default-segmenting-custom-column').fill('shipped_on')
  await shot(page, '09-mapping-segmenting')

  // saved, the replication's plan has the mapping: its target table is missing, so the Provisioning tab's badge counts the one step
  await expect(page.getByTestId('mapping-tab-provisioning-badge')).toHaveCount(0)
  await page.getByTestId('save-mapping-button').click()
  await expect(page.getByTestId('mapping-dirty')).toHaveCount(0)
  await expect(page.getByTestId('mapping-tab-provisioning-badge')).toHaveText('1')
  await page.getByTestId('mapping-tab-provisioning').click()
  await expect(page.getByTestId('provisioning-plan-target')).toHaveAttribute('data-state', 'missing')
  await expect(page.getByTestId('provisioning-plan-target-sql')).toContainText('CREATE TABLE dbo.Lines')
  const lines = saved(workspace, 'mapping-rest', mappingFile('dbo.Lines'))
  expect(lines.verification).toEqual([expect.objectContaining({ name: 'rows', kind: 'RowCount' })])
  expect(lines.defaultSegmenting).toEqual([{ mode: 'custom', strategyName: 'by-month', column: 'shipped_on' }])
  expect(lines.columnMappings.some((c: Record<string, unknown>) => 'targetType' in c)).toBe(false)
  expect(lines.primaryKey).toBeUndefined()
  expect(JSON.stringify(lines)).not.toContain('naturalKey')

  // the replication's Provisioning tab names the mapping, and the name opens the mapping's own Provisioning tab
  await page.getByTestId('tab-overview').click()
  await page.getByTestId('overview-tab-provisioning').click()
  await page.getByTestId('provisioning-mapping-link-dbo.Lines').first().click()
  await expect(page).toHaveURL(/\/mappings\/dbo\.Lines\/provisioning$/)
  await expect(page.getByTestId('mapping-editor')).toHaveAttribute('data-name', 'dbo.Lines')

  // an unsaved edit (Diagnostics) shows in Files, and is still there back in the Designer
  await page.getByTestId('mapping-tab-diagnostics').click()
  await page.getByTestId('trace-timing-toggle').click()
  await page.locator(`[data-testid="wsp-file"][data-path="${mappingFile('dbo.Lines')}"]`).click()
  await expect(area(page)).toHaveAttribute('data-area', 'files')
  await expect(page.getByTestId('wsp-dirty')).toBeVisible()
  await page.getByTestId('wsp-mode-yaml').click()
  // the editor draws only the lines in view, and the mapping's settings are at the end of a file longer than the editor: go there
  await page.getByTestId('wsp-code').locator('.monaco-editor').click()
  await page.keyboard.press('Control+End')
  await expect(page.getByTestId('wsp-code')).toContainText('traceTiming: true')
  await expect(page.getByTestId('wsp-code')).toContainText('strategyName: by-month')
  await expect(page.getByTestId('wsp-code')).toContainText('kind: RowCount')
  await page.locator('[data-testid="sd-nav-replication"][data-name="orders"]').click()
  await expect(area(page)).toHaveAttribute('data-area', 'designer')
  await page.getByTestId('tab-mappings').click()
  await page.getByTestId('mapping-item-dbo.Lines').click()
  await page.getByTestId('mapping-tab-diagnostics').click()
  await expect(page.getByTestId('trace-timing-toggle')).toHaveAttribute('aria-pressed', 'true')
  await expect(page.getByTestId('mapping-dirty')).toBeVisible()
})

const CATALOG_RUNNER = 'runner-eu-1'
const READ_AT = '2026-11-23T09:00:00Z'

/** A `catalog-cache/2` file of one connection as the console's read answers it, recorded as read from `runners-eu` in the channel asked. */
function readAnswer(connection: string, engine: string, channel: string, databases: string[], tables: string[]) {
  return [
    'schema: catalog-cache/2', `generated_at: '${READ_AT}'`, 'connections:', `  '${connection}':`, `    engine: '${engine}'`,
    `    read: {server: 'runners-eu', product: 'runner', channel: '${channel}', at: '${READ_AT}'}`, '    databases:', ...databases, '    tables:', ...tables, '',
  ].join('\n')
}
const tableLines = (database: string, name: string, columns: string[]) => [
  `      - database: '${database}'`, "        schema: 'dbo'", `        name: '${name}'`, '        kind: table', '        columns:', ...columns.map((c) => `          - ${c}`),
]
const ID = "{name: 'id', type: 'int', nullable: false, key: true}"
const INVOICE_COLUMNS = [ID, "{name: 'customer_id', type: 'int', nullable: false}", "{name: 'total', type: 'decimal(12,2)', nullable: true}"]

/**
 * The console's catalog read (`/api/workspace-catalog/...`), faked in the page: the channels of the project's package on one Runner, and a read that
 * answers at once. `target-db` has `dw` with `dbo.Orders`; `source-db` has gained `dbo.Payments`, and `dbo.Invoices` the column `segment`. The real
 * route is tested with real servers (`CatalogReadFromRunnerTests`); the follow of a slow read is `workspace-catalog-refresh.spec.ts`'s.
 */
async function mockCatalogRead(page: Page) {
  const reads: Record<string, unknown>[] = []
  const json = (route: Route, body: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })
  await page.route('**/api/instances', (route) => route.request().method() === 'GET'
    ? json(route, [
      { id: SERVER, name: 'Workspace server', url: 'https://workspace.invalid', product: 'dbdataworkspace', version: '1', apiVersion: 1, reachable: true, ready: true, problem: null, pairedAt: null },
      { id: CATALOG_RUNNER, name: 'runners-eu', url: 'https://runner.invalid', product: 'dbdatarunner', version: '1', apiVersion: 1, reachable: true, ready: true, problem: null, pairedAt: null },
    ])
    : route.fallback())
  await page.route('**/api/workspace-catalog/**', async (route) => {
    const url = new URL(route.request().url())
    if (url.pathname.endsWith('/channels')) {
      const connection = url.searchParams.get('connection')!
      return json(route, {
        runner: CATALOG_RUNNER, runner_name: 'runners-eu', connection, package: 'plant', slot: connection.replace(/[^a-z0-9_]/g, '_'), default: 'dev',
        channels: [{ channel: 'dev', production: false, bound: true, bound_to: `${connection}-dev` }, { channel: 'prod', production: true, bound: true, bound_to: connection }],
      })
    }
    if (url.pathname.endsWith('/read') && route.request().method() === 'POST') {
      const body = route.request().postDataJSON() as { connection: string; channel: string; table?: string }
      reads.push(body)
      const cache = body.connection === 'target-db'
        ? readAnswer('target-db', 'postgres', body.channel, ["      - {name: 'dw', schemas: ['dbo']}"], tableLines('dw', 'Orders', [ID]))
        : body.table === 'Invoices'
          ? readAnswer('source-db', 'sqlserver', body.channel, ["      - {name: 'App', schemas: ['dbo']}"], tableLines('App', 'Invoices', [...INVOICE_COLUMNS, "{name: 'segment', type: 'nvarchar(20)', nullable: true}"]))
          : readAnswer('source-db', 'sqlserver', body.channel, ["      - {name: 'App', schemas: ['dbo']}", "      - {name: 'Archive', schemas: ['dbo']}"], [
            ...tableLines('App', 'Customers', [ID, "{name: 'name', type: 'nvarchar(100)', nullable: true}"]), ...tableLines('App', 'Invoices', INVOICE_COLUMNS),
            ...tableLines('App', 'Orders', INVOICE_COLUMNS), ...tableLines('App', 'Payments', [ID]),
          ])
      return json(route, { runner: CATALOG_RUNNER, runner_name: 'runners-eu', connection: body.connection, state: 'done', run_id: `run-${reads.length}`, link: `/servers/${CATALOG_RUNNER}/runs?run=run-${reads.length}`, channel: body.channel, production: false, cache })
    }
    return json(route, { error: 'There is no such call.' }, 404)
  })
  return { reads }
}

test('the Designer\'s Refresh buttons read through the console: an endpoint\'s catalog, the source\'s tables, one table on a mapping, each an unsaved edit of the cache', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Sync Refresh', `sync-refresh-${Date.now()}@example.com`)
  const workspace = await mockWorkspace(page, { syncFiles: { 'catalog-cache.yaml': CACHE } })
  const catalog = await mockCatalogRead(page)
  await makeDraft(page, 'plant', 'sync', 'refresh-edit')
  const cacheFile = page.locator('[data-testid="wsp-file"][data-path="catalog-cache.yaml"]')

  // the target connection has no cache: Refresh catalog in dev reads it through the console, shows what it found, and accepted fills the database picker
  await page.getByTestId('task-source-connection-select').selectOption('source-db')
  await page.getByTestId('task-source-database-select').selectOption('App')
  await expect(page.getByTestId('task-source-refresh-catalog')).toHaveCount(0)
  await page.getByTestId('task-target-connection-select').selectOption('target-db')
  await expect(page.getByTestId('task-target-no-cache')).toBeVisible()
  await page.getByTestId('task-target-refresh-catalog').click()
  const endpoint = page.getByTestId('task-target-refresh')
  await expect(endpoint.getByTestId('wsp-channel-picker')).toHaveAttribute('data-channel', 'dev')
  await endpoint.getByTestId('wsp-refresh-go').click()
  await expect(endpoint.getByTestId('wsp-refresh-summary')).toHaveText('1 table added')
  await shot(page, '06-endpoint-refresh')
  await endpoint.getByTestId('wsp-refresh-accept').click()
  await expect(page.locator('[data-testid="task-target-database-select"] option')).toHaveText(['Select…', 'dw'])
  await page.getByTestId('task-target-database-select').selectOption('dw')
  await expect(page.getByTestId('task-target-read')).toContainText(`read from runners-eu (runner) in dev at ${READ_AT}`)
  await expect(cacheFile).toHaveAttribute('data-dirty', 'true')
  expect(workspace.files('plant', 'refresh-edit')?.get('catalog-cache.yaml')).not.toContain('target-db')
  await page.getByTestId('save-settings-button').click()

  // Refresh on the Table Mappings overview reads the source again: one table added, listed once accepted, before anything is saved
  await page.getByTestId('tab-mappings').click()
  await expect(page.locator('[data-testid^="select-table-"]')).toHaveCount(3)
  await page.getByTestId('mappings-refresh-button').click()
  const overview = page.getByTestId('mappings-refresh')
  await overview.getByTestId('wsp-refresh-go').click()
  await expect(overview.getByTestId('wsp-refresh-summary')).toHaveText('1 table added')
  await expect(overview.locator('[data-testid="wsp-refresh-change"][data-change="table-added"]')).toContainText('App.dbo.Payments')
  await overview.getByTestId('wsp-refresh-accept').click()
  await expect(page.getByTestId('select-table-dbo.Payments')).toBeVisible()
  await expect(page.getByTestId('mappings-refresh')).toHaveCount(0)

  // on a mapping made from the cache, Refresh of its source table on the cached-metadata card: "1 column added", and the card compares against it
  await page.getByTestId('select-table-dbo.Invoices').check()
  await page.getByTestId('create-mappings-button').click()
  await page.getByTestId('mapping-item-dbo.Invoices').click()
  await expect(page.getByTestId('cached-metadata-source-outcome')).toHaveText('the same as the catalog cache')
  await page.getByTestId('cached-metadata-source-refresh-button').click()
  const card = page.getByTestId('cached-metadata-source-refresh')
  await card.getByTestId('wsp-refresh-go').click()
  await expect(card.getByTestId('wsp-refresh-summary')).toHaveText('1 column added')
  await expect(card.locator('[data-testid="wsp-refresh-change"][data-change="column-added"]')).toContainText('App.dbo.Invoices.segment')
  await card.getByTestId('wsp-refresh-accept').click()
  await expect(page.getByTestId('cached-metadata-source-outcome')).toHaveText('the catalog cache has 1 column added (segment)')
  await shot(page, '07-cached-metadata-refresh')

  // Files shows the new cache as an unsaved edit; saved, it holds all three reads
  expect(catalog.reads).toEqual([
    { runner: CATALOG_RUNNER, channel: 'dev', connection: 'target-db' },
    { runner: CATALOG_RUNNER, channel: 'dev', connection: 'source-db' },
    { runner: CATALOG_RUNNER, channel: 'dev', connection: 'source-db', schema: 'dbo', table: 'Invoices' },
  ])
  await cacheFile.click()
  await expect(area(page)).toHaveAttribute('data-area', 'files')
  await expect(page.getByTestId('wsp-dirty')).toBeVisible()
  await expect(cacheFile).toHaveAttribute('data-dirty', 'true')
  await page.getByTestId('wsp-save-all').click()
  await expect(page.getByTestId('wsp-unsaved')).toHaveCount(0)
  const cache = saved(workspace, 'refresh-edit', 'catalog-cache.yaml')
  expect(cache.connections['target-db'].read).toEqual({ server: 'runners-eu', product: 'runner', channel: 'dev', at: READ_AT })
  expect(cache.connections['source-db'].tables.map((t: { name: string }) => t.name).sort()).toEqual(['Customers', 'Invoices', 'Orders', 'Payments'])
  expect(cache.connections['source-db'].tables.find((t: { name: string }) => t.name === 'Invoices').columns.map((c: { name: string }) => c.name)).toContain('segment')
})

const DEV_PASSWORD = 'dev-login-5521-long'

/**
 * The console's live reads of a draft's connection, faked in the page (the real routes are tested with real servers, `CatalogReadFromRunnerTests` and the
 * console's tests of `describe`, `binding-test` and `workspace-dev-logins`): on the Runner `plant`, dev binds the slot of `source-db` and not that of
 * `target-db`; the binding test passes each step; the description of a query answers its columns and no row; a development login sent with the draft
 * is stored and binds the slot in its channel.
 */
async function mockLiveReads(page: Page) {
  const asked: { path: string; body: Record<string, unknown> | null }[] = []
  const json = (route: Route, body: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })
  const base = { package: 'plant', runner: RUNNER, runner_name: RUNNER }
  const setUp = new Set<string>()
  await page.route('**/api/workspace-dev-logins/**', (route) => {
    const body = route.request().postDataJSON() as { channel: string; connection: string; user?: string; draft?: string }
    asked.push({ path: new URL(route.request().url()).pathname, body })
    const slot = body.connection.replace(/[^a-z0-9_]/g, '_')
    // with the draft, the console copies its connection to the Runner and binds the slot in the channel
    if (body.draft) setUp.add(body.connection)
    return json(route, { ...base, connection: body.connection, slot, channel: body.channel, production: false, ...(body.user ? { user: body.user } : {}),
      secrets: { password: `dbdatakit:runner:dev-login:plant.${body.channel}.${slot}:password` }, bound: !!body.draft, copy: `plant-${slot}-${body.channel}`, steps: [] })
  })
  await page.route('**/api/workspace-catalog/**', async (route) => {
    const url = new URL(route.request().url())
    const body = route.request().method() === 'POST' ? route.request().postDataJSON() as Record<string, unknown> : null
    asked.push({ path: url.pathname, body })
    if (url.pathname.endsWith('/channels')) {
      const connection = url.searchParams.get('connection')!
      const slot = connection.replace(/[^a-z0-9_]/g, '_')
      const devBound = connection === 'source-db' || setUp.has(connection)
      return json(route, { ...base, connection, slot, default: devBound ? 'dev' : null,
        channels: [{ channel: 'dev', production: false, bound: devBound, bound_to: devBound ? (setUp.has(connection) ? `plant-${slot}-dev` : `${connection}-dev`) : null }, { channel: 'prod', production: true, bound: true, bound_to: connection }] })
    }
    if (url.pathname.endsWith('/binding-test')) {
      const connection = String(body!.connection)
      return json(route, { ...base, connection, slot: connection.replace(/[^a-z0-9_]/g, '_'), channel: body!.channel, production: false, role: 'default', ok: true, step: 'connect',
        connected: true, bound_to: `${connection}-dev`, message: `The slot '${connection.replace(/[^a-z0-9_]/g, '_')}' opened a connection through '${connection}-dev'.`,
        steps: ['binding', 'connection', 'login', 'connect'].map((step) => ({ step, state: 'passed' })) })
    }
    if (url.pathname.endsWith('/describe')) {
      return json(route, { ...base, state: 'done', run_id: 'run-describe-1', link: `/servers/${RUNNER}/runs?run=run-describe-1`, channel: body!.channel, production: false, connection: body!.connection,
        engine: 'sqlserver', strategy: 'SqlServerDescribeFirstResultSet', columns: [{ name: 'id', type: 'int', nullable: false }, { name: 'total', type: 'decimal(12,2)', nullable: true }] })
    }
    return json(route, { error: 'There is no such call.' }, 404)
  })
  return { asked }
}

test('the live pieces of a replication: Test on an endpoint card, Set up for development where dev does not bind the slot, which binds it, Describe columns of a query, the newest preflight on the Provisioning sub-tabs', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Sync Live', `sync-live-${Date.now()}@example.com`)
  const workspace = await mockWorkspace(page, { syncFiles: { 'catalog-cache.yaml': CACHE }, syncTasks: ['orders', 'provision', 'preflight'] })
  const live = await mockLiveReads(page)
  await makeDraft(page, 'plant', 'sync', 'live-edit')
  await expect(page).toHaveURL(/\/designer\/replications\/orders$/)

  // Test on the source's card: dev binds its slot, and the Runner's binding test shows each step and no row
  await page.getByTestId('task-source-connection-select').selectOption('source-db')
  await page.getByTestId('task-source-test-button').click()
  const sourceTest = page.getByTestId('task-source-test')
  await expect(sourceTest.getByTestId('wsp-channel-picker')).toHaveAttribute('data-channel', 'dev')
  await sourceTest.getByTestId('wsp-test-go').click()
  await expect(sourceTest.getByTestId('wsp-test-result')).toHaveAttribute('data-ok', 'true')
  await expect(sourceTest.getByTestId('wsp-test-message')).toHaveText("The slot 'source_db' opened a connection through 'source-db-dev'.")
  await expect(sourceTest.locator('[data-testid="wsp-test-step"][data-state="passed"]')).toHaveCount(4)
  await expect(sourceTest.locator('table')).toHaveCount(0)
  expect(live.asked.find((a) => a.path.endsWith('/binding-test'))?.body).toEqual({ runner: RUNNER, channel: 'dev', connection: 'source-db', role: 'default' })
  await shot(page, '40-endpoint-test')

  // Test on the target's card: dev does not bind its slot, so the picker offers Set up for development and no test; the login goes to the console only
  await page.getByTestId('task-target-connection-select').selectOption('target-db')
  await page.getByTestId('task-target-test-button').click()
  const targetTest = page.getByTestId('task-target-test')
  await expect(targetTest.getByTestId('wsp-test-go')).toHaveCount(0)
  await expect(targetTest.getByTestId('task-target-test-pick')).toBeVisible()
  await targetTest.getByTestId('wsp-setup-open').click()
  await targetTest.getByTestId('wsp-setup-user').fill('dw_dev')
  await targetTest.getByTestId('wsp-setup-password').fill(DEV_PASSWORD)
  await targetTest.getByTestId('wsp-setup-save').click()
  // the console bound the slot in dev: the picker offers dev, Set up is no longer offered, and Test can run there
  await expect(targetTest.getByTestId('wsp-channel-picker')).toHaveAttribute('data-channel', 'dev')
  await expect(targetTest.getByTestId('wsp-setup')).toHaveCount(0)
  await expect(targetTest.getByTestId('wsp-test-go')).toBeVisible()
  const logins = live.asked.filter((a) => JSON.stringify(a.body ?? {}).includes(DEV_PASSWORD))
  expect(logins.map((a) => a.path)).toEqual([`/api/workspace-dev-logins/${SERVER}/plant`])
  expect(logins[0]!.body!.draft).toMatch(/live-edit$/)
  expect(await page.content()).not.toContain(DEV_PASSWORD)
  await shot(page, '41-set-up-for-development')
  await page.getByTestId('save-settings-button').click()

  // Describe columns of a query source: the columns are written into the mapping's column cache as an unsaved edit, saved with the mapping
  await page.getByTestId('tab-mappings').click()
  await page.getByTestId('mapping-item-orders').click()
  await page.getByTestId('source-query-source-toggle').click()
  await page.getByTestId('source-query-open-button').click()
  await page.getByTestId('source-query-editor').locator('.monaco-editor').click()
  await page.keyboard.insertText('SELECT id, total FROM dbo.Orders')
  await page.getByTestId('source-query-close-button').click()
  await expect(page.getByTestId('query-columns-card')).toHaveAttribute('data-count', '0')
  await page.getByTestId('query-columns-describe-button').click()
  const describe = page.getByTestId('query-columns-describe')
  await expect(describe.getByTestId('wsp-channel-picker')).toHaveAttribute('data-channel', 'dev')
  await describe.getByTestId('wsp-describe-go').click()
  await expect(describe.getByTestId('wsp-describe-summary')).toHaveText('2 columns: id int, total decimal(12,2) null')
  await describe.getByTestId('wsp-describe-use').click()
  await expect(page.getByTestId('query-columns-card')).toHaveAttribute('data-count', '2')
  await expect(page.getByTestId('query-columns-line')).toHaveText('2 columns recorded: id, total')
  await expect(page.getByTestId('mapping-dirty')).toBeVisible()
  expect(live.asked.find((a) => a.path.endsWith('/describe'))?.body).toEqual({ runner: RUNNER, channel: 'dev', connection: 'source-db', sql: 'SELECT id, total FROM dbo.Orders' })
  await shot(page, '42-describe-columns')
  await page.getByTestId('save-mapping-button').click()
  await expect(page.getByTestId('mapping-dirty')).toHaveCount(0)
  const mapping = saved(workspace, 'live-edit', 'config/replications/orders/table-mappings/orders.yaml')
  expect(mapping.sources[0].query).toBe('SELECT id, total FROM dbo.Orders')
  expect(mapping.sourceColumns).toEqual([
    { name: 'id', nativeType: 'int', isNullable: false, isPrimaryKey: false, isIdentity: false },
    { name: 'total', nativeType: 'decimal(12,2)', isNullable: true, isPrimaryKey: false, isIdentity: false },
  ])

  // before anything is published, the Provisioning sub-tab says no channel has a preflight
  await page.locator('[data-testid="sd-nav-replication"][data-name="orders"]').click()
  await page.getByTestId('overview-tab-provisioning').click()
  await expect(page.getByTestId('newest-preflight-unpublished')).toBeVisible()

  // built and staged on dev, then Re-check on the Runs tab runs the set's preflight there
  await page.getByTestId('sd-rail').getByTestId('sd-build-and-publish').click()
  await page.getByTestId('wsp-build').click()
  const built = page.locator('[data-testid="wsp-build-detail"][data-status="succeeded"]')
  await expect(built).toBeVisible({ timeout: 15_000 })
  await built.getByTestId('wsp-publish-target').selectOption(RUNNER)
  await built.getByTestId('wsp-publish-channel').fill('dev')
  await built.getByTestId('wsp-publish').click()
  await expect(built.getByTestId('wsp-published')).toHaveAttribute('data-version', '1')
  await page.getByTestId('wsp-tab-runs').click()
  await page.getByTestId('wsp-runs-preflight-recheck').click()
  await expect(page.getByTestId('wsp-runs-result')).toHaveAttribute('data-outcome', 'succeeded')
  expect(workspace.runs.at(-1)).toMatchObject({ task: 'preflight', inputs: {} })

  // the replication's Provisioning sub-tab shows the newest preflight of dev: its time, its run's link, and what it found by mapping
  await page.locator('[data-testid="sd-nav-replication"][data-name="orders"]').click()
  await page.getByTestId('overview-tab-provisioning').click()
  const newest = page.getByTestId('newest-preflight')
  await expect(newest).toHaveAttribute('data-channel', 'dev')
  await expect(newest).toHaveAttribute('data-errors', '1')
  await expect(newest.getByTestId('newest-preflight-when')).toContainText('preflight of version 1, succeeded, finished 2026-11-11T11:00:05Z')
  await expect(newest.getByTestId('newest-preflight-link')).toHaveAttribute('href', new RegExp(`^/servers/${RUNNER}/runs\\?run=`))
  await expect(newest.locator('[data-testid="newest-preflight-part"]')).toHaveCount(2)
  await expect(newest.locator('[data-testid="newest-preflight-part"][data-part="orders/orders"] [data-testid="wsp-preflight-finding"]').first()).toHaveAttribute('data-severity', 'error')
  await shot(page, '43-newest-preflight')

  // the mapping's Provisioning tab shows its own part only
  await page.getByTestId('tab-mappings').click()
  await page.getByTestId('mapping-item-orders').click()
  await page.getByTestId('mapping-tab-provisioning').click()
  await expect(page.getByTestId('newest-preflight').locator('[data-testid="newest-preflight-part"]')).toHaveCount(1)
  await expect(page.getByTestId('newest-preflight-summary')).toHaveText('The preflight found 1 error and 0 warnings.')
  expect(workspace.workspaceRuns).toEqual([])
})

test('Build and publish… on a replication opens the draft\'s Builds tab, a build is staged on a Runner\'s dev through the console with its slot bound, and Back returns to the replication', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Sync Publish', `sync-publish-${Date.now()}@example.com`)
  const workspace = await mockWorkspace(page, { syncFiles: { 'catalog-cache.yaml': CACHE } })
  await makeDraft(page, 'plant', 'sync', 'publish-edit')
  await expect(page).toHaveURL(/\/designer\/replications\/orders$/)
  const replicationAddress = page.url()

  // the rail's card: with an unsaved edit it says a build leaves the edit out; saved, it says nothing more
  const button = page.getByTestId('sd-rail').getByTestId('sd-build-and-publish')
  await expect(button).toHaveText('Build and publish…')
  await expect(page.getByTestId('sd-publish-unsaved')).toHaveCount(0)
  await page.getByTestId('enabled-toggle').click()
  await expect(page.getByTestId('sd-publish-unsaved')).toHaveAttribute('data-count', '1')
  await expect(page.getByTestId('sd-publish-unsaved')).toHaveText('One edit of this draft is not saved, so a build leaves it out. Save first to build it.')
  await page.getByTestId('wsp-save-all').click()
  await expect(page.getByTestId('wsp-unsaved')).toHaveCount(0)
  await expect(page.getByTestId('sd-publish-unsaved')).toHaveCount(0)
  expect(saved(workspace, 'publish-edit', 'config/replications/orders/task.yaml').enabled).toBe(false)

  // Build and publish… opens the Builds tab of the same draft by its address
  await button.click()
  await expect(page).toHaveURL(/\/files\?tab=builds$/)
  await expect(area(page)).toHaveAttribute('data-area', 'files')
  await expect(page.getByTestId('wsp-builds')).toBeVisible()
  await shot(page, '20-builds-from-designer')

  // the build, then its publish panel stages it on the Runner's dev with the slot bound, through the console
  await page.getByTestId('wsp-build').click()
  const built = page.locator('[data-testid="wsp-build-detail"][data-status="succeeded"]')
  await expect(built).toBeVisible({ timeout: 15_000 })
  const build = (await built.getAttribute('data-build'))!
  await expect(page).toHaveURL(new RegExp(`/files\\?tab=builds&build=${build}$`))
  await built.getByTestId('wsp-publish-target').selectOption(RUNNER)
  await built.getByTestId('wsp-publish-channel').fill('dev')
  await built.getByTestId('wsp-publish-bind').fill('source-db=erp')
  await expect(built.getByTestId('wsp-publish-serve')).not.toBeChecked()
  await built.getByTestId('wsp-publish').click()
  await expect(built.getByTestId('wsp-published')).toHaveAttribute('data-version', '1')
  await expect(built.getByTestId('wsp-published-line')).toHaveText('Staged plant on dev of https://runner.invalid as version 1.')
  await expect(built.locator('[data-testid="wsp-deploy-record"]')).toHaveAttribute('data-outcome', 'ok')
  const draft = await page.evaluate(() => location.pathname.split('/').at(-2)!)
  expect(workspace.published).toEqual([{ project: 'plant', body: { workspaceServer: SERVER, project: 'plant', workspace: draft, build, target: RUNNER, channel: 'dev', bind: { 'source-db': 'erp' } } }])
  expect(workspace.workspacePublishes).toEqual([])
  await shot(page, '21-staged-from-designer')

  // the browser's Back returns to the replication
  await page.goBack()
  await expect(page).toHaveURL(replicationAddress)
  await expect(area(page)).toHaveAttribute('data-area', 'designer')
  await expect(page.getByTestId('sd-replication')).toHaveAttribute('data-name', 'orders')
  await expect(page.getByTestId('enabled-toggle')).toHaveAttribute('aria-pressed', 'false')
})

test('the rest of a replication: error counts in the navigation, Unpublished changes in the rail, a script bound, the plan applied on the Runner, a quality check in Files', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Sync Replication Rest', `sync-replication-rest-${Date.now()}@example.com`)
  const task = (name: string, reader: string) => `schemaVersion: 2\nname: ${name}\nscheduling: {mode: Continuous, frequencySeconds: 60}\nchangeProcessing:\n  reader: {kind: ${reader}, options: {}}\n  cache: {kind: StagingTable, options: {}}\n  writer: {kind: DeleteInsert, options: {}}\nendpoints:\n  source: {connectionName: source-db, database: App}\n  target: {connectionName: target-db}\n`
  const workspace = await mockWorkspace(page, { syncFiles: {
    'catalog-cache.yaml': CACHE,
    'config/replications/customers/task.yaml': task('customers', 'BatchReload'),
    'config/replications/invoices/task.yaml': task('invoices', 'NOT_A_READER'),
    'config/scripts/trim-names.yaml': 'name: trim-names\nkind: rowTransform\nlanguage: CSharp\ndescription: Trims the names.\n',
  } })
  await makeDraft(page, 'plant', 'sync', 'rest-edit')
  await expect(page).toHaveURL(/\/designer\/replications\/customers$/)

  // three replications in the navigation; the one whose task the draft's check refuses carries its error count
  await expect(page.locator('[data-testid="sd-nav-replication"]')).toHaveCount(3)
  await expect(page.locator('[data-testid="sd-nav-replication"][data-name="invoices"] [data-testid="sd-nav-errors"]')).toHaveAttribute('data-count', '1')
  await expect(page.locator('[data-testid="sd-nav-replication"][data-name="orders"] [data-testid="sd-nav-errors"]')).toHaveCount(0)
  await expect(page.locator('[data-testid="sd-nav-replication"][data-name="customers"] [data-testid="sd-nav-errors"]')).toHaveCount(0)
  await shot(page, '30-navigation-error-count')

  // the Pipeline sub-tab of customers: the reader from the source's driver (SQL Server), the writer from the target's (PostgreSQL)
  await page.getByTestId('overview-tab-pipeline').click()
  await expect(page).toHaveURL(/\/designer\/replications\/customers\/overview\/pipeline$/)
  await expect(page.getByTestId('reader-kind-select').locator('option')).toContainText(['MsSqlChangeTracking', 'Watermark', 'BatchReload'])
  await page.getByTestId('stage-writer').click()
  await expect(page.getByTestId('writer-kind-select').locator('option')).toContainText(['ChangeApply', 'DeleteInsert', 'Scd2'])
  await expect(page.getByTestId('reconcile-config-card')).toBeVisible()

  // a draft whose check finds an error does not build: invoices gets a reader its source offers, saved, and its error count goes
  await page.locator('[data-testid="sd-nav-replication"][data-name="invoices"]').click()
  await expect(page.getByTestId('sd-replication')).toHaveAttribute('data-name', 'invoices')
  await page.getByTestId('overview-tab-pipeline').click()
  await page.getByTestId('reader-kind-select').selectOption('BatchReload')
  await page.getByTestId('save-settings-button').click()
  await expect(page.getByTestId('sd-task-dirty')).toHaveCount(0)
  await expect(page.locator('[data-testid="sd-nav-replication"][data-name="invoices"] [data-testid="sd-nav-errors"]')).toHaveCount(0)

  // a build of the draft staged on the Runner's dev through the console, from the rail of orders
  await page.locator('[data-testid="sd-nav-replication"][data-name="orders"]').click()
  await expect(page.getByTestId('sd-replication')).toHaveAttribute('data-name', 'orders')
  await expect(page.getByTestId('unpublished-changes')).toHaveCount(0)
  await page.getByTestId('sd-rail').getByTestId('sd-build-and-publish').click()
  await page.getByTestId('wsp-build').click()
  const built = page.locator('[data-testid="wsp-build-detail"][data-status="succeeded"]')
  await expect(built).toBeVisible({ timeout: 15_000 })
  await built.getByTestId('wsp-publish-target').selectOption(RUNNER)
  await built.getByTestId('wsp-publish-channel').fill('dev')
  await built.getByTestId('wsp-publish').click()
  await expect(built.getByTestId('wsp-published')).toHaveAttribute('data-version', '1')
  await page.goBack()
  await expect(page.getByTestId('sd-replication')).toHaveAttribute('data-name', 'orders')

  // published and nothing changed since: no notice; Enabled off is an unsaved edit of this replication, and the rail says the Runner runs version 1 without it
  await expect(page.getByTestId('unpublished-changes')).toHaveCount(0)
  await page.getByTestId('enabled-toggle').click()
  await expect(page.getByTestId('sd-rail').getByTestId('unpublished-changes')).toHaveAttribute('data-unsaved-here', '1')
  await expect(page.getByTestId('unpublished-changes-text')).toContainText('Version 1 on dev of plant was built before unsaved edits of 1 file of this replication')
  await shot(page, '31-rail-unpublished')
  // another replication's page does not count an unsaved edit of orders
  await page.locator('[data-testid="sd-nav-replication"][data-name="customers"]').click()
  await expect(page.getByTestId('sd-replication')).toHaveAttribute('data-name', 'customers')
  await expect(page.getByTestId('unpublished-changes')).toHaveCount(0)
  await page.locator('[data-testid="sd-nav-replication"][data-name="orders"]').click()
  await expect(page.getByTestId('sd-replication')).toHaveAttribute('data-name', 'orders')

  // Custom Transforms offers the Workspace's slots of a replication and the draft's scripts that fill each; binding one is an unsaved edit of task.yaml
  await page.getByTestId('overview-tab-transforms').click()
  await expect(page).toHaveURL(/\/designer\/replications\/orders\/overview\/transforms$/)
  await expect(page.getByTestId('script-slot-metadataProvider')).toHaveCount(0)
  await page.getByTestId('script-binding-rowTransform').selectOption('trim-names')
  await shot(page, '32-custom-transforms')

  // saved: the binding and Enabled are in the file, the rest of it as it was
  await page.getByTestId('save-settings-button').click()
  await expect(page.getByTestId('sd-task-dirty')).toHaveCount(0)
  const orders = saved(workspace, 'rest-edit', 'config/replications/orders/task.yaml')
  expect(orders.scripts).toEqual({ rowTransform: { scriptName: 'trim-names', parameters: {} } })
  expect(orders.enabled).toBe(false)
  expect(orders.changeProcessing.reader.kind).toBe('MsSqlChangeTracking')

  // the Provisioning sub-tab: the plan in the old layout, the target table that is missing and its statement, applied on the Runner through the console
  await page.getByTestId('overview-tab-provisioning').click()
  await expect(page).toHaveURL(/\/overview\/provisioning$/)
  const group = page.locator('[data-testid="provisioning-group"][data-connection="target-db"][data-side="target"]')
  await expect(group).toBeVisible()
  await expect(group.getByTestId('provisioning-step')).toHaveAttribute('data-step-title', 'Create the table')
  await expect(group.getByTestId('provisioning-step-sql')).toHaveText('CREATE TABLE "public"."orders" ("id" bigint)')
  await expect(page.getByTestId('provisioning-staged')).toHaveText('Apply runs the task provision of plant version 1, staged on dev of plant.')
  await shot(page, '33-provisioning-plan')
  await group.locator('[data-testid="provisioning-apply"][data-mapping="orders"]').click()
  await expect(page.getByTestId('wsp-prov-run-result')).toHaveAttribute('data-outcome', 'succeeded', { timeout: 15_000 })
  await expect(page.getByTestId('wsp-prov-run-step')).toHaveText('Done: Create the table of orders')
  expect(workspace.runs.map((r) => [r.task, r.inputs.replication, r.inputs.mapping, r.inputs.action])).toEqual([['provision', 'orders', 'orders', 'createTargetTable']])
  expect(workspace.workspaceRuns).toEqual([])

  // Quality Checks: a freshness check added to the list is an unsaved edit of task.yaml that Files shows
  await page.getByTestId('tab-checks').click()
  await expect(page).toHaveURL(/\/designer\/replications\/orders\/checks$/)
  await page.getByTestId('replication-check-add').click()
  await page.getByTestId('replication-check-id-input').fill('orders_fresh')
  await page.getByTestId('replication-check-dataset-input').fill('public.orders')
  await page.getByTestId('replication-check-maxAge-input').fill('6h')
  await page.getByTestId('replication-check-apply').click()
  await expect(page.locator('[data-testid="replication-check-orders_fresh"]')).toHaveAttribute('data-kind', 'freshness')
  await expect(page.getByTestId('tab-checks-count')).toHaveText('1')
  await shot(page, '34-quality-checks')
  await page.locator('[data-testid="wsp-file"][data-path="config/replications/orders/task.yaml"]').click()
  await expect(area(page)).toHaveAttribute('data-area', 'files')
  await expect(page.getByTestId('wsp-dirty')).toBeVisible()
  await page.getByTestId('wsp-mode-yaml').click()
  await expect(page.getByTestId('wsp-code')).toContainText('orders_fresh')
  await expect(page.getByTestId('wsp-code')).toContainText('max_age: 6h')
  await page.locator('[data-testid="sd-nav-replication"][data-name="orders"]').click()
  await expect(area(page)).toHaveAttribute('data-area', 'designer')
  await page.getByTestId('wsp-save-all').click()
  await expect(page.getByTestId('wsp-unsaved')).toHaveCount(0)
  expect(saved(workspace, 'rest-edit', 'config/replications/orders/task.yaml').checks).toEqual([expect.objectContaining({ id: 'orders_fresh', kind: 'freshness', dataset: 'public.orders', max_age: '6h' })])
})
