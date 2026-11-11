import { expect, test, type APIRequestContext, type BrowserContext, type Page } from '@playwright/test'
import { execFileSync } from 'node:child_process'
import fs from 'node:fs'
import path from 'node:path'
import { DatabaseSync } from 'node:sqlite'
import { fileURLToPath } from 'node:url'
import { runnerServer, scratchDirectory } from '../playwright.config'

// Plan 28a, R19: the console and a real runner. The console pairs with a `dbdatarunner` that serves the sample package, lists its instances (one of them gone) and tasks, runs a task from the browser, pauses and resumes it,
// lists the runs and shows one, shows the runner in the Monitoring page, and, when a publisher stages version 2 of the package (which resets a progress value the task has moved: a loss), shows the staging with its loss list and activates it
// after the loss is confirmed. Everything is asked of the runner through the console's relay, as the signed-in administrator.
const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../../..')
const consoleDll = path.join(repoRoot, 'DbDataKit/src/DbDataKit.Server/bin/Debug/net10.0/dbdatakit.dll')
const name = 'Runner e2e'
const goneHost = 'retired-box'

async function signInAsAdmin(page: Page, context: BrowserContext, baseURL: string) {
  const cdp = await context.newCDPSession(page)
  await cdp.send('WebAuthn.enable')
  await cdp.send('WebAuthn.addVirtualAuthenticator', {
    options: { protocol: 'ctap2', transport: 'internal', hasResidentKey: true, hasUserVerification: true, isUserVerified: true, automaticPresenceSimulation: true },
  })
  const made = execFileSync('dotnet', ['exec', consoleDll, 'user', 'invite', '--email', `runner-${Date.now()}-${Math.random().toString(36).slice(2, 8)}@example.com`, '--role', 'admin', '--url', baseURL, '--repo', scratchDirectory], { encoding: 'utf8' })
  await page.goto(`/invite#${/\/invite#(\S+)/.exec(made)![1]}`)
  await page.getByTestId('invite-name-input').fill('Runner Tester')
  await page.getByTestId('invite-enrol-button').click()
  await expect(page.getByTestId('home-page')).toBeVisible()
}

async function instanceId(request: APIRequestContext): Promise<string> {
  const list = await (await request.get('/api/instances')).json() as { id: string; name: string }[]
  return list.find((i) => i.name === name)!.id
}

const task = (page: Page, key: string) => page.locator(`[data-testid="runner-task"][data-task="${key}"]`)

