import { expect, test, type APIRequestContext, type Page } from '@playwright/test'
import { execFileSync } from 'node:child_process'
import fs from 'node:fs'
import path from 'node:path'
import { publishRunner, runnerServer, scratchDirectory } from '../playwright.config'
import { signInNewAdmin } from './people'

// Plan 33, UE06: who schedules a task, on the runner's own screens. The sample package of the runner's tests is published to the runner of `runner-publish.spec.ts` (the same fixture); its task `ok` is scheduled every hour. The story reads the owner of
// the task (the runner, as nothing moved it), moves it to the publishing product (`old`: the runner stops queueing it), to `observe` (the runner records what it would queue) and back to the runner, which takes a position and a reason; it sees the
// button wait for the reason and the position and say so, runs the task now with an input, and sees the owner in the Monitoring page. Not in the story: moving a Build plan from Monitoring (it needs a Build package active on the runner, which needs the
// `dbdatabuild-runtime` package: the Build server's route is proved by its unit tests). It signs in a new administrator, so it sorts after identity-home; it shares its runner with `runner-publish.spec.ts` and finds the pairing there.
const runnerName = 'Publish runner'
const code = (file: string) => fs.readFileSync(path.join(scratchDirectory, file), 'utf8').trim()
const task = (page: Page, key: string) => page.locator(`[data-testid="runner-task"][data-task="${key}"]`)

async function instanceId(request: APIRequestContext): Promise<string | null> {
  return ((await (await request.get('/api/instances')).json()) as { id: string; name: string }[]).find((i) => i.name === runnerName)?.id ?? null
}

