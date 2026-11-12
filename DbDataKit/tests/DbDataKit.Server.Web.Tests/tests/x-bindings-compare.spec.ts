import { expect, test, type Route } from '@playwright/test'
import { signInNewAdmin } from './people'

// Plan 27a IX25 (docs/channel-bindings.md "Compare, adopt and publish"): the console's bindings beside a Gateway's, what differs marked, an item adopted into the console and another published to the Gateway, and the one
// combined list of posture choices. The APIs are mocked in the page (the host side is `ConsoleBindingCompareTests`, the shared library is `BindingCompareTests` and `PostureListingTests`), so it needs the signed-in
// console only. Its name sorts after `first-run` and `identity-home`.
const json = (route: Route, body: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })

const item = (channel: string, state: string, here: string | null, there: string | null) => ({
  id: `binding/agent/helper/model:default/${channel}`, family: 'binding', name: 'helper', definitionKind: 'agent', slot: 'model:default', channel, state, here, there, hereText: null, thereText: null,
  canAdopt: state === 'differs' || state === 'only-there', canPublish: state !== 'same', problem: null,
})

test('the console compares its bindings with a Gateway, adopts one value and publishes another, and lists every posture choice once', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Compare Tester', `bindings-compare-${Date.now()}@example.com`)
  const values: Record<string, { here: string | null; there: string | null }> = {
    prod: { here: 'claude/claude-sonnet', there: 'claude/claude-sonnet' },
    test: { here: 'local/llama3.1:8b', there: 'claude/claude-sonnet' },
    qa: { here: 'local/llama3.1:8b', there: null },
  }
  const decisions: { path: string; body: any }[] = []
  const state = (v: { here: string | null; there: string | null }) => (v.here === v.there ? 'same' : v.here && v.there ? 'differs' : v.here ? 'only-here' : 'only-there')

  await page.route('**/api/bindings', (route) => json(route, { file: 'bindings.yml', yaml: '', diagnostics: [], earlier: null, releaseChannels: ['prod'], connections: [], slots: [], postureSetting: 'production_binding_on_non_production', postureEffect: '' }))
  await page.route('**/api/delivery', (route) => json(route, { problem: null }))
  await page.route('**/relay/console-runner/api/v1/definitions/integration', (route) => json(route, { definitions: [] }))
  await page.route('**/api/instances', (route) => json(route, [{ id: 'gateway-1', name: 'Gateway', url: 'https://gw', product: 'dbdatagateway', version: '1', apiVersion: 1, reachable: true, ready: true, problem: null, pairedAt: null }]))
  await page.route(/\/api\/bindings\/compare\?/, (route) => {
    const server = new URL(route.request().url()).searchParams.get('server')
    return json(route, {
      server, serverName: server === 'gateway-1' ? 'Gateway' : "the console's runner", product: server === 'gateway-1' ? 'dbdatagateway' : 'dbdatarunner', problems: [],
      items: server === 'gateway-1' ? Object.entries(values).map(([channel, v]) => item(channel, state(v), v.here, v.there)) : [],
    })
  })
  await page.route('**/api/bindings/compare/adopt', (route) => {
    const body = route.request().postDataJSON()
    decisions.push({ path: new URL(route.request().url()).pathname, body })
    values[body.channel].here = values[body.channel].there
    return json(route, { done: true, item: item(body.channel, 'same', values[body.channel].here, values[body.channel].there), diagnostics: [] })
  })
  await page.route('**/api/bindings/compare/publish', (route) => {
    const body = route.request().postDataJSON()
    decisions.push({ path: new URL(route.request().url()).pathname, body })
    values[body.channel].there = values[body.channel].here
    return json(route, { done: true, item: item(body.channel, 'same', values[body.channel].here, values[body.channel].there), diagnostics: [] })
  })
  await page.route('**/api/bindings/posture', (route) => json(route, {
    entries: [
      { setting: 'production_binding_on_non_production', scope: 'bindings/delivery/alerts/test', chosenBy: 'ada', chosenAt: '2026-11-10T09:00:00Z', note: 'the live hook', heldBy: ['console'], default: 'a sandbox', effect: 'A test message reaches production.' },
      { setting: 'production_binding_on_non_production', scope: 'bindings/runner/orders/source/dev', chosenBy: 'bo', chosenAt: '2026-11-11T09:00:00Z', note: null, heldBy: ['console', "the console's runner"], default: 'a sandbox', effect: 'A write reaches production.' },
    ],
    sources: [{ holder: 'console', ok: true, choices: 2, problem: null }, { holder: "the console's runner", ok: true, choices: 1, problem: null }],
  }))

  await page.goto('/bindings')
  await expect(page.getByTestId('compare-panel')).toBeVisible()

  // the Gateway is compared: two items differ or are held by one side, the one that is the same is hidden until asked for
  await page.getByTestId('compare-server').selectOption('gateway-1')
  await expect(page.getByTestId('compare-item')).toHaveCount(2)
  await expect(page.getByTestId('compare-summary')).toContainText('1 differ, 1 only in the console')
  const test = page.locator('[data-testid="compare-item"][data-item="binding/agent/helper/model:default/test"]')
  await expect(test).toHaveAttribute('data-state', 'differs')
  await expect(test.getByTestId('compare-here')).toContainText('local/llama3.1:8b')
  await expect(test.getByTestId('compare-there')).toContainText('claude/claude-sonnet')
  await page.getByTestId('compare-only-differences').uncheck()
  await expect(page.getByTestId('compare-item')).toHaveCount(3)
  await page.getByTestId('compare-only-differences').check()

  // adopt the Gateway's value into the console: the decision names the item and the value that was shown
  await test.getByTestId('compare-adopt').click()
  await expect.poll(() => decisions.length).toBe(1)
  expect(decisions[0].path).toBe('/api/bindings/compare/adopt')
  expect(decisions[0].body).toMatchObject({ server: 'gateway-1', family: 'binding', name: 'helper', definitionKind: 'agent', slot: 'model:default', channel: 'test', expected: 'claude/claude-sonnet' })
  await expect(page.locator('[data-testid="compare-said"][data-item="binding/agent/helper/model:default/test"]')).toContainText('Adopted')

  // publish the console's value to the Gateway for the release channel it has none for
  const qa = page.locator('[data-testid="compare-item"][data-item="binding/agent/helper/model:default/qa"]')
  await expect(qa).toHaveAttribute('data-state', 'only-here')
  await qa.getByTestId('compare-publish').click()
  await expect.poll(() => decisions.length).toBe(2)
  expect(decisions[1].path).toBe('/api/bindings/compare/publish')
  expect(decisions[1].body).toMatchObject({ server: 'gateway-1', channel: 'qa', expected: null })
  await expect(page.locator('[data-testid="compare-said"][data-item="binding/agent/helper/model:default/qa"]')).toContainText('Published to Gateway')

  // the posture choices of both tables are one list; a choice both hold is on one line naming both
  await expect(page.getByTestId('posture-entry')).toHaveCount(2)
  const once = page.locator('[data-testid="posture-entry"][data-scope="bindings/runner/orders/source/dev"]')
  await expect(once.getByTestId('posture-held')).toContainText("console and the console's runner")
  await once.getByTestId('posture-effect-button').click()
  await expect(once.getByTestId('posture-effect')).toContainText('A write reaches production.')
})
