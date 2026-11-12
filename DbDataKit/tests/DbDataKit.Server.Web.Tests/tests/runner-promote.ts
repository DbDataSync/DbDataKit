import { expect, type Page, type Route } from '@playwright/test'
import { mockRunner, RUNNER } from './runner-editors'

// Plan 30, PB06: the Promote action of the runner's Packages screen, with the runner's relay routes (`mockRunner`) and the console's own two calls mocked in the page: the list of servers (`GET api/instances`) and the promotion (`POST api/promotions`), which
// the story answers as the console does (the console side is ConsolePromotionTests and EmbeddedRunnerTests, the library's PromotionTests). The story checks what the screen SENDS as well as what it shows.

const json = (route: Route, body: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })
const server = (id: string, name: string) => ({ id, name, url: 'https://runner.invalid', product: 'dbdatarunner', version: '1', apiVersion: 1, reachable: true, ready: true, problem: null, pairedAt: null })

export async function promoteStory(page: Page) {
  await mockRunner(page)
  const sent: Record<string, unknown>[] = []
  let refuse: string | null = null
  // the console's list of servers: this runner, another one that answers and one that does not (it is not offered)
  await page.route('**/api/instances', (route) => route.request().method() === 'GET'
    ? json(route, [server(RUNNER, 'Test runner'), server('runner-2', 'Prod runner'), { ...server('runner-3', 'Down runner'), reachable: false, ready: false, problem: 'no answer' }])
    : route.fallback())
  await page.route('**/api/promotions', async (route) => {
    const body = route.request().postDataJSON() as Record<string, unknown>
    sent.push(body)
    if (refuse) return json(route, { error: refuse, code: 'refused' }, 409)
    return json(route, { kind: 'integration', name: body.name, fromChannel: body.fromChannel, toChannel: body.toChannel, sourceVersion: 1, targetVersion: 3, changed: true, mode: body.mode ?? 'stage', signature: 'unsigned', sent: sent.length === 1 ? 2 : 0, skipped: sent.length === 1 ? 1 : 3, bound: Object.keys((body.bindings as object | undefined) ?? {}) })
  })

  await page.goto(`/servers/${RUNNER}/packages/orders`)
  await expect(page.getByTestId('runner-package')).toBeVisible()

  // the action is on a channel that serves a version; the dialog offers the other runners that answer, not this one nor the one that is down
  await page.locator('[data-testid="runner-channel"][data-channel="test"]').getByTestId('runner-promote').click()
  const dialog = page.getByTestId('runner-promote-dialog')
  await expect(dialog).toBeVisible()
  await expect(dialog.getByTestId('runner-promote-target').locator('option')).toHaveText(['Choose...', 'Prod runner'])
  await expect(dialog.getByTestId('runner-promote-go')).toBeDisabled()
  await expect(dialog.getByTestId('runner-promote-why')).toHaveText('Choose the runner it goes to.')

  await dialog.getByTestId('runner-promote-target').selectOption('runner-2')
  await dialog.getByTestId('runner-promote-to-channel').fill('prod')
  await dialog.getByTestId('runner-promote-bindings').fill('source')
  await expect(dialog.getByTestId('runner-promote-bindings-problem')).toContainText('"source" is not slot=connection.')
  await expect(dialog.getByTestId('runner-promote-go')).toBeDisabled()
  await dialog.getByTestId('runner-promote-bindings').fill('source=erp-prod\n')
  await dialog.getByTestId('runner-promote-mode').selectOption('stage')
  await dialog.getByTestId('runner-promote-go').click()

  await expect(dialog.getByTestId('runner-promote-done')).toContainText('Version 1 of orders: Prod runner now holds version 3 in prod (stage). 2 files sent, 1 already there.')
  expect(sent).toEqual([{ from: RUNNER, to: 'runner-2', name: 'orders', fromChannel: 'test', toChannel: 'prod', version: 1, mode: 'stage', bindings: { source: 'erp-prod' } }])

  // a second promotion of the same version sends no file; a refusal of the target is shown in its words and nothing says it worked
  await dialog.getByTestId('runner-promote-go').click()
  await expect(dialog.getByTestId('runner-promote-done')).toContainText('No file was sent')
  refuse = "Prod runner refused the slot 'source' of orders in prod (409): not bound."
  await dialog.getByTestId('runner-promote-to-channel').fill('qa')
  await expect(dialog.getByTestId('runner-promote-done')).toHaveCount(0)
  await dialog.getByTestId('runner-promote-go').click()
  await expect(dialog).toContainText("Prod runner refused the slot 'source' of orders in prod (409): not bound.")
  await expect(dialog.getByTestId('runner-promote-done')).toHaveCount(0)
  await dialog.getByTestId('runner-promote-close').click()
  await expect(dialog).toHaveCount(0)

  // a version of the list is promoted from its row: the version is chosen already
  await page.locator('[data-testid="runner-version"][data-version="2"]').getByTestId('runner-promote-version-button').click()
  await expect(page.getByTestId('runner-promote-version')).toHaveValue('2')
}
