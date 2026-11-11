import { expect, type Page, type Route } from '@playwright/test'

// Plan 33, UE16: the Gateway's Secrets screen, and the references the data source editor shows, against a Gateway whose relay is mocked in the page (the Gateway side, with the roles, the store and the audit, is DbDataGateway.Tests'
// ManagementTests). The mock keeps the secrets (name to value, the value only to answer the data source test: a secret is never sent back) and every body the screen sent. What the mock does NOT do is encrypt: that is the Gateway's store.

const SERVER = 'gw-1'
const OWNER = 'managed-console-1'
const json = (route: Route, body: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })
const reference = (name: string) => `secret:dbdata:gateway:${OWNER}:${name}`

export interface SecretsMock { sent: Record<string, unknown[]>; secrets: Map<string, string>; source: { text: string; version: number } }

export async function mockGateway(page: Page): Promise<SecretsMock> {
  const mock: SecretsMock = { sent: {}, secrets: new Map(), source: { text: 'schema: connection/1\nname: erp\nengine: sqlserver\nconnection_string: Server=erp.example;Encrypt=True\nauth: { mode: sql, user: reader }\n', version: 1 } }
  await page.route('**/api/instances', (route) => route.request().method() === 'GET'
    ? json(route, [{ id: SERVER, name: 'Secrets gateway', url: 'https://gateway.invalid', product: 'dbdatagateway', version: '1', apiVersion: 1, reachable: true, ready: true, problem: null, pairedAt: null }])
    : route.fallback())
  await page.route(`**/relay/${SERVER}/api/v1/**`, async (route) => {
    const request = route.request()
    const method = request.method()
    const url = new URL(request.url())
    const tail = decodeURIComponent(url.pathname.slice(`/relay/${SERVER}/api/v1/`.length))
    const body = method === 'PUT' || method === 'POST' ? request.postDataJSON() as Record<string, any> : undefined
    if (method !== 'GET') (mock.sent[`${method} ${tail}`] ??= []).push(body ?? null)
    if (tail === 'secrets' && method === 'GET') {
      const names = [...mock.secrets.keys()].sort()
      return json(route, { secrets: names, items: names.map((name) => ({ name, reference: reference(name) })) })
    }
    if (tail.startsWith('secrets/')) {
      const name = tail.slice('secrets/'.length)
      if (method === 'PUT') {
        if (!/^[A-Za-z][A-Za-z0-9_.-]{0,63}$/.test(name)) return json(route, { error: "A secret name is a letter followed by letters, digits, '.', '_' or '-' (up to 64)." }, 400)
        if (name === 'locked') return json(route, { error: 'This call needs the admin role.' }, 403)
        const replaced = mock.secrets.has(name)
        mock.secrets.set(name, body!.value)
        return json(route, { name, reference: reference(name), replaced })
      }
      if (method === 'DELETE') { mock.secrets.delete(name); return json(route, { name, removed: true }) }
    }
    if (tail === 'data-sources' && method === 'GET') return json(route, { data_sources: [{ name: 'erp', version: String(mock.source.version), valid: true, engine: 'sqlserver', access: 'read', managed_by: null }] })
    if (tail === 'clients') return json(route, { clients: [] })
    if (tail === 'data-sources/erp' && method === 'GET') return json(route, { name: 'erp', text: mock.source.text, version: String(mock.source.version) })
    if (tail === 'data-sources/erp' && method === 'PUT') {
      mock.source = { text: body!.text, version: mock.source.version + 1 }
      return json(route, { name: 'erp', version: String(mock.source.version), created: false, diagnostics: [] })
    }
    if (tail === 'data-sources/erp/test' && method === 'POST') {
      // the Gateway resolves the reference in the file; a name it does not keep cannot connect
      const named = /password_secret: '([^']+)'/.exec(mock.source.text)?.[1]
      const found = [...mock.secrets.keys()].some((n) => reference(n) === named)
      return json(route, found ? { ok: true, elapsed_ms: 12 } : { ok: false, message: named ? `Secret '${named}' was not found by any configured provider.` : 'The login has no password.' })
    }
    return json(route, { error: 'There is no such call.' }, 404)
  })
  return mock
}

const VALUE = 'SENTINEL-erp-reader-pw-7c41'

/**
 * UE16: a secret is set from the screen (the form says what is missing; the value field is a password field and is empty once sent), listed by name with the reference to write, never shown; the data source editor shows the same reference;
 * the file is changed to name it and the test finds the secret; a refusal is shown in the Gateway's words; replacing says so; removing asks first and the test then says the secret is gone.
 */
