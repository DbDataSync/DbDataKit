import { expect, test, type APIRequestContext, type Page } from '@playwright/test'
import { execFileSync } from 'node:child_process'
import fs from 'node:fs'
import path from 'node:path'
import { publishBuild, publishRunner, scratchDirectory } from '../playwright.config'
import { signInNewAdmin } from './people'

// Plan 33, UE05: a person publishes a Build project to a runner from Build's Publish tab. The console pairs with a Build server (which hosts the project `orders`, one plan scheduled every hour) and with a runner; the Build server is paired with
// the runner as a publisher by the runner's own command (the Build server reads its pairings from the folder it was started with). The story then reads the list of paired runners on the Publish tab, publishes, sees the staged version and its task
// with the schedule, publishes again to see that nothing changed, and finds the package staged on the Runner screens. Publishing without a binding for the project's one slot is refused by the runner (reproduced against a real runner), so the story shows that refusal, binds `sqlserver=erp` and publishes. Not in the story: activation. A Build package needs the `dbdatabuild-runtime` package on the runner and this runner has none, so the version stays staged.
// It signs in a new administrator (an invitation), so it sorts after identity-home (see docs/ci-lessons.md, Playwright).
const buildName = 'Publish build'
const runnerName = 'Publish runner'
const pairingName = 'publish-runner'

const code = (file: string) => fs.readFileSync(path.join(scratchDirectory, file), 'utf8').trim()

async function pair(page: Page, url: string, pairingCode: string, name: string) {
  await page.goto('/instances')
  await page.getByTestId('pair-url-input').fill(url)
  await page.getByTestId('pair-code-input').fill(pairingCode)
  await page.getByTestId('pair-name-input').fill(name)
  await page.getByTestId('pair-button').click()
  await expect(page.getByTestId(`instance-row-${name}`).getByTestId('instance-status')).toHaveText('Ready', { timeout: 30_000 })
}

async function idOf(request: APIRequestContext, name: string): Promise<string> {
  return ((await (await request.get('/api/instances')).json()) as { id: string; name: string }[]).find((i) => i.name === name)!.id
}

test('a Build project is published to a paired runner from the Publish tab, and the staged version and its task are shown', async ({ page, context, baseURL }) => {
  const request = context.request
  test.setTimeout(240_000)
  await signInNewAdmin(page, context, baseURL!, 'Publisher One', `runner-publish-${Date.now()}@example.com`)
  await pair(page, `https://localhost:${publishBuild.port}`, code('publish-build-code.txt'), buildName)
  await pair(page, publishRunner.url, code('publish-runner-console-code.txt'), runnerName)

  // ---- before the Build server is paired with the runner, the tab says how
  await page.getByTestId(`instance-row-${buildName}`).getByTestId('instance-open-link').click()
  await page.getByTestId(`build-project-${publishBuild.project}`).getByTestId('build-project-link').click()
  await page.getByTestId('build-tab-publish').click()
  await expect(page.getByTestId('build-publish-runner-none')).toContainText('dbdatarunner package pair')

  // ---- pair the Build server's machine with the runner (what the message says), then the list shows it
  execFileSync('dotnet', ['exec', publishRunner.dll, 'package', 'pair', publishRunner.url, '--code', code('publish-runner-build-code.txt'), '--name', pairingName, '--client-dir', publishBuild.clientDirectory], { stdio: 'ignore' })
  await page.reload()
  await expect(page.getByTestId('build-publish-runner-server')).toBeVisible()
  await expect(page.getByTestId('build-publish-runner-server').locator('option')).toContainText(['Choose…', pairingName])

  // ---- the button waits for what is missing, and says what
  const button = page.getByTestId('build-publish-runner-button')
  await expect(button).toBeDisabled()
  await expect(page.getByTestId('build-publish-runner-why')).toHaveText('Choose the runner.')
  await page.getByTestId('build-publish-runner-server').selectOption({ label: pairingName })
  await expect(page.getByTestId('build-publish-runner-why')).toHaveText('Name the channel.')
  await page.getByTestId('build-publish-runner-channel').fill('dev')
  await page.getByTestId('build-publish-runner-bind').fill('nonsense')
  await expect(page.getByTestId('build-publish-runner-why')).toContainText('not slot=connection')
  await page.getByTestId('build-publish-runner-bind').fill('')
  await expect(button).toBeEnabled()
  await expect(page.getByTestId('build-publish-runner-why')).toHaveCount(0)

  // ---- the runner refuses a package whose connection slot has no binding in the channel, in its own words (found against a real runner: a channel's bindings are its own).
  // Another package name, because a publish records the Build server as the scheduler of its tasks before it uploads, even when it is then refused: `orders` must be untouched for the publish below.
  await page.getByTestId('build-publish-runner-package').fill('orders-unbound')
  await button.click()
  await expect(page.getByTestId('build-publish-runner-failed')).toContainText("integration 'orders-unbound' version 1 needs the slot 'sqlserver' bound in channel 'dev'", { timeout: 60_000 })
  await expect(page.getByTestId('build-publish-runner-done')).toHaveCount(0)
  await page.getByTestId('build-publish-runner-package').fill(publishBuild.project)

  // ---- bind the slot and publish: staged, with the task of the plan, its schedule and its connection slot
  await page.getByTestId('build-publish-runner-bind').fill('sqlserver=erp')
  await button.click()
  const done = page.getByTestId('build-publish-runner-done')
  await expect(done).toBeVisible({ timeout: 120_000 })
  await expect(page.getByTestId('build-publish-runner-summary')).toContainText(`Version 1 of ${publishBuild.project} is staged on dev`)
  const task = page.locator('[data-testid="build-publish-runner-task"]')
  await expect(task).toHaveCount(1)
  await expect(task).toContainText('15 * * * *')
  await expect(task).toContainText('sqlserver')
  // the plan stays with the Build server's scheduler until it is cut over, and the runner has been told so by this first publish of the package
  await expect(page.getByTestId('build-publish-runner-old')).toContainText('Still scheduled by this server until cut over')

  // ---- publishing the same project again changes nothing and sends no file
  await button.click()
  await expect(page.getByTestId('build-publish-runner-summary')).toContainText('nothing changed', { timeout: 120_000 })
  await expect(page.getByTestId('build-publish-runner-counts')).toContainText('0 file(s) sent')

  // ---- the Runner screens list the package, staged on the channel the Build screen named
  const runnerId = await idOf(request, runnerName)
  await page.goto(`/servers/${runnerId}/packages`)
  await page.getByTestId('runner-package-link').filter({ hasText: publishBuild.project }).first().click()
  await expect(page.getByTestId('runner-channel-heads')).toContainText('v1 staged', { timeout: 30_000 })
})
