import { expect, test, type APIRequestContext, type Page } from '@playwright/test'
import { execFileSync } from 'node:child_process'
import fs from 'node:fs'
import path from 'node:path'
import { publishRunner, runnerServer, scratchDirectory } from '../playwright.config'
import { signInNewAdmin } from './people'

// Plan 29a Q20 (28a R25): a task's checkpoints on the runner's own screens, with their history and the rewind to before one version. The sample package of the runner's tests is published to the runner of `runner-publish.spec.ts` (the same
// fixture as `runner-schedule.spec.ts`), and the details of its task `ok` are opened. The sample task keeps no checkpoint of its own, so the three checkpoint calls of the runner are answered in the page (`page.route`): the list, the history and
// the rewind, which the story checks was sent with the change it was pressed for. The runner's side of the calls is proved by `CheckpointRewindTests` (the rewind and its audit) and the screen's by the vitest of the checkpoints card.
// WRITTEN, NOT RUN in the session that wrote it. It signs in a new administrator, so it sorts after identity-home.
const runnerName = 'Publish runner'
const code = (file: string) => fs.readFileSync(path.join(scratchDirectory, file), 'utf8').trim()

async function instanceId(request: APIRequestContext): Promise<string | null> {
  return ((await (await request.get('/api/instances')).json()) as { id: string; name: string }[]).find((i) => i.name === runnerName)?.id ?? null
}

const part = (value: string) => ({ at: { type: 'int', value } })
const change = (id: number, version: number, old: string | null, next: string) => ({ id, part: 'at', type: 'int', old, new: next, version, run_id: null, actor: 'run', at: '2026-11-11T10:00:00Z' })

test('an operator reads a checkpoint, its history, and goes back before a version', async ({ page, context, baseURL }) => {
  const request = context.request
  test.setTimeout(240_000)
  await signInNewAdmin(page, context, baseURL!, 'Checkpoint One', `runner-checkpoints-${Date.now()}@example.com`)
  if (!(await instanceId(request))) {
    await page.goto('/instances')
    await page.getByTestId('pair-url-input').fill(publishRunner.url)
    await page.getByTestId('pair-code-input').fill(code('publish-runner-console-code.txt'))
    await page.getByTestId('pair-name-input').fill(runnerName)
    await page.getByTestId('pair-button').click()
    await expect(page.getByTestId(`instance-row-${runnerName}`).getByTestId('instance-status')).toHaveText('Ready', { timeout: 30_000 })
  }
  const id = (await instanceId(request))!
  execFileSync('dotnet', ['exec', publishRunner.dll, 'package', 'publish', path.join(runnerServer.manifests, 'v1.yaml'), runnerServer.package,
    '--server', publishRunner.url, '--channel', 'dev', '--client-dir', publishRunner.clientDirectory, '--code', code('publish-runner-sample-code.txt')], { stdio: 'ignore' })

  // the three calls, answered here: the list, the history of the whole-task item, and the rewind (whose body is kept to be checked)
  const sent: unknown[] = []
  let current = '30'
  const base = `**/relay/${id}/api/v1/tasks/sample/dev/ok/checkpoints`
  await page.route(`${base}`, (route) => route.fulfill({ json: { task: 'sample/dev/ok', checkpoints: [{ name: 'orders', items: [{ key: '', version: 3, updated: '2026-11-11T10:00:00Z', updated_by: 'run', parts: part(current) }] }] } }))
  await page.route(`${base}/orders/history*`, (route) => route.fulfill({ json: { task: 'sample/dev/ok', name: 'orders', key: '', changes: [change(3, 3, '20', '30'), change(2, 2, '10', '20')] } }))
  await page.route(`${base}/orders/rewind`, async (route) => {
    sent.push(route.request().postDataJSON())
    current = '10'
    await route.fulfill({ json: { task: 'sample/dev/ok', name: 'orders', key: '', version: 4 } })
  })

  await page.goto(`/servers/${id}`)
  const ok = page.locator('[data-testid="runner-task"][data-task="sample/dev/ok"]')
  await expect(ok).toBeVisible({ timeout: 60_000 })
  await ok.getByTestId('runner-task-details').click()

  const card = ok.getByTestId('runner-checkpoints')
  await expect(card.getByTestId('runner-checkpoint-value')).toHaveText('at=30')
  await card.getByTestId('runner-checkpoint-history').click()
  await expect(card.getByTestId('runner-checkpoint-change')).toHaveCount(2)
  await expect(card.getByTestId('runner-checkpoint-rewind')).toContainText('is audited')

  // going back before version 2 goes back to what the part was before that version (10)
  await card.getByTestId('runner-checkpoint-rewind-to').and(page.locator('[data-id="2"]')).click()
  await expect(card.getByTestId('runner-checkpoint-rewound')).toContainText('as version 4')
  expect(sent).toEqual([{ history_id: 2, key: '' }])
  await expect(card.getByTestId('runner-checkpoint-value')).toHaveText('at=10', { timeout: 30_000 })
})