test('a task is moved between its publishing product, observation and the runner, and runs now with an input', async ({ page, context, baseURL }) => {
  const request = context.request
  test.setTimeout(240_000)
  await signInNewAdmin(page, context, baseURL!, 'Scheduler One', `runner-schedule-${Date.now()}@example.com`)

  // the runner is paired by the story before this one (runner-publish.spec.ts), or here when this one runs alone
  if (!(await instanceId(request))) {
    await page.goto('/instances')
    await page.getByTestId('pair-url-input').fill(publishRunner.url)
    await page.getByTestId('pair-code-input').fill(code('publish-runner-console-code.txt'))
    await page.getByTestId('pair-name-input').fill(runnerName)
    await page.getByTestId('pair-button').click()
    await expect(page.getByTestId(`instance-row-${runnerName}`).getByTestId('instance-status')).toHaveText('Ready', { timeout: 30_000 })
  }
  const id = (await instanceId(request))!

  // the sample package, served at once on the channel `dev`
  execFileSync('dotnet', ['exec', publishRunner.dll, 'package', 'publish', path.join(runnerServer.manifests, 'v1.yaml'), runnerServer.package,
    '--server', publishRunner.url, '--channel', 'dev', '--client-dir', publishRunner.clientDirectory, '--code', code('publish-runner-sample-code.txt')], { stdio: 'ignore' })

  await page.goto(`/servers/${id}`)
  await expect(page.getByTestId('runner-tasks-page')).toBeVisible()
  const ok = task(page, 'sample/dev/ok')
  await expect(ok).toBeVisible({ timeout: 60_000 })
  await expect(ok.getByTestId('runner-task-owner')).toHaveAttribute('data-owner', 'runner')
  await expect(ok.getByTestId('runner-task-owner')).toContainText('this runner')

  // ---- the details: nothing moved it, so there is no one who set it
  await ok.getByTestId('runner-task-details').click()
  const panel = ok.getByTestId('runner-task-panel')
  await expect(panel.getByTestId('runner-task-panel-owner')).toHaveAttribute('data-owner', 'runner')
  await expect(panel.getByTestId('runner-task-schedule')).toContainText('never moved')

  // ---- the button waits for a reason and says so; then the runner stops queueing the task (the publishing product's)
  const move = panel.getByTestId('runner-task-owner-move')
  await expect(panel.getByTestId('runner-task-owner-select')).toHaveValue('old')
  await expect(move).toBeDisabled()
  await expect(panel.getByTestId('runner-task-owner-why')).toContainText('Say why')
  await panel.getByTestId('runner-task-owner-reason').fill('the Build server keeps it for now')
  await expect(move).toBeEnabled()
  await move.click()
  await expect(panel.getByTestId('runner-task-owner-moved')).toContainText('Now scheduled by the publishing product', { timeout: 30_000 })
  await expect(ok.getByTestId('runner-task-owner')).toHaveAttribute('data-owner', 'old', { timeout: 30_000 })
  await expect(panel.getByTestId('runner-task-panel-owner')).toHaveAttribute('data-owner', 'old', { timeout: 30_000 })
  await expect(panel.getByTestId('runner-task-reason')).toContainText('the Build server keeps it for now')
  await expect(panel.getByTestId('runner-task-schedule')).toContainText('set by Scheduler One')

  // ---- observe: the runner records what it would queue
  await panel.getByTestId('runner-task-owner-reason').fill('compare for a day')
  await expect(panel.getByTestId('runner-task-owner-select')).toHaveValue('observe')
  await panel.getByTestId('runner-task-owner-move').click()
  await expect(ok.getByTestId('runner-task-owner')).toHaveAttribute('data-owner', 'observe', { timeout: 30_000 })
  await expect(panel.getByTestId('runner-task-decisions')).toBeVisible()

  // ---- back to the runner: it takes the position the old scheduler had reached, which the button asks for
  await expect(panel.getByTestId('runner-task-owner-select')).toHaveValue('runner')
  await panel.getByTestId('runner-task-owner-reason').fill('cut over')
  // the form opens with the position the runner already has (when its schedule answer has arrived: the panel polls), so the story empties the field and asks the rule itself
  await panel.getByTestId('runner-task-owner-position').fill('')
  await expect(panel.getByTestId('runner-task-owner-move')).toBeDisabled()
  await expect(panel.getByTestId('runner-task-owner-why')).toContainText('Give the time')
  await panel.getByTestId('runner-task-owner-position').fill('not a time')
  await expect(panel.getByTestId('runner-task-owner-why')).toContainText('not a time')
  await panel.getByTestId('runner-task-owner-position').fill(new Date().toISOString())
  await panel.getByTestId('runner-task-owner-move').click()
  await expect(ok.getByTestId('runner-task-owner')).toHaveAttribute('data-owner', 'runner', { timeout: 30_000 })
  await expect(panel.getByTestId('runner-task-position')).toContainText('are dealt with', { timeout: 30_000 })

  // ---- run now with an input: a line that is not name=value is named and nothing is sent; then the run is queued and ends
  await panel.getByTestId('runner-task-inputs').fill('oops')
  await expect(panel.getByTestId('runner-task-run-with-inputs')).toBeDisabled()
  await expect(panel.getByTestId('runner-task-inputs-why')).toContainText('is not name=value')
  await panel.getByTestId('runner-task-inputs').fill('note=hello')
  await panel.getByTestId('runner-task-run-with-inputs').click()
  await expect(panel.getByTestId('runner-task-owner-moved')).toContainText('queued', { timeout: 30_000 })
  // the run ends; how it ends is the job's: the sample task `ok` declares no input, so a real runner may end a run that was given one as Failed (seen in CI), and the story is about the queue and the screen
  await expect(ok.getByTestId('runner-task-last')).toContainText(/Succeeded|Failed/, { timeout: 60_000 })

  // ---- the Monitoring page says who schedules a task that is not the runner's
  await panel.getByTestId('runner-task-owner-reason').fill('back to the product')
  await expect(panel.getByTestId('runner-task-owner-select')).toHaveValue('old')
  await panel.getByTestId('runner-task-owner-move').click()
  await expect(ok.getByTestId('runner-task-owner')).toHaveAttribute('data-owner', 'old', { timeout: 30_000 })
  await page.goto('/monitoring')
  await expect(page.locator('[data-testid="monitoring-row"]', { hasText: 'sample/dev/ok' })).toContainText('Scheduled by the publishing product, not by this runner', { timeout: 30_000 })
})
