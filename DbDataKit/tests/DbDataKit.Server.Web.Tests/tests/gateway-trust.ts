import { expect, type Page, type Route } from '@playwright/test'

// Plan 30 PB03: the Gateway's Publishers screen (the trust store of every serving kind), against a Gateway whose relay is mocked in the page (the Gateway side, with the roles, the Ed25519 check and the audit, is
// State.Api.Tests' DefinitionsApiTests and DbDataGateway.Tests). The mock keeps the keys per kind, the requirement per kind and the group's mode, and every body the screen sent.

const SERVER = 'gw-1'
const json = (route: Route, body: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })
const KEY = 'Ab3_'.repeat(10) + 'Ab3'   // 43 characters of base64url
const REFUSED_KEY = 'Z'.repeat(43)     // the mock's administrator-only refusal, to see a refusal in the Gateway's words

interface MockKey { key_id: string; label: string | null; added_by: string; added_at: string; revoked: boolean; revoked_by: string | null }
export interface TrustMock { sent: Record<string, unknown[]>; keys: Map<string, MockKey[]>; required: Map<string, boolean>; group: { mode: 'test' | 'production' } }

export async function mockGateway(page: Page): Promise<TrustMock> {
  const mock: TrustMock = { sent: {}, keys: new Map(), required: new Map(), group: { mode: 'production' } }
  await page.route('**/api/instances', (route) => route.request().method() === 'GET'
    ? json(route, [{ id: SERVER, name: 'Trust gateway', url: 'https://gateway.invalid', product: 'dbdatagateway', version: '1', apiVersion: 1, reachable: true, ready: true, problem: null, pairedAt: null }])
    : route.fallback())
  await page.route(`**/relay/${SERVER}/api/v1/**`, async (route) => {
    const request = route.request()
    const method = request.method()
    const tail = decodeURIComponent(new URL(request.url()).pathname.slice(`/relay/${SERVER}/api/v1/`.length))
    const body = method === 'PUT' || method === 'POST' ? request.postDataJSON() as Record<string, any> | null : undefined
    if (method !== 'GET') (mock.sent[`${method} ${tail}`] ??= []).push(body ?? null)
    const parts = tail.split('/')
    if (parts[0] === 'trust' && parts.length === 2 && method === 'GET') {
      const kind = parts[1]
      return json(route, { kind, required: mock.required.get(kind) ?? mock.group.mode === 'production', test_group: mock.group.mode === 'test', keys: mock.keys.get(kind) ?? [] })
    }
    if (parts[0] === 'trust' && parts[2] === 'keys' && method === 'PUT') {
      if (body!.public_key === REFUSED_KEY) return json(route, { error: 'This call is refused: this needs the admin role.', code: 'denied' }, 403)
      const list = mock.keys.get(parts[1]) ?? []
      const key_id = `${list.length + 1}`.padStart(16, 'a')
      list.push({ key_id, label: body!.label, added_by: 'Trust Tester', added_at: '2026-11-12T08:00:00Z', revoked: false, revoked_by: null })
      mock.keys.set(parts[1], list)
      return json(route, { kind: parts[1], key_id, revoked: false })
    }
    if (parts[0] === 'trust' && parts[3] === 'revoke' && method === 'POST') {
      const key = (mock.keys.get(parts[1]) ?? []).find((k) => k.key_id === parts[2] && !k.revoked)
      if (!key) return json(route, { error: 'There is no such key to revoke.' }, 404)
      key.revoked = true
      key.revoked_by = 'Trust Tester'
      return json(route, { kind: parts[1], key_id: parts[2], revoked: true })
    }
    if (parts[0] === 'settings' && parts[1] === 'signatures' && method === 'PUT') { mock.required.set(parts[2], body!.required); return json(route, { kind: parts[2], required: body!.required }) }
    if (parts[0] === 'settings' && parts[1] === 'group' && method === 'PUT') { mock.group.mode = body!.mode; mock.required.clear(); return json(route, { mode: body!.mode }) }
    return json(route, { error: 'There is no such call.' }, 404)
  })
  return mock
}

/**
 * PB03: the screen lists a card for every kind a publisher can send; a kind in a production group requires signatures and has no key (so says it); a key is typed, checked on the screen (it must be 32 bytes in base64url) and trusted, and the
 * Gateway's refusal is shown in its words; a revocation asks first and the key then reads revoked; the requirement is switched per kind and the group is marked a test group, which makes signatures optional.
 */
