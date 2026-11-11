import { test, expect } from '../fixtures'
import { execFileSync } from 'node:child_process'
import fs from 'node:fs'
import path from 'node:path'
import { consoleScratch } from '../console-paths'
import { screenshotDir } from '../screenshots'
import { syncDirectory, syncRunner } from '../playwright.config'

const screenshotsDir = screenshotDir('runner-publish')

// Plan 28c E08: the Runner screen against the real Sync server (through the console) and a real runner (`syncRunner` in the config, with a stand-in `dbdatasync-runtime`). A replication set of three replications (two periodic, one continuous; two connections;
// every mapping with its column cache) is put in the Sync repository, the machine's pairing with the runner is made by the runner's own command, and the story sets the runner from the screen, reads what a publish would hold, is refused by the runner for
// an unbound slot in the runner's own words, binds and stages, sees the version served, changes one mapping and publishes again (one file goes), and finally is refused by the verb itself for a mapping whose column cache is missing.
// It needs no engine: nothing here reads or writes a database. It sorts after replication-runner-owner (which also sets the link) and cleans up its replications.
const CONFIG = path.join(syncDirectory, 'config')
const REPLICATIONS = ['rp-orders', 'rp-customers', 'rp-events']
const CONNECTIONS = ['rp-crm-src', 'rp-dwh']
const PACKAGE = 'sales-set'

function write(relative: string, text: string) {
  const file = path.join(CONFIG, relative)
  fs.mkdirSync(path.dirname(file), { recursive: true })
  fs.writeFileSync(file, text)
}

const mapping = (name: string, table: string, cached: boolean) => [
  'schemaVersion: 1', `name: ${name}`, 'sources:', '- allowSubquery: true', '  connectionName: rp-crm-src', '  database: App', '  schema: dbo', `  table: ${table}`,
  'targets:', '- connectionName: rp-dwh', '  database: dw', '  schema: public', `  table: ${table.toLowerCase()}`,
  'columnMappings: []', 'relationships: []', 'scripts: {}', 'hooks: {}', 'provisioning: {}', 'verification: []', 'defaultSegmenting: []', 'primaryKey: []',
  ...(cached ? ['sourceColumns:', '- name: id', '  nativeType: bigint', '  isPrimaryKey: true', '  keyOrdinal: 1', 'targetColumns:', '- name: id', '  nativeType: bigint', '  isPrimaryKey: true', '  keyOrdinal: 1']
    : ['sourceColumns: []', 'targetColumns: []']),
  'relationshipColumns: {}', ''].join('\n')

const task = (name: string, scheduling: string[]) => [
  'schemaVersion: 2', `name: ${name}`, 'scheduling:', ...scheduling,
  'changeProcessing:', '  reader:', '    kind: MsSqlChangeTracking', '    options: {}', '  cache:', '    kind: MsSqlStagingTable', '    options: {}', '  writer:', '    kind: PostgresMerge', '    options: {}',
  'bulkLoad:', '  reader:', '    kind: BatchReload', '    options: {}', 'endpoints: {}', 'scripts: {}', 'hooks: {}', 'provisioning: {}',
  'reconcile:', '  afterChange:', '    mode: none', '  deleteGuard:', '    mode: ratio', '    maxRatio: 0.5', 'segmentingStrategies: []', ''].join('\n')

const connection = (name: string, engine: string, host: string) => [
  'schema: connection/1', `name: ${name}`, `engine: ${engine}`, 'address:', `  host: ${host}`, '  database: app', 'auth:', '  mode: sql', '  user: u', `  password_secret: secret:dbdatakit:sync:connection:${name}`, ''].join('\n')

function seed(cached = true) {
  write('connections/rp-crm-src.yaml', connection('rp-crm-src', 'sqlserver', 'src'))
  write('connections/rp-dwh.yaml', connection('rp-dwh', 'postgres', 'dw'))
  write('replications/rp-orders/task.yaml', task('rp-orders', ['  mode: Periodic', "  cronExpression: '*/15 * * * *'"]))
  write('replications/rp-customers/task.yaml', task('rp-customers', ['  mode: Periodic', "  cronExpression: '0 2 * * *'"]))
  write('replications/rp-events/task.yaml', task('rp-events', ['  mode: Continuous', '  frequencySeconds: 3600', '  idleTimeoutSeconds: 7200']))
  for (const [replication, table] of [['rp-orders', 'Orders'], ['rp-customers', 'Customers'], ['rp-events', 'Events']] as const)
    write(`replications/${replication}/table-mappings/${table.toLowerCase()}.yaml`, mapping(table.toLowerCase(), table, cached))
}

test.afterAll(() => {
  for (const name of REPLICATIONS) fs.rmSync(path.join(CONFIG, 'replications', name), { recursive: true, force: true })
  for (const name of CONNECTIONS) fs.rmSync(path.join(CONFIG, 'connections', `${name}.yaml`), { force: true })
})

