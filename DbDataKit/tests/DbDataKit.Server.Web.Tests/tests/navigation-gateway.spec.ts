import { expect, test, type APIRequestContext, type Page } from '@playwright/test'
import { execFileSync } from 'node:child_process'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { gatewayDirectory, gatewayUrl } from '../playwright.config'
import { signInNewAdmin, signInNewPerson } from './people'

// A Gateway's entries in the console's navigation: Overview, then the groups Serving, Access
// and Server, nested under the Gateway's name; a viewer sees none of the pages that answer "This screen is for administrators". It uses the test Gateway
// of `playwright.config.ts` (the one `first-run.spec.ts` pairs as "Gateway one"), and pairs it with a new code when no story before it did. It signs in
// new people, so it sorts after `first-run` and `identity-home`.

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../../..')
const gatewayDll = path.join(repoRoot, 'DbDataGateway/src/DbDataGateway.Server/bin/Debug/net10.0/dbdatagateway.dll')

const groups: [string, [string, string][]][] = [
  ['', [['Overview', 'rail-overview']]],
  ['Serving', [['Data sources', 'rail-data-sources'], ['Models', 'rail-models'], ['Saved queries', 'rail-saved-queries'], ['Apps', 'rail-apps'], ['Result cache', 'rail-cache']]],
  ['Access', [['Access rules', 'rail-access'], ['Users and sign-in', 'rail-identities'], ['Identity home', 'rail-identity-home'], ['Publishers', 'rail-trust'], ['Secrets', 'rail-secrets']]],
  ['Server', [['Certificate', 'rail-tls'], ['Activity', 'rail-activity'], ['Server settings', 'rail-settings'], ['Configuration history', 'rail-config']]],
]
const adminOnly = new Set(['rail-data-sources', 'rail-identities', 'rail-identity-home', 'rail-secrets', 'rail-tls', 'rail-activity', 'rail-settings', 'rail-config'])

/** The test Gateway's id in this console: the one paired already, or paired now with a new code. */
async function testGateway(request: APIRequestContext): Promise<string> {
  type Server = { id: string; url: string; product: string | null }
  const at = (list: Server[]) => list.find((s) => s.product === 'dbdatagateway' && s.url.replace(/\/+$/, '') === gatewayUrl)
  const paired = at(await (await request.get('/api/instances')).json())
  if (paired) return paired.id
  const made = execFileSync('dotnet', ['exec', gatewayDll, 'server', 'pair', '--minutes', '120', '--repo', gatewayDirectory, '--max-role', 'admin'], { encoding: 'utf8' })
  const answer = await request.post('/api/instances', { data: { url: gatewayUrl, code: /Pairing code: (\S+)/.exec(made)![1], name: 'Navigation gateway' } })
  expect(answer.status(), await answer.text()).toBe(200)
  return ((await answer.json()) as Server).id
}

/** The Gateway's entries under its name, as the page shows them: each group's heading and the names of its entries. */
async function nested(page: Page) {
  return page.getByTestId('nav-server-entries').locator(':scope > .rail-group').evaluateAll((els) => els.map((g) => [
    g.querySelector('.rail-group-title')?.textContent ?? '',
    Array.from(g.querySelectorAll(':scope > .rail-item')).map((a) => a.textContent ?? ''),
  ]))
}

test('a Gateway\'s entries sit under Serving, Access and Server, and a viewer sees none of its administrators\' pages', async ({ page, context, baseURL, browser }) => {
  await page.setViewportSize({ width: 1440, height: 900 })
  await signInNewAdmin(page, context, baseURL!, 'Navigation Gateway Tester', `navigation-gateway-${Date.now()}@example.com`)
  const id = await testGateway(context.request)

  await page.goto(`/servers/${id}`)
  await expect(page.getByTestId('gateway-overview')).toBeVisible()
  await expect(page.getByTestId('nav-server-entries').getByTestId('nav-group-serving')).toBeVisible()
  expect(await nested(page)).toEqual(groups.map(([title, items]) => [title, items.map(([name]) => name)]))

  // a viewer: only the pages a viewer may use, and none of them says it is for administrators
  const viewerContext = await browser.newContext({ baseURL, ignoreHTTPSErrors: true, viewport: { width: 1440, height: 900 } })
  const viewer = await viewerContext.newPage()
  await signInNewPerson(viewer, viewerContext, baseURL!, 'Navigation Gateway Viewer', `navigation-gateway-viewer-${Date.now()}@example.com`, 'viewer')
  // the console answers its list of servers to an administrator only (a viewer gets 403, so a viewer reaches no server's screens in the console today):
  // the viewer's page is given the list the administrator read, and everything else (the Gateway's own screens, through the relay) is the real answer
  expect((await viewerContext.request.get('/api/instances')).status()).toBe(403)
  const listed = await (await context.request.get('/api/instances')).text()
  await viewer.route(/\/api\/instances(\?.*)?$/, (route) => route.fulfill({ status: 200, contentType: 'application/json', body: listed }))
  await viewer.goto(`/servers/${id}`)
  await expect(viewer.getByTestId('nav-server-entries')).toBeVisible()
  const seen = groups
    .map(([title, items]) => [title, items.filter(([, testId]) => !adminOnly.has(testId))] as const)
    .filter(([, items]) => items.length > 0)
  expect(await nested(viewer)).toEqual(seen.map(([title, items]) => [title, items.map(([name]) => name)]))
  for (const testId of adminOnly) await expect(viewer.getByTestId('nav-server-entries').getByTestId(testId)).toHaveCount(0)
  for (const [, items] of seen) {
    for (const [name, testId] of items) {
      await viewer.getByTestId('nav-server-entries').getByTestId(testId).click()
      await expect(viewer.getByTestId('nav-server-entries').getByTestId(testId), name).toHaveClass(/\bactive\b/)
      await expect(viewer.getByText('This screen is for administrators.'), name).toHaveCount(0)
    }
  }
  await viewerContext.close()
})