export async function secretsStory(page: Page) {
  const outside: string[] = []
  const origin = new URL(page.url()).origin
  page.on('request', (request) => { if (/^https?:/.test(request.url()) && !request.url().startsWith(origin)) outside.push(request.url()) })
  const mock = await mockGateway(page)

  await page.goto(`/servers/${SERVER}/secrets`)
  await expect(page.getByTestId('gateway-secrets')).toBeVisible()
  await expect(page.getByTestId('secrets-empty')).toBeVisible()
  await expect(page.getByTestId('secret-set')).toBeDisabled()
  await expect(page.getByTestId('secret-why')).toHaveText('Name the secret.')
  await expect(page.getByTestId('secret-value')).toHaveAttribute('type', 'password')

  // a name the Gateway would refuse is said before anything is sent
  await page.getByTestId('secret-name').fill('9lives')
  await expect(page.getByTestId('secret-why')).toContainText('A secret name is a letter')
  await expect(page.getByTestId('secret-set')).toBeDisabled()
  await page.getByTestId('secret-name').fill('erp-login')
  await expect(page.getByTestId('secret-why')).toHaveText('Type the value to keep.')
  await page.getByTestId('secret-value').fill(VALUE)
  await expect(page.getByTestId('secret-set')).toBeEnabled()
  await page.getByTestId('secret-set').click()

  // kept: the reference to write is shown, the value is gone from the form and from the page, and the list has the name only
  await expect(page.getByTestId('secret-written-reference')).toHaveText(`password_secret: '${`secret:dbdata:gateway:managed-console-1:erp-login`}'`)
  await expect(page.getByTestId('secret-erp-login')).toBeVisible()
  await expect(page.getByTestId('secret-value')).toHaveValue('')
  await expect(page.getByTestId('secret-name')).toHaveValue('')
  expect(await page.content()).not.toContain(VALUE)
  expect(mock.sent['PUT secrets/erp-login']).toEqual([{ value: VALUE }])

  // a refusal in the Gateway's words, and no value is kept in the form after it either
  await page.getByTestId('secret-name').fill('locked')
  await page.getByTestId('secret-value').fill(VALUE)
  await page.getByTestId('secret-set').click()
  await expect(page.getByTestId('secret-refused')).toContainText('needs the admin role')
  await expect(page.getByTestId('secret-value')).toHaveValue('')
  await expect(page.getByTestId('secret-name')).toHaveValue('locked')
  expect(await page.content()).not.toContain(VALUE)

  // the data source editor shows the reference to write; the file names it; the test finds the secret
  await page.goto(`/servers/${SERVER}/data-sources/erp`)
  await expect(page.getByTestId('data-source-secret-erp-login')).toContainText("password_secret: 'secret:dbdata:gateway:managed-console-1:erp-login'")
  await expect(page.getByTestId('data-source-secrets-link')).toBeVisible()
  await page.getByTestId('data-source-test').click()
  await expect(page.getByTestId('data-source-test-result')).toHaveAttribute('data-ok', 'false')
  await expect(page.getByTestId('data-source-test-result')).toContainText('The login has no password')
  const text = mock.source.text.replace('auth: { mode: sql, user: reader }', `auth: { mode: sql, user: reader, password_secret: 'secret:dbdata:gateway:${OWNER}:erp-login' }`)
  await page.getByTestId('data-source-text').fill(text)
  await page.getByTestId('data-source-save').click()
  await expect(page.getByTestId('data-source-notice')).toContainText('Saved')
  await page.getByTestId('data-source-test').click()
  await expect(page.getByTestId('data-source-test-result')).toHaveAttribute('data-ok', 'true')
  expect(await page.content()).not.toContain(VALUE)

  // replacing says so (the button offers Replace for a name that is kept, and the old value is never shown)
  await page.goto(`/servers/${SERVER}/secrets`)
  await page.getByTestId('secret-name').fill('erp-login')
  await expect(page.getByTestId('secret-set')).toHaveText('Replace')
  await page.getByTestId('secret-value').fill('another-value-1234')
  await page.getByTestId('secret-set').click()
  await expect(page.getByTestId('secret-written')).toContainText('was replaced')
  expect(mock.secrets.get('erp-login')).toBe('another-value-1234')

  // remove: asked first (Keep changes nothing), then removed, and the data source test says the secret is gone
  await page.getByTestId('secret-remove-erp-login').click()
  await page.getByTestId('secret-remove-cancel').click()
  expect(mock.secrets.has('erp-login')).toBe(true)
  await page.getByTestId('secret-remove-erp-login').click()
  await expect(page.getByTestId('secret-remove-ask')).toContainText('cannot connect once it is removed')
  await page.getByTestId('secret-remove-confirm').click()
  await expect(page.getByTestId('secrets-empty')).toBeVisible()
  expect(mock.secrets.size).toBe(0)
  await page.goto(`/servers/${SERVER}/data-sources/erp`)
  await page.getByTestId('data-source-test').click()
  await expect(page.getByTestId('data-source-test-result')).toContainText('was not found')

  expect(outside).toEqual([])
}
