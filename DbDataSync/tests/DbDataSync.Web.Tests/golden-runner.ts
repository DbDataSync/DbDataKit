import { execFileSync } from 'node:child_process'
import fs from 'node:fs'
import path from 'node:path'
import { expect, type Page } from '@playwright/test'
import { consoleScratch } from './console-paths'
import { syncDirectory, syncRunner } from './playwright.config'

/**
 * The golden path's passes run on the suite's runner (plan 28c E22, owner decision S2 of 2026-11-14: the Runner is Sync's only scheduler, so the Sync server runs no pass of its own). Before a step runs a pass, the
 * replication set is published to the runner as a person would from the Runner screen: the first time the Sync server is paired with the runner and linked to it (its own pairing code and client directory, so the
 * Runner screen's story keeps its own), and every time the set is published again, so the pass runs the definition as the step left it (a publish is explicit, P9). A switched-off replication is not published, so the
 * golden path's replication is switched on first. The runner's copies of the connections are the files Sync wrote; their logins reach the runner from its environment (`secretEnv` of the config).
 */
export const GOLDEN_PACKAGE = 'golden-set'
const CLIENT = 'golden-runner'
let linked = false

export async function publishToRunner(page: Page, replication: string, connections: string[]) {
  if (!linked) {
    const code = fs.readFileSync(path.join(consoleScratch, 'sync-runner-golden-pairing-code.txt'), 'utf8').trim()
    execFileSync('dotnet', ['exec', syncRunner.dll, 'package', 'pair', syncRunner.url, '--code', code, '--name', CLIENT, '--client-dir', syncRunner.goldenClientDirectory], { stdio: 'ignore' })
    const link = await page.request.put('/api/runner', { data: { server: CLIENT, channel: 'prod', package: GOLDEN_PACKAGE, clientDirectory: syncRunner.goldenClientDirectory } })
    expect(link.ok(), await link.text()).toBe(true)
    linked = true
  }

  fs.mkdirSync(path.join(syncRunner.directory, 'connections'), { recursive: true })
  for (const connection of connections)
    fs.copyFileSync(path.join(syncDirectory, 'config', 'connections', `${connection}.yaml`), path.join(syncRunner.directory, 'connections', `${connection}.yaml`))

  // on demand, as the story's passes were on this server's worker: a periodic schedule that is never due while the story runs, so a pass is the one Run now starts (a continuous drain would stay up between passes)
  const task = await (await page.request.get(`/api/replications/${replication}`)).json() as { enabled: boolean; scheduling: Record<string, unknown> }
  if (task.scheduling.mode !== 'Periodic') {
    const saved = await page.request.put(`/api/replications/${replication}`, { data: { ...task, scheduling: { ...task.scheduling, mode: 'Periodic', cronExpression: '0 3 1 1 *' } } })
    expect(saved.ok(), await saved.text()).toBe(true)
  }
  if (!task.enabled) expect((await page.request.put(`/api/replications/${replication}/enabled`, { data: { enabled: true } })).ok()).toBe(true)

  const bind = Object.fromEntries(connections.map((c) => [c.replace(/-/g, '_'), c]))
  const published = await page.request.post('/api/runner/package/publish', { data: { mode: 'serve', bind }, timeout: 180_000 })
  expect(published.ok(), await published.text()).toBe(true)
  await expect.poll(async () => {
    const view = await (await page.request.get('/api/runner/package')).json() as { latest: number | null; active: number | null }
    return view.latest !== null && view.active === view.latest
  }, { timeout: 180_000 }).toBe(true)
  await runnerIdle(page, replication)
}

/** Waits until the runner lists the replication's task with no drain running or queued, so the next Run now starts a drain of its own (one that is up would take the request from its queue and start none). */
export async function runnerIdle(page: Page, replication: string) {
  await expect.poll(async () => {
    const view = await (await page.request.get(`/api/replications/${replication}/runner`)).json() as { runner: { running: number; queued: number } | null }
    return view.runner !== null && view.runner.running === 0 && view.runner.queued === 0
  }, { timeout: 120_000 }).toBe(true)
}

/**
 * Presses Run now and waits for the live panel of the drain it started. A drain the runner had just started on its own (the version a publish activated) takes the request from its queue and starts none, so no panel
 * opens: the press is made again once the task is idle.
 */
export async function runNow(page: Page, replication: string) {
  for (let attempt = 1; ; attempt++) {
    await runnerIdle(page, replication)
    await page.getByTestId('trigger-run-button').click()
    try {
      await expect(page.getByTestId('live-run-panel')).toBeVisible({ timeout: 15_000 })
      return
    } catch (error) {
      if (attempt >= 3) throw error
    }
  }
}

/** Unlinks the Sync server from the runner when the story ends, so the stories after it start from "no runner is linked". */
export function unlinkRunner() {
  fs.rmSync(path.join(syncDirectory, 'runner-link.json'), { force: true })
  linked = false
}