test('three replications are published to a real runner: refused for an unbound slot in the runner\'s words, then staged and served, then one file goes, and a mapping with no column cache is refused in the verb\'s', async ({ page }) => {
  test.setTimeout(300_000)
  seed()
  const code = fs.readFileSync(path.join(consoleScratch, 'sync-runner-pairing-code.txt'), 'utf8').trim()
  // the Sync server's machine is paired with the runner by the runner's own command, as a publisher would
  execFileSync('dotnet', ['exec', syncRunner.dll, 'package', 'pair', syncRunner.url, '--code', code, '--name', 'story-runner', '--client-dir', syncRunner.clientDirectory], { stdio: 'ignore' })

  // ---- the runner is set from the screen (an earlier story may have left another one: the fields are overwritten)
  await page.goto('/runner')
  await expect(page.getByTestId('runner-page')).toBeVisible()
  await page.getByTestId('runner-link-server').fill('story-runner')
  await page.getByTestId('runner-link-channel').fill('prod')
  await page.getByTestId('runner-link-package').fill(PACKAGE)
  await page.getByTestId('runner-link-client-dir').fill(syncRunner.clientDirectory)
  await page.getByTestId('runner-link-save').click()

  // ---- what a publish would hold: one task per replication, its schedule on the runner, the slots it needs
  await expect(page.getByTestId('runner-package-none')).toContainText(`no package called ${PACKAGE}`, { timeout: 30_000 })
  const table = page.getByTestId('runner-check-table')
  await expect(table).toBeVisible({ timeout: 60_000 })
  await expect(page.getByTestId('runner-check-row-rp-orders')).toContainText('periodic, */15 * * * *')
  await expect(page.getByTestId('runner-check-row-rp-customers')).toContainText('periodic, 0 2 * * *')
  await expect(page.getByTestId('runner-check-row-rp-events')).toContainText('continuous, every 3600 s')
  await expect(page.getByTestId('runner-check-row-rp-orders')).toContainText('rp_crm_src, rp_dwh')
  await expect(page.getByTestId('runner-slot-input-rp_crm_src')).toHaveValue('rp-crm-src')     // the Sync connection's own name: the runner's connection bound to the slot carries it
  await page.screenshot({ path: path.join(screenshotsDir, '01-check.png'), fullPage: true })

  // ---- nothing is bound on the runner: it refuses the package, naming the slot and the channel
  await page.getByTestId('runner-slot-input-rp_crm_src').fill('')
  await page.getByTestId('runner-slot-input-rp_dwh').fill('')
  await page.getByTestId('runner-publish-stage').click()
  await expect(page.getByTestId('runner-publish-refused')).toContainText("needs the slots 'rp_crm_src', 'rp_dwh' bound in channel 'prod'", { timeout: 120_000 })
  await expect(page.getByTestId('runner-publish-done')).toHaveCount(0)
  await page.screenshot({ path: path.join(screenshotsDir, '02-refused.png'), fullPage: true })

  // ---- bind and stage: the version is staged, the runner prepares it and serves it
  await page.getByTestId('runner-slot-input-rp_crm_src').fill('rp-crm-src')
  await page.getByTestId('runner-slot-input-rp_dwh').fill('rp-dwh')
  await page.getByTestId('runner-publish-stage').click()
  const done = page.getByTestId('runner-publish-done')
  await expect(done).toContainText(`Staged ${PACKAGE} on prod: version`, { timeout: 120_000 })
  const first = Number(/version (\d+)/.exec((await done.textContent())!)![1])
  await expect(page.getByTestId('runner-package-active')).toContainText(String(first), { timeout: 120_000 })
  await expect(page.getByTestId('runner-package-state')).toHaveAttribute('data-state', 'serving')
  await expect(page.getByTestId('runner-slots-input-rp_dwh')).toHaveValue('rp-dwh')
  await expect(page.getByTestId('runner-slots-state')).toHaveText('all bound')
  await page.screenshot({ path: path.join(screenshotsDir, '03-served.png'), fullPage: true })

  // ---- the same set again changes nothing; one changed mapping sends one file
  await page.getByTestId('runner-publish-stage').click()
  await expect(page.getByTestId('runner-publish-done')).toContainText('the same content as before, nothing changed', { timeout: 120_000 })
  write('replications/rp-orders/table-mappings/orders.yaml', mapping('orders', 'Orders', true).replace('keyOrdinal: 1', 'keyOrdinal: 1\n- name: amount\n  nativeType: decimal(14,2)'))
  await page.getByTestId('runner-check').click()
  await page.getByTestId('runner-publish-stage').click()
  await expect(done).toContainText(`version ${first + 1}`, { timeout: 120_000 })
  await expect(done).toContainText('1 file(s) sent')

  // ---- a mapping with no column cache is refused by the verb before anything is sent, naming the mapping and its file
  write('replications/rp-customers/table-mappings/customers.yaml', mapping('customers', 'Customers', false))
  await page.getByTestId('runner-check').click()
  await expect(page.getByTestId('runner-check-refused')).toContainText("Table mapping 'customers' has no cached source column metadata", { timeout: 60_000 })
  await expect(page.getByTestId('runner-check-refused')).toContainText('customers.yaml')
  await expect(page.getByTestId('runner-publish-stage')).toBeDisabled()
  await page.screenshot({ path: path.join(screenshotsDir, '04-no-cache.png'), fullPage: true })
})
