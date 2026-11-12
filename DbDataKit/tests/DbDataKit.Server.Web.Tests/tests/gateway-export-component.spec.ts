import { expect, test, type Route } from '@playwright/test'
import { signInNewAdmin } from './people'
import { mockSettings } from './gateway-settings'

// Plan 32: the 'PDF export component' card of the Gateway's server settings screen (`GET /api/v1/views/export`, `POST .../install`), with the Gateway's relay mocked in the page beside the settings mock (the Gateway side is DbDataGateway.Tests'
// export component tests). The card says the component is missing and offers Install; a refused install is shown in the Gateway's words and Install stays; a running install shows the step and the bytes and no button; a finished one says the Gateway
// must restart and shows the release it holds. It signs in a new administrator, so it sorts after `identity-home` and `first-run`.
const json = (route: Route, body: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })
const SERVER = 'gw-1'
const idle = { state: 'idle', step: '', bytes: 0, total: 0, message: null as string | null, restart_needed: false }
const missing = { platform: 'linux-x64', supported: true, installed: false, whole: false, directory: '/var/lib/dbdatakit/gateway/export-component', release: null, chromium: null, fonts: null, mode: null, configured: false, problems: [] as string[], summary: 'PDF export is not installed.', install: idle }

test('the PDF export component card says it is missing, shows a refused install, the progress of a running one and the restart a finished one needs', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Export Tester', `export-component-${Date.now()}@example.com`)
  await mockSettings(page)
  let state: any = missing
  const installs: unknown[] = []
  let next: 'refuse' | 'run' | 'finish' = 'refuse'
  await page.route(`**/relay/${SERVER}/api/v1/views/export`, (route) => json(route, state))
  await page.route(`**/relay/${SERVER}/api/v1/views/export/install`, (route) => {
    installs.push(route.request().postDataJSON())
    if (next === 'refuse') {
      state = { ...missing, install: { ...idle, state: 'failed', message: 'dbdatagateway-export-v1-linux-x64.zip does not match SHA256SUMS: its SHA-256 is aa and the release says bb. Nothing was installed.' } }
    } else if (next === 'run') {
      state = { ...missing, install: { ...idle, state: 'running', step: 'downloading dbdatagateway-export-v1-linux-x64.zip', bytes: 100 * 1024 * 1024, total: 400 * 1024 * 1024 } }
    } else {
      state = { ...missing, installed: true, whole: true, configured: true, release: '1', chromium: '156.0', fonts: '2', mode: 'pinned', summary: 'PDF export is installed.', install: { ...idle, state: 'done', message: 'Release 1 is installed. Restart the gateway to use it.', restart_needed: true } }
    }
    return json(route, state)
  })

  await page.goto(`/servers/${SERVER}/settings`)
  const card = page.getByTestId('export-component')
  await expect(card).toBeVisible()
  await expect(card).toHaveAttribute('data-state', 'missing')
  await expect(card.getByTestId('export-summary')).toHaveText('PDF export is not installed.')
  await expect(card.getByTestId('export-install')).toHaveText('Install')
  await expect(card.getByTestId('export-install')).toBeEnabled()

  // a refused install: the Gateway's words, nothing installed, the button is still there
  await card.getByTestId('export-install').click()
  await expect(card.getByTestId('export-failed')).toContainText('does not match SHA256SUMS')
  await expect(card.getByTestId('export-install')).toBeVisible()
  expect(installs).toEqual([{ force: false }])

  // a running install: the step and the megabytes, no button; the card polls and ends where the Gateway ends
  next = 'run'
  await card.getByTestId('export-install').click()
  await expect(card).toHaveAttribute('data-state', 'running')
  await expect(card.getByTestId('export-progress')).toContainText('downloading dbdatagateway-export-v1-linux-x64.zip')
  await expect(card.getByTestId('export-progress')).toContainText('100 MB of 400 MB')
  await expect(card.getByTestId('export-install')).toHaveCount(0)
  state = { ...state, install: { ...state.install, bytes: 300 * 1024 * 1024 } }
  await expect(card.getByTestId('export-progress')).toContainText('300 MB of 400 MB')
  next = 'finish'
  state = { ...missing, installed: true, whole: true, configured: true, release: '1', chromium: '156.0', fonts: '2', mode: 'pinned', summary: 'PDF export is installed.', install: { ...idle, state: 'done', message: 'Release 1 is installed. Restart the gateway to use it.', restart_needed: true } }
  await expect(card).toHaveAttribute('data-state', 'ready')
  await expect(card.getByTestId('export-done')).toHaveAttribute('data-restart', 'true')
  await expect(card.getByTestId('export-done')).toContainText('Restart the gateway')
  await expect(card.getByTestId('export-install')).toHaveCount(0)
})
