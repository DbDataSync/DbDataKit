import { expect, test, type Route } from '@playwright/test'
import { signInNewAdmin } from './people'

// Plan 27a IX23 (docs/channel-bindings.md "The runner: what it keeps and how the console hands it a connection"): an administrator changes a runner slot's connection, sees the runner's copy of it against the console's file,
// sends it again, and confirms production's connection in a non-production channel. The APIs are mocked in the page (the host side is `ConsoleRunnerBindingsTests` and the runner's `RunnerConnectionCopyTests`), so it needs the signed-in
// console only. Its name sorts after `first-run` and `identity-home`.
const json = (route: Route, body: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })

const crm = (version: string) => ({ name: 'crm', kind: 'http', purpose: null, models: [], canSend: true, target: 'https://crm.example/api', cloud: null, version })

test('a runner slot is bound to a console connection, its copy is compared with the console file and sent again, and production in a dev channel is confirmed', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Runner Binder', `bindings-runner-${Date.now()}@example.com`)
  let consoleVersion = 'sha256:aaaa111122223333'
  let copyVersion: string | null = 'sha256:old0111122223333'
  let devBound: string | null = null
  const calls: { method: string; path: string; body: any }[] = []
  const view = () => ({ file: 'bindings.yml', yaml: '', diagnostics: [], earlier: null, releaseChannels: ['prod'], connections: [crm(consoleVersion)], slots: [], postureSetting: 'production_binding_on_non_production', postureEffect: 'It reaches production.' })

  await page.route('**/api/bindings', (route) => json(route, view()))
  await page.route('**/api/delivery', (route) => json(route, { problem: null }))
  await page.route('**/relay/console-runner/api/v1/definitions/integration', (route) => json(route, { definitions: [{ name: 'orders' }] }))
  await page.route('**/relay/console-runner/api/v1/definitions/integration/orders', (route) => json(route, {
    slots: [{ channel: 'prod', slot: 'api', kind: 'http', tasks: ['pull'], versions: [1] }, { channel: 'dev', slot: 'api', kind: 'http', tasks: ['pull'], versions: [1] }],
    bindings: [{ channel: 'prod', slot: 'api', bound_to: 'crm', revision: 1, shares_production: false, confirmed: false }, ...(devBound ? [{ channel: 'dev', slot: 'api', bound_to: devBound, revision: 1, shares_production: true, confirmed: true }] : [])],
  }))
  await page.route('**/relay/console-runner/api/v1/connection-copies', (route) => json(route, { copies: copyVersion ? [{
    name: 'crm', version: copyVersion, kind: 'http', sent_by: 'ada', sent_at: '2026-11-11T10:00:00Z', shadowed_by_file: false, used_by: [], problem: null, unresolved: 1,
    secrets: [{ path: 'sign_in.key_secret', reference: 'secret:dbdatakit:server:connection:crm:key', resolves: false, problem: 'It is not in the runner\'s secret store.' }],
  }] : [] }))
  await page.route('**/api/bindings/runner/console-runner/connections/crm/send', (route) => {
    calls.push({ method: 'POST', path: new URL(route.request().url()).pathname, body: null })
    copyVersion = consoleVersion
    return json(route, { connection: 'crm', version: consoleVersion, unresolved: [] })
  })
  await page.route('**/api/bindings/runner/console-runner/orders/api', (route) => {
    const body = route.request().postDataJSON()
    calls.push({ method: 'PUT', path: new URL(route.request().url()).pathname, body })
    if (body.channels.dev === 'crm' && !body.confirm)
      return json(route, { saved: false, error: 'Production\'s connection would serve a non-production release channel.', needsConfirmation: [{ scope: 'bindings/runner/orders/api/dev', section: 'runner', slot: 'orders/api', release: 'dev', connection: 'crm', effect: 'The slot orders/api would use crm, which production uses, in the channel dev.' }] }, 409)
    devBound = body.channels.dev ?? devBound
    return json(route, { saved: true, sent: [] })
  })

  await page.goto('/bindings')
  const prod = 'binding-copy-orders/api-prod'

  // the runner's copy is older than the console's file, and the runner cannot resolve the reference
  await expect(page.getByTestId(prod)).toHaveAttribute('data-copy', 'stale')
  await expect(page.getByTestId(prod)).toContainText('older than the console')
  await expect(page.getByTestId('binding-copy-secret-orders/api-prod')).toContainText('secret:dbdatakit:server:connection:crm:key')

  // Send again: the copy is the console's file afterwards
  await page.getByTestId('binding-send-orders/api-prod').click()
  await expect.poll(() => calls.length).toBe(1)
  expect(calls[0].path).toBe('/api/bindings/runner/console-runner/connections/crm/send')
  await expect(page.getByTestId(prod)).toHaveAttribute('data-copy', 'current')

  // the console file changes: the copy is stale again, the screen says so
  consoleVersion = 'sha256:bbbb111122223333'
  await page.reload()
  await expect(page.getByTestId(prod)).toHaveAttribute('data-copy', 'stale')

  // a dev channel on production's connection is asked first, then sent with the confirmation and a note
  await page.getByTestId('binding-edit-orders/api').click()
  await page.getByTestId('binding-new-release-orders/api').fill('dev')
  await page.getByTestId('binding-new-release-add-orders/api').click()
  await page.getByTestId('binding-select-orders/api-dev-0').fill('crm')
  await page.getByTestId('binding-save-orders/api').click()
  await expect(page.getByTestId('binding-confirm-orders/api')).toContainText('production')
  await page.getByTestId('binding-confirm-note-orders/api').fill('the dev crm is the live one this week')
  await page.getByTestId('binding-save-orders/api').click()
  await expect.poll(() => calls.length).toBe(3)
  expect(calls[2].path).toBe('/api/bindings/runner/console-runner/orders/api')
  expect(calls[2].body.confirm).toEqual(['bindings/runner/orders/api/dev'])
  expect(calls[2].body.note).toBe('the dev crm is the live one this week')
  expect(calls[2].body.channels).toEqual({ prod: 'crm', dev: 'crm' })
})
