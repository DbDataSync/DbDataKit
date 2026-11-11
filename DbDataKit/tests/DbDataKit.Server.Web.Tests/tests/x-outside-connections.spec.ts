import { expect, test, type Page, type Route } from '@playwright/test'
import { signInNewAdmin } from './people'

// Plan 27a IX01: the console's connections screen shows the kind of each connection and edits the ones of outside systems (an API, a model provider) as text with the loader's diagnostics. The host side (the loader, the secret store, the canary) is
// `DbDataKit.Server.Tests` OutsideConnectionsTests and `DbDataKit.Connections.Abstractions.Tests` ConnectionKindTests; this story mocks the outside-connections API in the page, so it needs the signed-in console only. It invites a console administrator
// as `identity-admin.spec.ts` does, and its name sorts after `first-run` and `identity-home` (which spend the first-run invitation).
const json = (route: Route, body: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })

// the editor is Monaco: click into it, select everything and insert the text (inserted, not typed: typing lets the editor's suggestions finish the word)
async function replaceEditorText(page: Page, text: string) {
  await page.getByTestId('outside-yaml').locator('.monaco-editor').click()
  await page.keyboard.press('Control+A')
  await page.keyboard.insertText(text)
}

const CRM = 'schema: connection/2\nkind: http\nname: crm\nendpoint: https://api.example.com/v1\nsign_in:\n  method: api_key\n  key_secret: secret:dbdatakit:server:connection:crm:key\n'

test('an API connection is started from a template, a key written as itself is refused with its line, the file is saved and the typed key is kept as a reference', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Connection Tester', `connections-${Date.now()}@example.com`)
  let stored = false
  let saved: string | null = null
  const secretBodies: unknown[] = []
  const row = () => ({
    name: 'crm', file: 'connections/crm.yml', yaml: saved ?? CRM, kind: 'http', endpoint: 'https://api.example.com/v1', signIn: 'api_key', cloud: null, diagnostics: [],
    secrets: [{ field: 'key', path: 'sign_in.key_secret', reference: 'secret:dbdatakit:server:connection:crm:key', available: stored, problem: stored ? null : 'The secret `secret:dbdatakit:server:connection:crm:key` is not set. Type it here.' }],
  })
  await page.route('**/api/outside-connections', (route) => json(route, { kinds: ['http', 'model-provider'], connections: saved ? [row()] : [] }))
  await page.route('**/api/outside-connections/template*', (route) => json(route, { yaml: CRM }))
  await page.route('**/api/outside-connections/crm/check', (route) => json(route, { diagnostics: [] }))
  await page.route('**/api/outside-connections/crm/test', (route) => json(route, stored
    ? { ok: true, reached: true, signIn: 'accepted', message: 'https://api.example.com/v1 answered 200 and accepted the sign-in.', kind: null, temporary: false, status: 200, milliseconds: 12 }
    : { ok: false, reached: null, signIn: 'not checked', message: 'The secret `secret:dbdatakit:server:connection:crm:key` is not set. Type it on this screen.', kind: null, temporary: false, status: null, milliseconds: 0 }))
  await page.route('**/api/outside-connections/crm/secrets/key', (route) => { secretBodies.push(route.request().postDataJSON()); stored = true; return json(route, { stored: true, reference: 'secret:dbdatakit:server:connection:crm:key' }) })
  await page.route('**/api/outside-connections/crm', (route) => {
    const { yaml } = route.request().postDataJSON() as { yaml: string }
    // the key must be a reference (`key_secret: secret:...`); a bare `key_secret:` line contains the text `secret:` too, so the test is on what follows it
    if (!/key_secret:\s*(secret|env|file|keyring|dpapi):/.test(yaml)) {
      return json(route, { saved: false, diagnostics: [{ code: 'DBD-CONN-006', severity: 'error', file: 'connections/crm.yml', line: 8, column: 3, message: '`sign_in.key_secret` is not a secret reference: a key is never written in the file.', fix: 'Use secret:, env:, file:, keyring: or dpapi:.' }] }, 422)
    }
    saved = yaml
    return json(route, { saved: true, diagnostics: [] })
  })

  await page.goto('/connections')
  await expect(page.getByTestId('outside-connections')).toBeVisible()
  await expect(page.getByTestId('outside-none')).toBeVisible()

  // a template per kind; the new-connection form says what the kind is
  await page.getByTestId('outside-new-name').fill('crm')
  await expect(page.getByTestId('outside-new-meaning')).toContainText('HTTP API')
  await page.getByTestId('outside-new-kind').selectOption('model-provider')
  await expect(page.getByTestId('outside-new-meaning')).toContainText('model an agent can use')
  await page.getByTestId('outside-new-kind').selectOption('http')
  await page.getByTestId('outside-new-start').click()
  await expect(page.getByTestId('outside-editor')).toBeVisible()
  await expect(page.getByTestId('outside-yaml').locator('.monaco-editor')).toBeVisible()

  // a key written as itself is refused, with the line and the fix, and nothing is saved
  await replaceEditorText(page, 'schema: connection/2\nkind: http\nname: crm\nendpoint: https://api.example.com/v1\nsign_in:\n  method: api_key\n  key_secret: sk-typed-into-the-file\n')
  await page.getByTestId('outside-save').click()
  await expect(page.getByTestId('outside-diagnostic').first()).toContainText('line 8')
  await expect(page.getByTestId('outside-diagnostic').first()).toContainText('never written in the file')
  await expect(page.getByTestId('outside-none')).toBeVisible()

  // the reference is saved; the list says the kind, the address and the sign-in; the secret is not set until it is typed
  await replaceEditorText(page, CRM)
  await page.getByTestId('outside-save').click()
  await expect(page.getByTestId('outside-row')).toHaveAttribute('data-kind', 'http')
  await expect(page.getByTestId('outside-kind')).toHaveText('API (http)')
  await expect(page.getByTestId('outside-detail')).toContainText('https://api.example.com/v1, API key')
  await expect(page.getByTestId('outside-secret')).toHaveAttribute('data-available', 'false')
  await page.getByTestId('outside-test').click()
  await expect(page.getByTestId('outside-test-result')).toHaveAttribute('data-ok', 'false')
  await expect(page.getByTestId('outside-test-result')).toContainText('is not set')
  await page.getByTestId('outside-secret-input').fill('the-typed-key')
  await page.getByTestId('outside-secret-save').click()
  await expect(page.getByTestId('outside-secret')).toHaveAttribute('data-available', 'true')
  await page.getByTestId('outside-test').click()
  await expect(page.getByTestId('outside-test-result')).toHaveAttribute('data-ok', 'true')
  await expect(page.getByTestId('outside-test-result')).toHaveAttribute('data-signin', 'accepted')
  await expect(page.getByTestId('outside-test-result')).not.toContainText('the-typed-key')
  expect(secretBodies).toEqual([{ value: 'the-typed-key' }])
  expect(saved).not.toContain('the-typed-key')
})
