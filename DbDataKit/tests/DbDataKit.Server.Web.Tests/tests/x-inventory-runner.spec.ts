import { expect, test, type Route } from '@playwright/test'
import { signInNewAdmin } from './people'

// Plan 28c E27 (ED17, G4): the Connections page shows, for a runner, where each connection goes (engine, host, database from the copy or the runner's own file), where the definition came from, the runner's copy against the console's file
// (current, stale, missing, runner's own) beside the processes that use it, a copy nothing is bound to as unused, and the filters "exists on" and "used by". The APIs are mocked in the page (the runner's answer is `ConnectionInventoryTests` in
// DbDataRunner.Tests, the comparison is `connectionInventory.test.ts`), so it needs the signed-in console only. Its name sorts after `first-run` and `identity-home`. Written, not run where it was made.
const json = (route: Route, body: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })

const mine = (name: string, version: string) => ({ name, kind: 'database', purpose: null, models: [], canSend: true, target: null, cloud: null, version })
const use = { process: 'task', name: 'orders/extract', version: '3', group: 'main', instance: null, channel: 'prod', role: 'run' }
const entry = (name: string, source: string, version: string | null, extra: object = {}) => ({ name, kind: 'database', engine: 'sqlserver', host: 'sql01:1433', database: 'Erp', secret_ref: 'env:ERP_PASSWORD', roles: [], used_by: [], source, version, ...extra })

test('the runner rows say where each connection goes, how the runner holds it against the console file and who uses it, and an unused copy is listed', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Inventory Runner', `inventory-runner-${Date.now()}@example.com`)
  await page.route('**/api/instances', (route) => json(route, [{ id: 'console-runner', name: 'Console runner', url: baseURL, product: 'dbdatarunner', version: '1', apiVersion: 1, reachable: true, ready: true, problem: null, pairedAt: null }]))
  await page.route('**/api/bindings', (route) => json(route, { file: 'bindings.yml', yaml: '', diagnostics: [], earlier: null, releaseChannels: ['prod'], connections: [mine('erp', 'sha256:aaaa111122223333'), mine('crm', 'sha256:cccc111122223333'), mine('wh', 'sha256:dddd111122223333')], slots: [], postureSetting: 'production_binding_on_non_production', postureEffect: '' }))
  await page.route('**/relay/console-runner/api/v1/connections', (route) => json(route, {
    product: 'dbdatarunner',
    connections: [
      entry('erp', 'copy', 'sha256:old0111122223333', { used_by: [use] }),             // the console's file changed since it was sent: stale
      entry('crm', 'copy', 'sha256:cccc111122223333', { host: 'crm01:1433', database: 'Crm', used_by: [] }),   // a copy nothing is bound to: unused, and current
      entry('wh', 'file', 'sha256:dddd111122223333', { host: 'own-host:1433', database: 'Own', used_by: [{ ...use, name: 'orders/load' }] }),   // a file of the runner's own answers first
      entry('ghost', 'unknown', null, { engine: null, host: null, database: null, secret_ref: null, used_by: [{ ...use, name: 'orders/ghost' }] }),
    ],
  }))

  await page.goto('/connections')
  const row = (name: string) => page.locator(`[data-testid="inventory-row"][data-name="${name}"]`)
  await expect(row('erp')).toBeVisible()
  await expect(row('erp').getByTestId('inventory-address')).toHaveText('sql01:1433/Erp')
  await expect(row('erp').getByTestId('inventory-copy')).toHaveAttribute('data-state', 'stale')
  await expect(row('erp').getByTestId('inventory-users')).toContainText('task orders/extract v3')
  await expect(row('crm').getByTestId('inventory-copy')).toHaveAttribute('data-state', 'current')
  await expect(row('crm').getByTestId('inventory-users')).toHaveText('Not used')
  await expect(row('wh').getByTestId('inventory-copy')).toHaveAttribute('data-state', "runner's own")
  await expect(row('wh').getByTestId('inventory-address')).toHaveText('own-host:1433/Own')
  await expect(row('ghost').getByTestId('inventory-copy')).toHaveAttribute('data-source', 'unknown')

  // "exists on" and "used by"
  await page.getByTestId('inventory-filter-server').fill('nowhere')
  await expect(page.getByTestId('inventory-none')).toBeVisible()
  await page.getByTestId('inventory-filter-server').fill('runner')
  await expect(page.getByTestId('inventory-row')).toHaveCount(4)
  await page.getByTestId('inventory-filter-user').fill('orders/load')
  await expect(page.getByTestId('inventory-row')).toHaveCount(1)
  await expect(page.getByTestId('inventory-row')).toHaveAttribute('data-name', 'wh')
})