export async function trustStory(page: Page) {
  const outside: string[] = []
  const origin = new URL(page.url()).origin
  page.on('request', (request) => { if (/^https?:/.test(request.url()) && !request.url().startsWith(origin)) outside.push(request.url()) })
  const mock = await mockGateway(page)

  await page.goto(`/servers/${SERVER}/trust`)
  await expect(page.getByTestId('gateway-trust')).toBeVisible()
  await expect(page.getByTestId('trust-store')).toHaveCount(9)
  await expect(page.getByTestId('trust-group')).toContainText('production group')
  await expect(page.getByTestId('trust-app-required')).toHaveAttribute('data-value', 'true')
  await expect(page.getByTestId('trust-app-none')).toContainText('every publish of it is refused until one is added')
  await expect(page.getByTestId('trust-app-add')).toBeDisabled()

  // a key that is not 32 bytes in base64url is said so before anything is sent
  await page.getByTestId('trust-app-public-key').fill('not a key')
  await expect(page.getByTestId('trust-app-key-problem')).toBeVisible()
  await expect(page.getByTestId('trust-app-add')).toBeDisabled()

  // the Gateway's refusal is shown as it came, and nothing is listed
  await page.getByTestId('trust-app-public-key').fill(REFUSED_KEY)
  await page.getByTestId('trust-app-add').click()
  await expect(page.getByTestId('error-banner').first()).toContainText('this needs the admin role')
  await expect(page.getByTestId('trust-app-key')).toHaveCount(0)

  // a good key is trusted, its label is kept, and the form is empty again
  await page.getByTestId('trust-app-public-key').fill(` ${KEY} `)
  await page.getByTestId('trust-app-label').fill('ci of the sales team')
  await page.getByTestId('trust-app-add').click()
  await expect(page.getByTestId('trust-app-key')).toHaveCount(1)
  await expect(page.getByTestId('trust-app-key')).toContainText('ci of the sales team')
  await expect(page.getByTestId('trust-app-key')).toContainText('by Trust Tester, 2026-11-12')
  await expect(page.getByTestId('trust-app-public-key')).toHaveValue('')
  expect(mock.sent['PUT trust/app/keys']?.at(-1)).toEqual({ public_key: KEY, label: 'ci of the sales team' })
  // a key of one kind is not a key of another
  await expect(page.getByTestId('trust-dashboard-none')).toBeVisible()

  // rotation: add the new key, revoke the old one after being asked
  await page.getByTestId('trust-app-public-key').fill('B'.repeat(43))
  await page.getByTestId('trust-app-add').click()
  await expect(page.getByTestId('trust-app-key')).toHaveCount(2)
  await page.getByTestId('trust-app-revoke').first().click()
  await expect(page.getByTestId('trust-app-revoke-ask')).toContainText('versions it signed stay')
  await page.getByTestId('trust-app-revoke-cancel').click()
  await expect(page.getByTestId('trust-app-revoke-ask')).toHaveCount(0)
  expect(mock.sent['POST trust/app/' + 'a'.repeat(15) + '1/revoke']).toBeUndefined()
  await page.getByTestId('trust-app-revoke').first().click()
  await page.getByTestId('trust-app-revoke-go').click()
  await expect(page.getByTestId('trust-app-key').first()).toHaveAttribute('data-revoked', 'true')
  await expect(page.getByTestId('trust-app-key').first()).toContainText('revoked by Trust Tester')
  await expect(page.getByTestId('trust-app-revoke')).toHaveCount(1)   // only the live key has the button

  // the requirement of one kind
  await page.getByTestId('trust-dashboard-toggle').click()
  await expect(page.getByTestId('trust-dashboard-required')).toHaveAttribute('data-value', 'false')
  expect(mock.sent['PUT settings/signatures/dashboard']?.at(-1)).toEqual({ required: false })
  await expect(page.getByTestId('trust-app-required')).toHaveAttribute('data-value', 'true')

  // a test group makes signatures optional where an administrator has not said otherwise
  await page.getByTestId('trust-group-toggle').click()
  await expect(page.getByTestId('trust-group')).toContainText('test group')
  await expect(page.getByTestId('trust-app-required')).toHaveAttribute('data-value', 'false')
  expect(mock.sent['PUT settings/group']?.at(-1)).toEqual({ mode: 'test' })
  expect(outside).toEqual([])
}
