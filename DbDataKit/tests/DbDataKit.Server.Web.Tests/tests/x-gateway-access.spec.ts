import { expect, test, type Route } from '@playwright/test'
import { signInNewAdmin } from './people'

// Plan 22a GP15: the Gateway's Access rules screen against a Gateway whose relay is mocked in the page (the Gateway side, with the real resolver, the predicate and the engines, is DbDataGateway.Tests' AccessExplainTests). The story reads the rules and the
// state of their rules tables, sees the warning that row rules limit reads only on a Store model (the owner requires it to be visible, QR6), asks why a person cannot see a row, adds a row to the rules table's source and refreshes it (the mock's
// two states of the table), asks again and sees the answer change, checks the coverage (a value no row covers and a NULL), and rotates the key for hash masks with a second click. It signs in a new administrator, so its name sorts after
// `first-run` and `identity-home` (which spend the first-run invitation).
const json = (route: Route, body: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })
const SERVER = 'gw-1'

const column = { dataset_column: 'status', rules_column: 'status_code' }
const table = (name: string, state: string, age: number, changed: string | null) => ({ table: name, key: `warehouse/${name}`, state, age_seconds: age, max_age_seconds: 3600, counted_from: '2026-11-12T09:00:00Z', last_changed: changed })

test('the access rules screen shows the rules and their tables, the reads-only warning, why a row is or is not seen, the coverage and the rotation of the mask key', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Access Tester', `access-rules-${Date.now()}@example.com`)
  // the rules table: east is told about Completed; a person adds Pending to its source and the producer refreshes it
  let rows = ['Completed']
  let refreshed: string | null = '2026-11-12T09:50:00Z'
  let keyExists = false
  const explains: URLSearchParams[] = []
  const rotations: unknown[] = []
  const rules = () => ({
    mask_key: { exists: keyExists },
    definitions: [
      {
        reference: 'semantic-model/orders', name: 'orders', kind: 'semantic-model', version: 3, has_row_rules: true, has_column_rules: true, max_age_seconds: 3600,
        row: [{ dataset: 'fact_sales', match: 'all', groups: [{ id: 'visible', match: 'all', rules: [
          { id: 'statuses', effect: 'include', table: 'status_access', subject: 'who', subject_kind: 'group', columns: [column], max_age_seconds: null },
          { id: 'no_blocked', effect: 'exclude', table: 'blocked_status', subject: 'who', subject_kind: 'group', columns: [column], max_age_seconds: null },
        ] }] }],
        columns: [{ subject: 'support', subject_kind: 'group', dataset: 'customers', column: 'email', treatment: 'mask', mask: 'hash' }],
        tables: [table('status_access', 'fresh', 600, refreshed), table('blocked_status', 'expired', 7200, null)],
        warnings: ["The rules table 'status_access' is not known to be built by a published Build model: the group holds no build-metadata to check it against (plan 27a KN13)."],
        limits_reads_only: false,
      },
      {
        reference: 'entity-model/sales', name: 'sales', kind: 'entity-model', version: 1, has_row_rules: true, has_column_rules: false, max_age_seconds: 3600, limits_reads_only: true,
        row: [{ dataset: 'territory', match: 'all', groups: [{ id: 'by_region', match: 'all', rules: [{ id: 'region', effect: 'include', table: 'rules.region_access', subject: 'who', subject_kind: 'group', columns: [{ dataset_column: 'region', rules_column: 'region_code' }], max_age_seconds: null }] }] }],
        columns: [], tables: [table('rules.region_access', 'stale', 2400, '2026-11-12T09:20:00Z')],
        warnings: ["Row rules limit reads only, not writes: a person may still change a row they cannot read through the Store's own roles and approvals."],
      },
    ],
  })
  // a toy of the Gateway's explanation, for the one row of the story (what is shown is the screen; the real explanation is tested on the Gateway)
  const explanation = (status: string) => {
    const covered = rows.includes(status)
    return {
      definition: 'semantic-model/orders', subject: 'Ann', subject_kind: 'person', names: ['Ann', 'east'], fingerprint: covered ? 'f1' : 'f0',
      datasets: [{
        dataset: 'fact_sales', match: 'all', refusal: null, missing_columns: [],
        summary: `They read the rows of 'fact_sales' that the rule rows select: 'statuses' (${rows.length} ${rows.length === 1 ? 'row' : 'rows'} of 'status_access').`,
        verdict: { included: covered, decided_by: covered ? ['visible/statuses'] : [], row: { status }, text: covered ? `The row is included: rule 'visible/statuses' holds ('status_access' has a row for 'east' with status_code = '${status}').` : `The row is not included: no include rule holds for it. rule 'visible/statuses': none of the ${rows.length} ${rows.length === 1 ? 'row' : 'rows'} of 'status_access' that name them has the values of the row (status = '${status}').` },
        groups: [{ id: 'visible', match: 'all', included: covered, rules: [{
          id: 'statuses', effect: 'include', table: 'status_access', subject: 'who', subject_kind: 'group', columns: [column], names: ['east'], rows: rows.map((r) => ({ subject: 'east', values: { status_code: r } })), rows_truncated: false, holds: covered, read_problem: null,
        }] }],
      }],
      columns: [], tables: [table('status_access', 'fresh', 600, refreshed), table('blocked_status', 'expired', 7200, null)], warnings: [],
    }
  }
  await page.route('**/api/instances', (route) => route.request().method() === 'GET'
    ? json(route, [{ id: SERVER, name: 'Access gateway', url: 'https://gateway.invalid', product: 'dbdatagateway', version: '1', apiVersion: 1, reachable: true, ready: true, problem: null, pairedAt: null }])
    : route.fallback())
  await page.route(`**/relay/${SERVER}/api/v1/access/rules`, (route) => json(route, rules()))
  await page.route(`**/relay/${SERVER}/api/v1/access/explain*`, (route) => {
    const query = new URL(route.request().url()).searchParams
    explains.push(query)
    return json(route, explanation(query.getAll('row').map((r) => r.split('=')[1])[0]))
  })
  await page.route(`**/relay/${SERVER}/api/v1/access/coverage*`, (route) => json(route, {
    definition: 'semantic-model/orders',
    rules: [
      { dataset: 'fact_sales', group: 'visible', rule: 'statuses', effect: 'include', table: 'status_access', columns: [column], uncovered: rows.includes('Pending') ? 1 : 2,
        values: [...(rows.includes('Pending') ? [] : [{ values: { status: 'Pending' }, rows: 4 }]), { values: { status: null }, rows: 1 }], truncated: false, problem: null },
      { dataset: 'fact_sales', group: 'visible', rule: 'no_blocked', effect: 'exclude', table: 'blocked_status', columns: [column], uncovered: 0, values: [], truncated: false, problem: null },
    ],
  }))
  await page.route(`**/relay/${SERVER}/api/v1/access/mask-key/rotate`, (route) => { rotations.push(route.request().postData()); keyExists = true; return json(route, { rotated: true, cleared: 3 }) })

  await page.goto(`/servers/${SERVER}/access`)
  await expect(page.getByTestId('gateway-access')).toBeVisible()
  await expect(page.getByTestId('rail-access')).toBeVisible()

  // the rules, each rules table with its state and age, and the warnings
  await expect(page.getByTestId('access-definition-orders')).toBeVisible()
  await expect(page.getByTestId('access-rule-orders-fact_sales-statuses')).toContainText('Include the rows whose status = status_code appear in status_access')
  await expect(page.getByTestId('access-rule-orders-fact_sales-no_blocked')).toHaveAttribute('data-effect', 'exclude')
  await expect(page.getByTestId('access-column-rules-orders')).toContainText('Group support: mask (hash)')
  await expect(page.getByTestId('access-table-state-orders-status_access')).toHaveAttribute('data-state', 'fresh')
  await expect(page.getByTestId('access-table-state-orders-blocked_status')).toHaveAttribute('data-state', 'expired')
  await expect(page.getByTestId('access-table-orders-blocked_status')).toContainText('no refresh reported yet')
  await expect(page.getByTestId('access-table-state-sales-rules.region_access')).toHaveAttribute('data-state', 'stale')
  // the owner requires this one to be visible: the Store model's row rules limit reads only
  await expect(page.getByTestId('access-reads-only')).toHaveCount(1)
  await expect(page.getByTestId('access-reads-only')).toContainText('Row rules limit reads only, not writes')
  await expect(page.getByTestId('access-definition-sales').getByTestId('access-reads-only')).toBeVisible()
  await expect(page.getByTestId('access-definition-orders').getByTestId('access-reads-only')).toHaveCount(0)

  // why a person cannot see a row: Ann is in east, and the rules table has no row for Pending
  await page.getByTestId('access-open-orders').click()
  await page.getByTestId('access-subject').fill('Ann')
  await page.getByTestId('access-dataset').selectOption('fact_sales')
  await page.getByTestId('access-row-column-0').fill('status')
  await page.getByTestId('access-row-value-0').fill('Pending')
  await page.getByTestId('access-explain-run').click()
  await expect(page.getByTestId('access-explain-result')).toBeVisible()
  await expect(page.getByTestId('access-verdict-fact_sales')).toHaveAttribute('data-included', 'false')
  await expect(page.getByTestId('access-verdict-fact_sales')).toContainText("none of the 1 row of 'status_access'")
  await expect(page.getByTestId('access-explained-rows-fact_sales-visible-statuses')).toContainText('status_code = Completed')
  expect(explains[0].get('subject')).toBe('Ann')
  expect(explains[0].get('kind')).toBe('person')
  expect(explains[0].get('model')).toBe('semantic-model/orders')
  expect(explains[0].get('dataset')).toBe('fact_sales')
  expect(explains[0].getAll('row')).toEqual(['status=Pending'])

  // a person adds the row to the rules table's source and the producer refreshes it: the same question now has another answer
  rows = ['Completed', 'Pending']
  refreshed = '2026-11-12T10:05:00Z'
  await page.getByTestId('access-explain-run').click()
  await expect(page.getByTestId('access-verdict-fact_sales')).toHaveAttribute('data-included', 'true')
  await expect(page.getByTestId('access-verdict-fact_sales')).toHaveAttribute('data-decided-by', 'visible/statuses')
  await expect(page.getByTestId('access-explained-rows-fact_sales-visible-statuses')).toContainText('status_code = Pending')

  // the coverage: a value no row covers, and a NULL
  rows = ['Completed']
  await page.getByTestId('access-coverage-run').click()
  await expect(page.getByTestId('access-coverage-result')).toHaveAttribute('data-uncovered', '2')
  await expect(page.getByTestId('access-coverage-rule-fact_sales-statuses')).toContainText('2 combinations of values have no row in status_access')
  await expect(page.getByTestId('access-coverage-value-fact_sales-statuses-0')).toContainText('Pending')
  await expect(page.getByTestId('access-coverage-value-fact_sales-statuses-1')).toContainText('NULL')
  await expect(page.getByTestId('access-coverage-rule-fact_sales-no_blocked')).toContainText('Every value is covered')

  // the key for hash masks: not made yet; a rotation waits for a second click and says what it cleared
  await expect(page.getByTestId('access-mask-key')).toHaveAttribute('data-exists', 'false')
  await page.getByTestId('access-mask-key-rotate').click()
  await expect(page.getByTestId('access-mask-key-rotate')).toHaveText('Click again to rotate the key')
  expect(rotations).toHaveLength(0)
  await page.getByTestId('access-mask-key-rotate').click()
  await expect(page.getByTestId('access-mask-key-rotated')).toContainText('3 cache entries were cleared')
  expect(rotations).toHaveLength(1)
  await expect(page.getByTestId('access-mask-key')).toHaveAttribute('data-exists', 'true')
})
