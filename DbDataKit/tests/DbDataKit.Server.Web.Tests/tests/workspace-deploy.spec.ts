import { expect, test } from '@playwright/test'
import { signInNewAdmin } from './people'
import { RUNNER, SERVER, mockWorkspace } from './workspace-fake'

// A Sync project's build published from the Builds tab goes through the console's deploy (docs/publishing.md, "Deploying a build from the control service"): the
// panel offers the Runners the console manages, stages the build on a Runner's `dev` channel with its slot bound, and shows the version the Runner answered and the
// console's record of the deploy. The panel opens by its address (`?tab=builds&build=<id>`), which is how a link from the Designer reaches it. The Workspace server
// and the console's deploy are answered in the page (`workspace-fake.ts`), so the story needs the signed-in console only and takes no port; the real deploy is
// ConsoleDeployTests. It signs in a new administrator, so its name sorts after `first-run` and `identity-home` (ci-lessons).
test.setTimeout(120_000)

test('a Sync build is staged on a Runner\'s dev channel with its slot bound, through the console, and the Runner\'s version is shown', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Workspace Deployer', `workspace-deploy-${Date.now()}@example.com`)
  const fake = await mockWorkspace(page)
  const sentToWorkspace: string[] = []
  page.on('request', (request) => { if (/\/relay\/[^/]+\/api\/v1\/.*\/builds\/[^/]+\/(publish|preview-data)$/.test(new URL(request.url()).pathname)) sentToWorkspace.push(request.url()) })

  // a Sync project and a draft of it, made on the console's screens of the Workspace server
  await page.goto(`/servers/${SERVER}`)
  await expect(page.getByTestId('ws-projects')).toBeVisible()
  await page.getByTestId('ws-new-project-open').click()
  await page.getByTestId('ws-new-project-id').fill('orders')
  await page.getByTestId('ws-new-project-kind').selectOption('sync')
  await page.getByTestId('ws-new-project-create').click()
  await page.getByTestId('ws-new-name-orders').fill('deploy-edit')
  await page.getByTestId('ws-new-create-orders').click()
  const listed = page.locator('[data-testid="ws-workspace"][data-workspace$="-deploy-edit"]')
  await expect(listed).toBeVisible()
  const draft = (await listed.getAttribute('data-workspace'))!
  await page.getByTestId(`ws-open-${draft}`).click()
  await expect(page.getByTestId('wsp-page')).toBeVisible()

  // make the package on the Builds tab
  await page.getByTestId('wsp-nav-files').click()
  await page.getByTestId('wsp-tab-builds').click()
  await page.getByTestId('wsp-build').click()
  const built = page.locator('[data-testid="wsp-build-detail"][data-status="succeeded"]')
  await expect(built).toBeVisible({ timeout: 15_000 })
  const build = (await built.getAttribute('data-build'))!

  // the build's publish panel by its address, as the Designer's link opens it
  await page.goto(`/servers/${SERVER}/orders/${draft}/files?tab=builds&build=${build}`)
  const done = page.locator(`[data-testid="wsp-build-detail"][data-build="${build}"]`)
  await expect(done).toHaveAttribute('data-status', 'succeeded')

  // the targets are the console's servers a Sync set goes to: the Runner, not the Gateway
  const target = done.getByTestId('wsp-publish-target')
  await expect(target.locator('option')).toHaveText(['Choose a Runner or a Gateway', `${RUNNER} (Runner)`])
  await target.selectOption(RUNNER)
  await expect(done.getByTestId('wsp-publish-form')).toHaveAttribute('data-product', 'runner')
  await expect(done.getByTestId('wsp-publish')).toBeDisabled()
  await expect(done.getByTestId('wsp-publish-why')).toHaveText('Name the channel of the Runner the package goes to.')
  await done.getByTestId('wsp-publish-channel').fill('dev')
  await done.getByTestId('wsp-publish-bind').fill('source-db=erp')
  await done.getByTestId('wsp-publish').click()

  // the version the Runner holds now, the console's steps, and the console's record of the deploy
  const published = done.getByTestId('wsp-published')
  await expect(published).toHaveAttribute('data-version', '1')
  await expect(done.getByTestId('wsp-published-line')).toHaveText('Staged orders on dev of https://runner.invalid as version 1.')
  await expect(done.locator('[data-testid="wsp-publish-step"]').first()).toContainText(`Read build ${build}`)
  const record = done.locator('[data-testid="wsp-deploy-record"]')
  await expect(record).toHaveCount(1)
  await expect(record).toHaveAttribute('data-outcome', 'ok')
  await expect(record).toHaveAttribute('data-version', '1')
  await expect(record).toContainText(`tester to ${RUNNER}`)
  await expect(record).toContainText(`Staged orders on dev of ${RUNNER} as version 1.`)

  // what the console was asked, and that nothing was sent to the Workspace server's own publish
  expect(fake.published.at(-1)).toEqual({ project: 'orders', body: { workspaceServer: SERVER, project: 'orders', workspace: draft, build, target: RUNNER, channel: 'dev', bind: { 'source-db': 'erp' } } })
  expect(fake.workspacePublishes).toEqual([])
  expect(sentToWorkspace).toEqual([])

  // the record is the console's: it is there when the page is opened again
  await page.reload()
  await expect(page.locator(`[data-testid="wsp-build-detail"][data-build="${build}"] [data-testid="wsp-deploy-record"]`)).toContainText(`Staged orders on dev of ${RUNNER} as version 1.`)
})
