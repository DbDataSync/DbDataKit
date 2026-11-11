import { expect, type Page, type Route } from '@playwright/test'

// Plan 33, UE17: the Gateway's server settings screen (server.yaml as a form and as text, checked by the Gateway's validator as it changes, saved as a commit, with the restart said) against a Gateway whose relay is mocked in
// the page (the Gateway side, with the real validator, the masking, the version, the commit and the audit, is DbDataGateway.Tests' ManagementTests). The mock keeps the file and every body the screen sent. Its check and edit are a
// toy over the few lines this story uses (a second implementation, which is why the real ones are tested on the real host); what the story shows is the screen.

const SERVER = 'gw-1'
const json = (route: Route, body: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })
const SECRET = 'SENTINEL-tls-pw-4e2a'
const COMMIT = '5f3c9a1b7d2e4a60918273645f0e1d2c3b4a5968'

export interface SettingsMock {
  sent: Record<string, any[]>
  /** The file as the Gateway holds it, with the real secret. */
  file: () => string
  /** The next write is answered as if the file had changed since it was read. */
  refuseNextAsStale: () => void
}

export async function mockSettings(page: Page): Promise<SettingsMock> {
  let file = `schemaVersion: 2\ninstanceId: gw-instance-1\ntls: { mode: selfSigned, certificatePassword: ${SECRET} }\nmodules: [flightsql]\nviews:\n  cacheSeconds: 30\nlogSql: true\n`
  let version = 1
  let stale = false
  const sent: Record<string, any[]> = {}
  const masked = (text: string) => text.replace(`certificatePassword: ${SECRET}`, "certificatePassword: '********'")
  const restore = (text: string) => text.replace("certificatePassword: '********'", `certificatePassword: ${SECRET}`)
  const fieldsOf = (text: string) => ({
    modules: /^modules: \[(.*)\]/m.exec(text)?.[1]?.split(',').map((m) => m.trim()).filter(Boolean) ?? [],
    'views.cacheSeconds': Number(/cacheSeconds: (\d+)/.exec(text)?.[1] ?? 30), logSql: !/logSql: false/.test(text),
  })
  const topKeys = (text: string) => Object.fromEntries(text.split('\n').filter((l) => /^[A-Za-z]/.test(l)).map((l) => [/^([A-Za-z]+)/.exec(l)![1], l]))
  const changed = (before: string, after: string) => {
    const a = topKeys(before), b = topKeys(after)
    // the views block spans two lines: compare its cache line with the others by the key it holds
    a.views = /cacheSeconds: (\d+)/.exec(before)?.[0] ?? ''; b.views = /cacheSeconds: (\d+)/.exec(after)?.[0] ?? ''
    return [...new Set([...Object.keys(a), ...Object.keys(b)])].filter((k) => a[k] !== b[k])
  }
  const diagnosticsOf = (text: string) => {
    const lines = text.split('\n')
    const at = lines.findIndex((l) => /cacheSeconds: (\d+)/.test(l))
    const n = Number(/cacheSeconds: (\d+)/.exec(text)?.[1] ?? 0)
    return n > 3600 ? [{ code: 'DBP-110', severity: 'error', key: 'Views.CacheSeconds', file: '/data/config/server.yaml', line: at + 1, column: 3, message: `Value ${n} is above the maximum 3600.`, fix: 'Use 3600 or less.' }] : []
  }
  await page.route('**/api/instances', (route) => route.request().method() === 'GET'
    ? json(route, [{ id: SERVER, name: 'Settings gateway', url: 'https://gateway.invalid', product: 'dbdatagateway', version: '1', apiVersion: 1, reachable: true, ready: true, problem: null, pairedAt: null }])
    : route.fallback())
  await page.route(`**/relay/${SERVER}/api/v1/**`, async (route) => {
    const request = route.request()
    const method = request.method()
    const tail = new URL(request.url()).pathname.slice(`/relay/${SERVER}/api/v1/`.length)
    const body = method === 'POST' || method === 'PUT' ? request.postDataJSON() as Record<string, any> : undefined
    if (method !== 'GET') (sent[`${method} ${tail}`] ??= []).push(body)
    if (tail === 'config/server.yaml' && method === 'GET') return json(route, { text: masked(file), version: `v${version}`, masked: true, protected: ['instanceId', 'pairedClients'], restart_keys: ['modules', 'views', 'tls'] })
    if (tail === 'config/server.yaml/check') {
      const text = restore(body!.text)
      const diagnostics = diagnosticsOf(text)
      const keys = changed(file, text)
      return json(route, { valid: diagnostics.length === 0, diagnostics, fields: fieldsOf(text), changed: keys, restart_keys: keys.filter((k) => ['modules', 'views', 'tls'].includes(k)), problems: [] })
    }
    if (tail === 'config/server.yaml/edit') {
      let text: string = body!.text
      for (const [path, items] of Object.entries<string[]>(body!.list ?? {})) if (path === 'modules') text = text.replace(/^modules: \[.*\]/m, `modules: [${items.join(', ')}]`)
      for (const [path, value] of Object.entries<any>(body!.set ?? {})) {
        if (path === 'views.cacheSeconds') text = text.replace(/cacheSeconds: \d+/, `cacheSeconds: ${value}`)
        if (path === 'logSql') text = text.replace(/^logSql: .*/m, `logSql: ${value}`)
      }
      return json(route, { text })
    }
    if (tail === 'config/server.yaml' && method === 'PUT') {
      if (stale) { stale = false; version++; file += '# edited by hand\n'; return json(route, { error: 'server.yaml changed since it was read; read it again.', code: 'version_conflict', current_version: `v${version}` }, 409) }
      if (body!.expected_version !== `v${version}`) return json(route, { error: 'server.yaml changed since it was read; read it again.', code: 'version_conflict', current_version: `v${version}` }, 409)
      const text = restore(body!.text)
      const diagnostics = diagnosticsOf(text)
      if (diagnostics.length > 0) return json(route, { error: 'The change is not valid, so nothing was written.', detail: { valid: false, diagnostics } }, 400)
      const keys = changed(file, text)
      file = text
      if (keys.length > 0) version++
      const restart = keys.filter((k) => ['modules', 'views', 'tls'].includes(k))
      return json(route, { version: `v${version}`, changed: keys, restart_keys: restart, restart_needed: restart.length > 0, commit: keys.length > 0 ? COMMIT : null, diagnostics: [] })
    }
    if (tail === `config/history/${COMMIT}`) return json(route, { sha: COMMIT, message: 'Edit server.yaml (modules): add the views module', truncated: false, changes: [{ path: 'server.yaml', kind: 'modified', before: masked('modules: [flightsql]'), after: 'modules: [flightsql, views]' }] })
    if (tail === 'config/history') return json(route, { commits: [] })
    return json(route, { error: 'There is no such call.' }, 404)
  })
  return { sent, file: () => file, refuseNextAsStale: () => { stale = true } }
}

