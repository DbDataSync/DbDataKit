import { expect, test } from '@playwright/test'
import { signInNewAdmin } from './people'
import { RUNNER, SERVER, mockWorkspace } from './workspace-fake'

// A Sync build published through the console is run through the console (docs/publishing.md, "Runs of a deployed build"): the Provisioning tab applies the setup of a
// replication's table with the set's task provision of the version the deploy staged, and the Runs tab runs a replication now and tests a connection's login, each
// on the Runner named by the console's record of the deploy. The Workspace server's own run routes are never called. The Workspace server and the console's deploy
// and runs are answered in the page (`workspace-fake.ts`), so the story needs the signed-in console only and takes no port; the real runs are ConsoleDeployTests.
// It signs in a new administrator, so its name sorts after `first-run` and `identity-home` (ci-lessons).
test.setTimeout(120_000)

test('a Sync build published through the console has its setup applied, a replication run and a connection tested, all through the console', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Workspace Runner', `workspace-runs-${Date.now()}@example.com`)
  const fake = await mockWorkspace(page)

  // a Sync project, a draft of it and a build, made on the console's screens of the Workspace server
  await page.goto(`/servers/${SERVER}`)
  await expect(page.getByTestId('ws-projects')).toBeVisible()
  await page.getByTestId('ws-new-project-open').click()
  await page.getByTestId('ws-new-project-id').fill('orders')
  await page.getByTestId('ws-new-project-kind').selectOption('sync')
  await page.getByTestId('ws-new-project-create').click()
  await page.getByTestId('ws-new-name-orders').fill('runs-edit')
  await page.getByTestId('ws-new-create-orders').click()
  const listed = page.locator('[data-testid="ws-workspace"][data-workspace$="-runs-edit"]')
  await expect(listed).toBeVisible()
  const draft = (await listed.getAttribute('data-workspace'))!
  await page.getByTestId(`ws-open-${draft}`).click()
  await expect(page.getByTestId('wsp-page')).toBeVisible()
  await page.getByTestId('wsp-nav-files').click()
  await page.getByTestId('wsp-tab-builds').click()
  await page.getByTestId('wsp-build').click()
  const built = page.locator('[data-testid="wsp-build-detail"][data-status="succeeded"]')
  await expect(built).toBeVisible({ timeout: 15_000 })
  const build = (await built.getAttribute('data-build'))!

  // published from the build's panel: the console stages it on the Runner's dev channel with the source bound
  await page.goto(`/servers/${SERVER}/orders/${draft}/files?tab=builds&build=${build}`)
  const done = page.locator(`[data-testid="wsp-build-detail"][data-build="${build}"]`)
  await done.getByTestId('wsp-publish-target').selectOption(RUNNER)
  await done.getByTestId('wsp-publish-channel').fill('dev')
  await done.getByTestId('wsp-publish-bind').fill('source_db=erp')
  await done.getByTestId('wsp-publish').click()
  await expect(done.getByTestId('wsp-published')).toHaveAttribute('data-version', '1')

  // Provisioning: the version is the console's record of the deploy; Apply runs provision on the Runner and its report comes back
  await page.getByTestId('wsp-tab-provisioning').click()
  await expect(page.getByTestId('wsp-prov-staged')).toHaveText(`Staged: orders version 1 on dev of ${RUNNER}.`)
  await page.getByTestId('wsp-prov-run').click()
  const target = page.locator('[data-testid="wsp-prov-side"][data-side="target"]')
  await expect(target).toHaveAttribute('data-state', 'missing')
  await target.getByTestId('wsp-prov-apply').click()
  const applied = page.getByTestId('wsp-prov-run-result')
  await expect(applied).toHaveAttribute('data-outcome', 'succeeded')
  await expect(applied.getByTestId('wsp-prov-run-step')).toHaveText('Done: Create the table of orders')
  expect(fake.runs.at(-1)).toMatchObject({ task: 'provision', served: false, inputs: { replication: 'orders', mapping: 'orders', action: 'createTargetTable' } })

  // Runs: the replication runs now as the channel serves it, its pass comes back, and the run links to the Runner's screens in the console
  await page.getByTestId('wsp-tab-runs').click()
  await expect(page.getByTestId('wsp-runs-version')).toContainText(`orders version 1 on dev of ${RUNNER}`)
  await expect(page.getByTestId('wsp-runs-tasks-link')).toHaveAttribute('href', `/servers/${RUNNER}/?package=orders`)
  const replication = page.locator('[data-testid="wsp-runs-replication"][data-task="orders"]')
  await replication.getByTestId('wsp-runs-run-now').click()
  const result = page.getByTestId('wsp-runs-result')
  await expect(result).toHaveAttribute('data-outcome', 'succeeded')
  await expect(result.getByTestId('wsp-runs-pass')).toHaveText('orders: Primary Succeeded, 3 read, 3 written')
  await expect(result.getByTestId('wsp-runs-run-link')).toHaveAttribute('href', new RegExp(`^/servers/${RUNNER}/runs\\?run=`))
  expect(fake.runs.at(-1)).toMatchObject({ task: 'orders', served: true, inputs: { run_all: 'true' } })

  // Test of a connection: the Runner tries the login bound to its slot, and says where it stopped
  const connection = page.locator('[data-testid="wsp-runs-connection"][data-name="source-db"]')
  await connection.getByTestId('wsp-runs-test-connection').click()
  await expect(connection.getByTestId('wsp-runs-test-result')).toHaveAttribute('data-ok', 'false')
  await expect(connection.getByTestId('wsp-runs-test-result')).toContainText(`${RUNNER} has no connection erp.`)
  expect(fake.bindingTests.map((t) => t.slot)).toEqual(['source_db'])

  // every run went to the deploy the console keeps, and none to the Workspace server's own run routes
  expect(new Set(fake.runs.map((r) => r.deploy)).size).toBe(1)
  expect(fake.workspaceRuns).toEqual([])
})