test('the console pairs with a runner, runs, pauses and resumes a task, shows a gone instance, and stages and activates a package version with a loss', async ({ page, context, baseURL }) => {
  const request = context.request
  test.setTimeout(240_000)
  await signInAsAdmin(page, context, baseURL!)

  // a runner of the group that is gone: its row says so (a second live runner needs a server database; the row is what one leaves behind)
  const db = new DatabaseSync(path.join(runnerServer.directory, 'state.db'))
  try {
    const now = new Date().toISOString()
    db.prepare('INSERT INTO dbdatakit_runner_main_instance (id, runner, host, version, started_at, heartbeat_at, state) VALUES (?, ?, ?, ?, ?, ?, ?)')
      .run('2d1f0f7a-0000-4000-8000-000000000001', 'dbdatarunner', goneHost, '0.1.0.0', now, now, 'Gone')
  } finally { db.close() }

  // a publisher puts version 1 on the dev channel and serves it at once (a package is owned by the client that created it, so the same publisher stages version 2 below)
  const publish = (version: string, ...more: string[]) => execFileSync('dotnet', ['exec', runnerServer.dll, 'package', 'publish', path.join(runnerServer.manifests, version), runnerServer.package,
    '--server', runnerServer.url, '--channel', 'dev', '--client-dir', runnerServer.clientDirectory, ...more], { encoding: 'utf8' })
  publish('v1.yaml', '--code', fs.readFileSync(path.join(scratchDirectory, 'runner-publisher-code.txt'), 'utf8').trim())

  // ---- pair it like any server
  await page.goto('/instances')
  await page.getByTestId('pair-url-input').fill(runnerServer.url)
  await page.getByTestId('pair-code-input').fill(fs.readFileSync(path.join(scratchDirectory, 'runner-pairing-code.txt'), 'utf8').trim())
  await page.getByTestId('pair-name-input').fill(name)
  await page.getByTestId('pair-button').click()
  const row = page.getByTestId(`instance-row-${name}`)
  await expect(row.getByTestId('instance-status')).toHaveText('Ready', { timeout: 30_000 })
  const id = await instanceId(request)
  expect(((await (await request.get('/api/instances')).json()) as { id: string; product: string }[]).find((i) => i.id === id)!.product).toBe('dbdatarunner')
  await row.getByTestId('instance-open-link').click()

  // ---- the screen: the group's instances, live and gone
  await expect(page.getByTestId('runner-tasks-page')).toBeVisible()
  await expect(page.getByTestId('runner-instance')).toHaveCount(2)
  await expect(page.locator('[data-testid="runner-instance"][data-state="live"]')).toHaveCount(1)
  const gone = page.locator('[data-testid="runner-instance"][data-state="gone"]')
  await expect(gone).toContainText(goneHost)
  await expect(gone.getByTestId('runner-instance-state')).toContainText('Gone')
  const live = page.locator('[data-testid="runner-instance"][data-state="live"]')
  await expect(live.getByTestId('runner-instance-capabilities')).toContainText('network')
  await expect(live.getByTestId('runner-instance-labels')).toContainText('os:')

  // ---- the tasks of the package
  await expect(page.getByTestId('runner-task')).toHaveCount(2)
  const ok = task(page, 'sample/dev/ok')
  const tally = task(page, 'sample/dev/tally')
  // `ok` is scheduled every hour and runs once when the runner first sees it; `tally` has not run
  await expect(tally.getByTestId('runner-task-last')).toHaveText('No run yet')

  // run now, from the browser: twice for `tally`, so that its progress value `seen` has moved
  for (const times of [1, 2]) {
    await tally.getByTestId('runner-task-run').click()
    await expect(tally.getByTestId('runner-task-last')).toContainText('Succeeded', { timeout: 60_000 })
    await expect.poll(async () => (await (await request.get(`/relay/${id}/api/v1/runs?package=sample&task=tally&channel=dev&outcome=succeeded`)).json() as { runs: unknown[] }).runs.length, { timeout: 60_000 }).toBe(times)
  }
  await ok.getByTestId('runner-task-run').click()
  await expect(ok.getByTestId('runner-task-last')).toContainText('Succeeded', { timeout: 60_000 })

  // pause it, and resume it; the runner says it is held, and who held it
  await ok.getByTestId('runner-task-toggle').click()
  await expect(ok).toHaveAttribute('data-state', 'paused', { timeout: 30_000 })
  await expect(ok.getByTestId('runner-task-toggle')).toHaveText('Resume')
  await expect(ok.getByTestId('runner-task-note')).toContainText('Paused by Runner Tester')
  await ok.getByTestId('runner-task-toggle').click()
  await expect(ok).not.toHaveAttribute('data-state', 'paused', { timeout: 30_000 })
  await expect(ok.getByTestId('runner-task-toggle')).toHaveText('Pause')

  // ---- the runs, filtered, and one of them in detail
  await page.getByTestId('rail-runs').click()
  await expect(page.getByTestId('runner-runs-page')).toBeVisible()
  await expect.poll(() => page.getByTestId('runner-run').count()).toBeGreaterThanOrEqual(3)
  await page.getByTestId('runner-filter-outcome').selectOption('succeeded')
  await expect.poll(() => page.getByTestId('runner-run').count()).toBeGreaterThanOrEqual(3)
  await page.getByTestId('runner-filter-outcome').selectOption('failed')
  await expect(page.getByTestId('runner-no-runs')).toBeVisible()
  await page.getByTestId('runner-filter-outcome').selectOption('')
  await page.getByTestId('runner-filter-package').selectOption('sample')
  await page.getByTestId('runner-run').first().click()
  await expect(page.getByTestId('runner-run-detail')).toContainText('Succeeded')
  await expect(page.getByTestId('runner-run-package')).toContainText('sample v1')
  await page.getByTestId('runner-run-close').click()

  // ---- the console's Monitoring page lists the runner's tasks and the runner that is gone
  await page.goto('/monitoring')
  await expect(page.locator('[data-testid="monitoring-row"][data-state="gone"]')).toContainText(`runner on ${goneHost}`, { timeout: 30_000 })
  await expect(page.getByTestId('monitoring-rows')).toContainText('sample/dev/tally')

  // ---- a publisher stages version 2: the package page shows it ready, with the loss, and activates it once the loss is confirmed
  const published = publish('v2.yaml', '--stage')
  expect(published).toContain('sample')
  await page.goto(`/servers/${id}/packages`)
  await page.getByTestId('runner-package-link').click()
  const staging = page.getByTestId('runner-staging')
  await expect(staging).toBeVisible({ timeout: 30_000 })
  await expect(staging.getByTestId('runner-staging-state')).toHaveText('ready', { timeout: 60_000 })
  await expect(staging).toHaveAttribute('data-needs-confirmation', 'true')
  await expect(page.getByTestId('runner-staging-losses')).toContainText('seen')
  await expect(page.getByTestId('runner-channel-heads')).toContainText('v1 active')

  await page.getByTestId('runner-activate').click()
  const dialog = page.getByTestId('runner-activate-dialog')
  await expect(dialog.getByTestId('runner-activate-loss').first()).toContainText('seen')
  // the losses are echoed, and the button waits for the confirmation of them
  await expect(dialog.getByTestId('runner-activate-go')).toBeDisabled()
  await dialog.getByTestId('runner-activate-confirm').check()
  await expect(dialog.getByTestId('runner-activate-go')).toBeEnabled()
  await dialog.getByTestId('runner-activate-go').click()
  await expect(dialog).toBeHidden({ timeout: 60_000 })
  await expect(page.getByTestId('runner-channel-heads')).toContainText('v2 active', { timeout: 30_000 })
  await expect(page.getByTestId('runner-channel-heads')).toContainText('v1 previous')
  await expect(page.getByTestId('runner-staging')).toHaveCount(0)

  // the tasks now run version 2
  await page.getByTestId('rail-tasks').click()
  await expect(task(page, 'sample/dev/tally')).toContainText('2', { timeout: 30_000 })

  // and only the listed calls of the runner are reachable through the relay (plan 33, "The relay to a runner"):
  // the artifact upload is listed (admin or publisher), so it reaches the runner, which refuses this body itself (400, not the relay's 403)
  expect((await request.put(`/relay/${id}/api/v1/artifacts/${'0'.repeat(64)}`, { data: 'x' })).status()).toBe(400)
  // a call that is not listed is refused by the relay, in its words, and never reaches the runner
  for (const [method, path] of [['PUT', 'api/v1/status'], ['DELETE', 'api/v1/artifacts/' + '0'.repeat(64)], ['POST', 'api/v1/artifacts/' + '0'.repeat(64)], ['PUT', 'api/v1/settings/other']] as const) {
    const refused = await request.fetch(`/relay/${id}/${path}`, { method, data: 'x' })
    expect(refused.status(), `${method} ${path}`).toBe(403)
    expect(await refused.text()).toContain('relays only its management calls')
  }
})