/**
 * UE17: the settings are read with the secret masked; a change in the form is made in the text by the Gateway (the form and the text are one document) and checked a moment later; a restart is said before saving and after; an error is shown
 * beside its field and in the text view with its line; Save says why it is off; the save is the masked text and the version read, with the note; the commit is one click away and shows the change; a file changed by someone else is refused with
 * the draft kept.
 */
export async function settingsStory(page: Page) {
  const outside: string[] = []
  const origin = new URL(page.url()).origin
  page.on('request', (request) => { if (/^https?:/.test(request.url()) && !request.url().startsWith(origin)) outside.push(request.url()) })
  const mock = await mockSettings(page)

  await page.goto(`/servers/${SERVER}/settings`)
  await expect(page.getByTestId('gateway-settings')).toBeVisible()
  await expect(page.getByTestId('settings-masked-note')).toBeVisible()
  await expect(page.getByTestId('settings-module-flightsql')).toBeChecked()
  await expect(page.getByTestId('settings-module-views')).not.toBeChecked()
  await expect(page.getByTestId('settings-cache-seconds')).toHaveValue('30')
  await expect(page.getByTestId('settings-save')).toBeDisabled()
  await expect(page.getByTestId('settings-save-why')).toHaveText('Nothing is changed yet.')
  // the secret is never on the page: the text shows the mask
  await page.getByTestId('settings-tab-yaml').click()
  await expect(page.getByTestId('settings-yaml')).toHaveValue(/certificatePassword: '\*{8}'/)
  expect(await page.content()).not.toContain(SECRET)
  await page.getByTestId('settings-tab-form').click()

  // a module switched in the form is a change of the text, said to need a restart before anything is saved
  await page.getByTestId('settings-module-views').click()   // a click, not check(): the box follows the draft the Gateway edits, a moment later
  await expect(page.getByTestId('settings-module-views')).toBeChecked()
  await expect(page.getByTestId('settings-restart-draft')).toContainText('modules is read when the Gateway starts')
  await expect(page.getByTestId('settings-save')).toBeEnabled()
  await page.getByTestId('settings-tab-yaml').click()
  await expect(page.getByTestId('settings-yaml')).toHaveValue(/modules: \[flightsql, views\]/)

  // a value out of range typed in the text: the Gateway's diagnostic, with its line, beside the field in the form; Save says why it is off
  const broken = (await page.getByTestId('settings-yaml').inputValue()).replace('cacheSeconds: 30', 'cacheSeconds: 99999')
  await page.getByTestId('settings-yaml').fill(broken)
  await expect(page.getByTestId('settings-save-why')).toHaveText('Fix the 1 problem first.')
  await expect(page.getByTestId('settings-save')).toBeDisabled()
  await page.getByTestId('settings-tab-form').click()
  await expect(page.getByTestId('settings-error-views.cacheSeconds')).toContainText('above the maximum 3600')
  await expect(page.getByTestId('settings-error-views.cacheSeconds')).toContainText('server.yaml:6')
  // a number the field itself refuses is said without asking, and nothing is sent for it
  const edits = (mock.sent['POST config/server.yaml/edit'] ?? []).length
  await page.getByTestId('settings-cache-seconds').fill('abc')
  await expect(page.getByTestId('settings-cache-problem')).toContainText('A whole number of seconds')
  expect((mock.sent['POST config/server.yaml/edit'] ?? []).length).toBe(edits)
  await page.getByTestId('settings-cache-seconds').fill('60')
  await expect(page.getByTestId('settings-error-views.cacheSeconds')).toHaveCount(0)
  await expect(page.getByTestId('settings-cache-problem')).toHaveCount(0)
  await expect(page.getByTestId('settings-save')).toBeEnabled()

  // save: the masked text and the version read go, with the note; the answer names the commit and the restart
  await page.getByTestId('settings-note').fill('add the views module')
  await page.getByTestId('settings-save').click()
  await expect(page.getByTestId('settings-saved')).toContainText('Saved: modules, views.')
  await expect(page.getByTestId('settings-saved-restart')).toHaveAttribute('data-restart', 'true')
  await expect(page.getByTestId('settings-saved-restart')).toContainText('restart the Gateway for them to take effect')
  const put = mock.sent['PUT config/server.yaml']!.at(-1)
  expect(put.expected_version).toBe('v1')
  expect(put.note).toBe('add the views module')
  expect(put.text).toContain("certificatePassword: '********'")
  expect(put.text).not.toContain(SECRET)
  expect(mock.file()).toContain(`certificatePassword: ${SECRET}`)
  expect(mock.file()).toContain('modules: [flightsql, views]')
  await expect(page.getByTestId('settings-save')).toBeDisabled()
  await expect(page.getByTestId('settings-save-why')).toHaveText('Nothing is changed yet.')
  expect(await page.content()).not.toContain(SECRET)

  // the commit is one click away and shows the change, with the secret still masked
  await page.getByTestId('settings-saved-commit').click()
  await expect(page.getByTestId('gateway-config-commit')).toBeVisible()
  await expect(page.getByTestId('change-server.yaml')).toContainText('modules: [flightsql, views]')
  expect(await page.content()).not.toContain(SECRET)

  // the file changed under the person (by hand, say): the save is refused, the draft is kept, and reading again drops it
  await page.goto(`/servers/${SERVER}/settings`)
  await expect(page.getByTestId('settings-module-views')).toBeChecked()
  mock.refuseNextAsStale()
  await page.getByTestId('settings-log-sql').click()
  await expect(page.getByTestId('settings-log-sql')).not.toBeChecked()
  await expect(page.getByTestId('settings-save')).toBeEnabled()
  await page.getByTestId('settings-save').click()
  await expect(page.getByTestId('settings-conflict')).toContainText('changed since you read it')
  await expect(page.getByTestId('settings-log-sql')).not.toBeChecked()
  await page.getByTestId('settings-reload').click()
  await expect(page.getByTestId('settings-conflict')).toHaveCount(0)
  await expect(page.getByTestId('settings-log-sql')).toBeChecked()
  await expect(page.getByTestId('settings-save')).toBeDisabled()

  // a change that needs no restart says so
  await page.getByTestId('settings-log-sql').click()
  await expect(page.getByTestId('settings-log-sql')).not.toBeChecked()
  await expect(page.getByTestId('settings-save')).toBeEnabled()
  await page.getByTestId('settings-save').click()
  await expect(page.getByTestId('settings-saved-restart')).toHaveAttribute('data-restart', 'false')
  await expect(page.getByTestId('settings-saved-restart')).toContainText('nothing here needs a restart')

  expect(outside).toEqual([])
}
