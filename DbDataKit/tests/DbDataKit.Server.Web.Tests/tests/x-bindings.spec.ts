import { expect, test, type Route } from '@playwright/test'
import { signInNewAdmin } from './people'

// Plan 27a IX23 (docs/channel-bindings.md): ONE Bindings screen for every slot. This story mocks the bindings, delivery, agents and runner APIs in the page (the host side is `DbDataKit.Server.Tests` ConsoleDeliveryTests and ConsoleModelsTests,
// `DbDataKit.Connections.Abstractions.Tests` BindingsTests), so it needs the signed-in console only. Its name sorts after `first-run` and `identity-home`.
const json = (route: Route, body: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })

const entry = (connection: string, model: string | null = null, extra: Record<string, unknown> = {}) => ({ connection, model, usable: true, problem: null, kind: model ? 'model-provider' : 'http', target: model ? null : 'https://hooks.slack.com/services/{token}', ...extra })
const row = (release: string, entries: unknown[], extra: Record<string, unknown> = {}) => ({ release, wildcard: release === '*', entries, needsConfirmation: false, confirmed: false, effect: null, ...extra })
const connections = [
  { name: 'claude', kind: 'model-provider', purpose: 'chat', models: ['claude-sonnet'], canSend: false, target: null, cloud: true },
  { name: 'local', kind: 'model-provider', purpose: 'chat', models: ['llama3.1:8b'], canSend: false, target: null, cloud: false },
  { name: 'slack-ops', kind: 'http', purpose: null, models: [], canSend: true, target: 'https://hooks.slack.com/services/{token}', cloud: null },
  { name: 'slack-sandbox', kind: 'http', purpose: null, models: [], canSend: true, target: 'https://hooks.slack.com/services/{token}', cloud: null },
]
const view = (slots: unknown[]) => ({ file: 'bindings.yml', yaml: '', diagnostics: [], earlier: null, releaseChannels: ['prod', 'test'], connections, slots, postureSetting: 'production_binding_on_non_production', postureEffect: 'It reaches production.' })

test('every slot of every kind is bound per release channel in one place: a model fallback is reordered, a delivery channel is tested through its sandbox, production in a test channel is confirmed, and the runner\'s slots are shown the same way', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Binding Tester', `bindings-${Date.now()}@example.com`)
  const saved: { path: string; body: any }[] = []
  let slots: any[] = [
    { section: 'models', kind: 'model-chat', name: 'default', usedBy: ['helper'], limitPerMinute: null, rows: [row('prod', [entry('claude', 'claude-sonnet')])] },
    { section: 'delivery', kind: 'delivery', name: 'alerts', usedBy: [], limitPerMinute: null, rows: [row('prod', [entry('slack-ops')]), row('test', [entry('slack-sandbox')])] },
  ]
  await page.route('**/api/bindings', (route) => json(route, view(slots)))
  await page.route('**/api/bindings/slots/**', (route) => {
    const body = route.request().postDataJSON()
    saved.push({ path: new URL(route.request().url()).pathname, body })
    const sharesProd = body.channels.test?.[0]?.connection === 'slack-ops' && !body.confirm
    if (sharesProd) return json(route, { saved: false, diagnostics: [], needsConfirmation: [{ scope: 'bindings/delivery/alerts/test', section: 'delivery', slot: 'alerts', release: 'test', connection: 'slack-ops', effect: 'The delivery channel alerts would use slack-ops, which production uses.' }] }, 409)
    return json(route, { saved: true, diagnostics: [], view: view(slots) })
  })
  await page.route('**/api/delivery', (route) => json(route, { problem: null, canRender: false, renderNote: '', isAdmin: true, policy: { maxAttempts: 6, backoffSeconds: [30], leaseSeconds: 120 }, releaseChannels: ['prod', 'test'], connections: [], connectionKinds: [], outbox: {}, channels: [] }))
  await page.route('**/api/delivery/channels/alerts/test', (route) => json(route, { id: 'm1', releaseChannel: 'test', connection: 'slack-sandbox', attempts: [], message: { id: 'm1', channel: 'alerts', releaseChannel: 'test', subject: '[TEST]', recipients: [], source: 'test', ownerPerson: null, status: 'sent', attempts: 1, nextAttemptAt: 't', lastOutcome: 'sent', lastDetail: '200 OK', createdAt: 't', settledAt: 't', attachments: [] } }))
  await page.route('**/relay/console-runner/api/v1/definitions/integration', (route) => json(route, { definitions: [{ name: 'orders' }] }))
  await page.route('**/relay/console-runner/api/v1/definitions/integration/orders', (route) => json(route, { slots: [{ channel: 'dev', slot: 'source', kind: 'sqlserver', tasks: ['load'], versions: [1] }, { channel: 'prod', slot: 'source', kind: 'sqlserver', tasks: ['load'], versions: [1] }], bindings: [{ channel: 'dev', slot: 'source', bound_to: 'erp', revision: 1 }] }))

  await page.goto('/bindings')
  await expect(page.getByTestId('bindings-panel')).toBeVisible()
  await expect(page.getByTestId('bindings-rule')).toContainText('never uses another')

  // the three kinds are listed in one shape, the runner's slot beside them, and a release channel with no entry says it is unbound
  await expect(page.getByTestId('binding-slot')).toHaveCount(3)
  await expect(page.getByTestId('binding-row-alerts-test')).toContainText('slack-sandbox')
  await expect(page.getByTestId('binding-state-orders/source-prod')).toContainText('not bound')
  await expect(page.getByTestId('binding-row-orders/source-dev')).toContainText('erp')

  // a delivery channel is tested through the test release channel's connection
  await page.getByTestId('binding-test-alerts-test').click()
  await expect(page.getByTestId('binding-test-result-alerts-test')).toContainText('through slack-sandbox: sent')

  // a model slot gets a fallback and the fallback is reordered
  await page.getByTestId('binding-edit-default').click()
  await page.getByTestId('binding-add-choice-default-prod').click()
  await page.getByTestId('binding-select-default-prod-1').selectOption('local')
  await page.getByTestId('binding-up-default-prod-1').click()
  await expect(page.getByTestId('binding-select-default-prod-0')).toHaveValue('local')
  await page.getByTestId('binding-save-default').click()
  await expect.poll(() => saved.length).toBe(1)
  expect(saved[0].path).toBe('/api/bindings/slots/models/default')
  expect(saved[0].body.channels.prod).toEqual([{ connection: 'local', model: 'llama3.1:8b' }, { connection: 'claude', model: 'claude-sonnet' }])

  // production's connection in the test release channel is asked first and sent with the confirmation and the note
  await page.getByTestId('binding-edit-alerts').click()
  await page.getByTestId('binding-select-alerts-test-0').selectOption('slack-ops')
  await page.getByTestId('binding-save-alerts').click()
  await expect(page.getByTestId('binding-confirm-alerts')).toContainText('production')
  await page.getByTestId('binding-confirm-note-alerts').fill('the test channel may use the live hook this week')
  await page.getByTestId('binding-save-alerts').click()
  await expect.poll(() => saved.length).toBe(2)
  expect(saved[1].body.confirm).toEqual(['bindings/delivery/alerts/test'])
  expect(saved[1].body.note).toBe('the test channel may use the live hook this week')

  // the Delivery page shows the same panel for its channels, not a second editor
  await page.goto('/delivery')
  await expect(page.getByTestId('delivery-channels').getByTestId('bindings-panel')).toBeVisible()
  await expect(page.getByTestId('delivery-binding-alerts-prod')).toHaveCount(0)
})
