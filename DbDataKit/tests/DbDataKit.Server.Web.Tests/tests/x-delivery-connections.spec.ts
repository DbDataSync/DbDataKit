import { expect, test, type Route } from '@playwright/test'
import { signInNewAdmin } from './people'

// Plan 22a GP18 fold (owner decision 2026-11-11): the delivery channels are connections, bound per release channel. This story mocks the delivery and outside-connections APIs in the page (the host side is `DbDataKit.Server.Tests` ConsoleDeliveryTests and
// OutsideConnectionsTests, `DbDataKit.Delivery.Tests` ConnectionChannelTests), so it needs the signed-in console only. It invites a console administrator as `identity-admin.spec.ts` does; its name sorts after `first-run` and `identity-home`.
const json = (route: Route, body: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })

const binding = (release: string, connection: string, extra: Record<string, unknown> = {}) => ({
  release, connection, own: true, kind: 'slack', target: 'https://hooks.slack.com/services/{token}', ready: true, missing: [], problem: null, ...extra,
})

const overview = (alerts: unknown[]) => ({
  problem: null, canRender: false, renderNote: 'no renderer', isAdmin: true, policy: { maxAttempts: 6, backoffSeconds: [30], leaseSeconds: 120 }, releaseChannels: ['prod', 'test', 'dev'],
  connections: [{ name: 'slack-ops', kind: 'slack', target: 'https://hooks.slack.com/services/{token}' }, { name: 'slack-sandbox', kind: 'slack', target: 'https://hooks.slack.com/services/{token}' }],
  connectionKinds: [{ kind: 'smtp', description: 'A mail server' }], outbox: {},
  channels: [{ name: 'alerts', kind: 'slack', limitPerMinute: null, ready: true, missing: [], bindings: alerts, outbox: {} }],
})

test('the Channels tab shows the connection each release channel uses, tests the sandbox without touching production and saves a changed mapping', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Delivery Binder', `delivery-bind-${Date.now()}@example.com`)
  let bindings = [binding('prod', 'slack-ops'), binding('test', 'slack-sandbox')]
  const tests: unknown[] = []
  let saved: unknown = null
  await page.route('**/api/delivery', (route) => json(route, overview(bindings)))
  await page.route('**/api/delivery/channels/alerts/test', (route) => {
    const body = route.request().postDataJSON() as { channel: string }
    tests.push(body)
    return json(route, { id: 'm1', releaseChannel: body.channel, connection: body.channel === 'test' ? 'slack-sandbox' : 'slack-ops', attempts: [],
      message: { id: 'm1', channel: 'alerts', releaseChannel: body.channel, subject: '[TEST] x', recipients: [], source: 'test', ownerPerson: null, status: 'sent', attempts: 1, nextAttemptAt: 't', lastOutcome: 'sent', lastDetail: '200 OK from hooks.slack.com', createdAt: 't', settledAt: 't', attachments: [] } })
  })
  await page.route('**/api/delivery/channels', (route) => {
    saved = route.request().postDataJSON()
    bindings = [binding('prod', 'slack-ops'), binding('test', 'slack-sandbox'), binding('dev', 'slack-sandbox')]
    return json(route, { saved: true, releaseChannels: ['prod', 'test', 'dev'], channels: overview(bindings).channels })
  })

  await page.goto('/delivery')
  await expect(page.getByTestId('delivery-channels')).toBeVisible()
  await expect(page.getByTestId('delivery-binding-alerts-prod')).toHaveAttribute('data-connection', 'slack-ops')
  await expect(page.getByTestId('delivery-binding-alerts-test')).toHaveAttribute('data-connection', 'slack-sandbox')
  await expect(page.getByTestId('delivery-binding-target-alerts-test')).toContainText('https://hooks.slack.com/services/{token}')   // the template, never an address
  await expect(page.getByTestId('delivery-release-note')).toContainText('never to production')

  // the test send goes through the test release channel's connection
  await page.getByTestId('delivery-test-alerts-test').click()
  await expect(page.getByTestId('delivery-test-result-alerts')).toContainText('Test through slack-sandbox (test)')
  expect(tests).toEqual([{ recipient: null, channel: 'test' }])

  // a release channel is bound to a connection, and saved
  await page.getByTestId('delivery-binding-new-alerts').fill('dev')
  await page.getByTestId('delivery-binding-add-alerts').click()
  await expect(page.getByTestId('delivery-bindings-save')).toBeEnabled()
  await expect(page.getByTestId('delivery-test-alerts-dev')).toBeDisabled()                       // not saved yet: nothing to test
  await page.getByTestId('delivery-binding-select-alerts-dev').selectOption('slack-sandbox')
  await page.getByTestId('delivery-bindings-save').click()
  await expect(page.getByTestId('delivery-binding-alerts-dev')).toHaveAttribute('data-ready', 'true')
  expect(saved).toEqual({ channels: { alerts: { prod: 'slack-ops', test: 'slack-sandbox', dev: 'slack-sandbox' } } })
})

test('a webhook is started from a template, its pasted address is split and only the template is shown', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Webhook Tester', `webhook-${Date.now()}@example.com`)
  const TEMPLATE = 'schema: connection/2\nkind: http\nname: ops-slack\nendpoint: https://hooks.slack.com/services/{token}\nendpoint_secrets:\n  token: secret:x:ops-slack:token\nwebhook:\n  format: slack\n'
  const pasted: unknown[] = []
  await page.route('**/api/outside-connections', (route) => json(route, { kinds: ['http', 'model-provider', 'smtp'], flavors: ['slack', 'teams', 'signed'], connections: [] }))
  await page.route('**/api/outside-connections/template*', (route) => json(route, { yaml: TEMPLATE }))
  await page.route('**/api/outside-connections/ops-slack/webhook', (route) => {
    pasted.push(route.request().postDataJSON())
    return json(route, { detected: true, service: 'Slack', format: 'slack', template: 'https://hooks.slack.com/services/{token}', secrets: { token: 'secret:x:ops-slack:token' } })
  })

  await page.goto('/connections')
  await expect(page.getByTestId('outside-connections')).toBeVisible()
  await page.getByTestId('outside-new-name').fill('ops-slack')
  await page.getByTestId('outside-new-kind').selectOption('slack')
  await expect(page.getByTestId('outside-new-meaning')).toContainText('Slack incoming webhook')
  await page.getByTestId('outside-new-start').click()
  await expect(page.getByTestId('outside-editor')).toBeVisible()
  await expect(page.getByTestId('outside-webhook')).toBeVisible()

  // the address is typed into a password field, split on the server, and the editor then holds the template; no part of the address is on the page
  const url = 'https://hooks.slack.com/services/T000/B000/XXXXCANARYXXXX'
  await expect(page.getByTestId('outside-webhook-url')).toHaveAttribute('type', 'password')
  await page.getByTestId('outside-webhook-url').fill(url)
  await page.getByTestId('outside-webhook-split').click()
  await expect(page.getByTestId('outside-webhook-detected')).toHaveAttribute('data-service', 'Slack')
  await expect(page.getByTestId('outside-webhook-detected')).toContainText('https://hooks.slack.com/services/{token}')
  expect(pasted).toEqual([{ url }])
  await expect(page.getByText('XXXXCANARYXXXX')).toHaveCount(0)
  await expect(page.getByTestId('outside-yaml').locator('.monaco-editor')).toContainText('endpoint_secrets')

})
