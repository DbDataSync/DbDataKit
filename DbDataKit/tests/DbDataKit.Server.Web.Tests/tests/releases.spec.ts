import { expect, test, type Route } from '@playwright/test'
import { signInNewAdmin } from './people'

// Plan 21 CH02: the Releases screen over the console's release API, mocked in the page (the console side is the release tests of DbDataKit.Server.Tests), so the story needs the signed-in console only. It checks what the screen SENDS as well as what it shows:
// the kind picker (a runner package, a Gateway definition) changes the servers offered and the placeholder, the form says why the draft cannot be made, and a release goes through draft, approved, running, stopped (an item failed) and resumed
// to succeeded; another draft is abandoned. It signs in a new administrator, so the file sorts after `identity-home` and `first-run`.
const json = (route: Route, body: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })
const server = (id: string, name: string, product: string) => ({ id, name, url: 'https://x.invalid', product, version: '1', apiVersion: 1, reachable: true, ready: true, problem: null, pairedAt: null })

test('a release is drafted with the kind picker, approved, started, stopped at a failing item, resumed and finished, and another draft is abandoned', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Releases Tester', `releases-${Date.now()}@example.com`)
  const created: Record<string, any>[] = []
  const calls: string[] = []
  let releases: any[] = []
  let failFirstStart = true
  await page.route('**/api/instances', (route) => route.request().method() === 'GET'
    ? json(route, [server('rn-test', 'Test runner', 'dbdatarunner'), server('rn-prod', 'Prod runner', 'dbdatarunner'), server('gw-test', 'Test gateway', 'dbdatagateway'), server('gw-prod', 'Prod gateway', 'dbdatagateway')])
    : route.fallback())
  const view = (id: string, name: string, body: any) => ({
    id, name, state: 'draft', createdBy: 'u1', createdByName: 'Releases Tester', createdAt: '2026-11-12T10:00:00Z', approvedBy: null, approvedByName: '', approvedAt: null, stoppedAt: null, stoppedReason: null, updatedAt: '2026-11-12T10:00:00Z',
    items: body.items.map((i: any, n: number) => ({ position: n + 1, product: i.product, target: i.target, source: i.source ?? null, kind: i.kind, name: i.name, version: i.version, fromChannel: i.fromChannel, toChannel: i.toChannel, state: 'pending', error: null, finishedAt: null })),
  })
  await page.route('**/api/releases', async (route) => {
    if (route.request().method() === 'GET') return json(route, releases)
    const body = route.request().postDataJSON() as Record<string, any>
    created.push(body)
    const made = view(`r${created.length}`, body.name, body)
    releases = [...releases, made]
    return json(route, made, 201)
  })
  await page.route(/\/api\/releases\/[^/]+\/(approve|start|resume|abandon)$/, async (route) => {
    const [, id, verb] = /releases\/([^/]+)\/(\w+)$/.exec(route.request().url())!
    calls.push(`${verb} ${id}`)
    const r = releases.find((x) => x.id === id)
    if (verb === 'approve') Object.assign(r, { state: 'approved', approvedBy: 'u1', approvedByName: 'Releases Tester' })
    if (verb === 'abandon') r.state = 'abandoned'
    if (verb === 'start') {
      if (failFirstStart) { failFirstStart = false; r.items[0].state = 'done'; r.items[1].state = 'failed'; r.items[1].error = 'Prod gateway refused: the definition has no version 4.'; Object.assign(r, { state: 'stopped', stoppedReason: 'Item 2 failed: Prod gateway refused: the definition has no version 4.' }) }
      else { r.items.forEach((i: any) => { i.state = 'done' }); r.state = 'succeeded' }
    }
    if (verb === 'resume') { r.items.forEach((i: any) => { i.state = 'done'; i.error = null }); Object.assign(r, { state: 'succeeded', stoppedReason: null }) }
    return json(route, r)
  })

  await page.goto('/releases')
  await expect(page.getByTestId('releases-page')).toBeVisible()
  await expect(page.getByTestId('releases-empty')).toBeVisible()

  // the form says what is missing, one thing at a time
  const why = page.getByTestId('release-why')
  await expect(page.getByTestId('release-create')).toBeDisabled()
  await expect(why).toHaveText('Name the release.')
  await page.getByTestId('release-name').fill('Spring')
  await expect(why).toHaveText('Item 1: choose the server it goes to.')

  // the runner kind offers the runners, not the Gateways; the kind picker lists the Gateway definitions and swaps the servers and the placeholder
  const item = page.getByTestId('release-item-form').first()
  await expect(item.getByTestId('release-item-target').locator('option')).toHaveText(['Goes to...', 'Test runner', 'Prod runner'])
  await expect(item.getByTestId('release-item-name')).toHaveAttribute('placeholder', 'package')
  await item.getByTestId('release-item-target').selectOption('rn-prod')
  await expect(why).toHaveText('Item 1: name the package.')
  await item.getByTestId('release-item-name').fill('orders')
  await item.getByTestId('release-item-version').fill('x')
  await expect(why).toHaveText('Item 1: the version is a whole number.')
  await item.getByTestId('release-item-version').fill('3')
  await expect(why).toHaveText('Item 1: name both channels.')
  await item.getByTestId('release-item-from').fill('test')
  await item.getByTestId('release-item-to').fill('prod')
  await expect(why).toHaveCount(0)
  await expect(page.getByTestId('release-create')).toBeEnabled()

  await page.getByTestId('release-item-add').click()
  const second = page.getByTestId('release-item-form').nth(1)
  await second.getByTestId('release-item-kind').selectOption('dashboard')
  await expect(second.getByTestId('release-item-target').locator('option')).toHaveText(['Goes to...', 'Test gateway', 'Prod gateway'])
  await expect(second.getByTestId('release-item-source').locator('option')).toHaveText(['Same server', 'Test gateway', 'Prod gateway'])
  await expect(second.getByTestId('release-item-name')).toHaveAttribute('placeholder', 'definition')
  await expect(why).toHaveText('Item 2: choose the server it goes to.')
  await second.getByTestId('release-item-source').selectOption('gw-test')
  await second.getByTestId('release-item-target').selectOption('gw-prod')
  await second.getByTestId('release-item-name').fill('sales')
  await second.getByTestId('release-item-version').fill('4')
  await second.getByTestId('release-item-from').fill('test')
  await second.getByTestId('release-item-to').fill('prod')
  await expect(page.getByTestId('release-create')).toBeEnabled()
  // an item can be removed again, and the kind of the other stays
  await second.getByTestId('release-item-remove').click()
  await expect(page.getByTestId('release-item-form')).toHaveCount(1)
  await page.getByTestId('release-item-add').click()
  const again = page.getByTestId('release-item-form').nth(1)
  await again.getByTestId('release-item-kind').selectOption('dashboard')
  await again.getByTestId('release-item-source').selectOption('gw-test')
  await again.getByTestId('release-item-target').selectOption('gw-prod')
  await again.getByTestId('release-item-name').fill('sales')
  await again.getByTestId('release-item-version').fill('4')
  await again.getByTestId('release-item-from').fill('test')
  await again.getByTestId('release-item-to').fill('prod')
  await page.getByTestId('release-create').click()

  expect(created).toEqual([{ name: 'Spring', items: [
    { product: 'runner', kind: 'integration', target: 'rn-prod', name: 'orders', version: 3, fromChannel: 'test', toChannel: 'prod' },
    { product: 'gateway', kind: 'dashboard', target: 'gw-prod', source: 'gw-test', name: 'sales', version: 4, fromChannel: 'test', toChannel: 'prod' },
  ] }])
  const release = page.locator('[data-testid="release"][data-release="Spring"]')
  await expect(release).toHaveAttribute('data-state', 'draft')
  await expect(release.getByTestId('release-item')).toHaveText([
    '1orders v3 to Prod runner: test to prodpending',
    '2dashboard sales v4 from Test gateway to Prod gateway: test to prodpending',
  ])
  await expect(release).toContainText('Made by Releases Tester, not approved yet')
  // the form is empty again, with one item
  await expect(page.getByTestId('release-name')).toHaveValue('')
  await expect(page.getByTestId('release-item-form')).toHaveCount(1)

  // a draft offers Approve and Abandon, and no Start
  await expect(release.getByTestId('release-start')).toHaveCount(0)
  await expect(release.getByTestId('release-resume')).toHaveCount(0)
  await release.getByTestId('release-approve').click()
  await expect(release).toHaveAttribute('data-state', 'approved')
  await expect(release).toContainText('approved by Releases Tester')
  await expect(release.getByTestId('release-approve')).toHaveCount(0)

  // the first start stops at the second item: the first stays done, the second says why, and Resume is offered (not Start)
  await release.getByTestId('release-start').click()
  await expect(release).toHaveAttribute('data-state', 'stopped')
  await expect(release.getByTestId('release-stopped')).toContainText('Item 2 failed: Prod gateway refused')
  await expect(release.getByTestId('release-item').nth(0)).toHaveAttribute('data-state', 'done')
  await expect(release.getByTestId('release-item').nth(1)).toHaveAttribute('data-state', 'failed')
  await expect(release.getByTestId('release-item').nth(1)).toContainText('the definition has no version 4')
  await expect(release.getByTestId('release-start')).toHaveCount(0)
  await release.getByTestId('release-resume').click()
  await expect(release).toHaveAttribute('data-state', 'succeeded')
  await expect(release.getByTestId('release-stopped')).toHaveCount(0)
  await expect(release.getByTestId('release-abandon')).toHaveCount(0)
  expect(calls).toEqual(['approve r1', 'start r1', 'resume r1'])

  // a second draft is abandoned: no button is left on it
  await page.getByTestId('release-name').fill('Summer')
  await page.getByTestId('release-item-target').first().selectOption('rn-test')
  await page.getByTestId('release-item-name').first().fill('billing')
  await page.getByTestId('release-item-version').first().fill('1')
  await page.getByTestId('release-item-from').first().fill('test')
  await page.getByTestId('release-item-to').first().fill('prod')
  await page.getByTestId('release-create').click()
  const summer = page.locator('[data-testid="release"][data-release="Summer"]')
  await expect(summer).toHaveAttribute('data-state', 'draft')
  await summer.getByTestId('release-abandon').click()
  await expect(summer).toHaveAttribute('data-state', 'abandoned')
  await expect(summer.locator('button')).toHaveCount(0)
})
