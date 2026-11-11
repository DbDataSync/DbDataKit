import { expect, test } from '@playwright/test'
import { execFileSync } from 'node:child_process'
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import pg from 'pg'
import { refreshBuild, scratchDirectory, warehouseConnection } from '../playwright.config'
import { signInNewAdmin } from './people'

// Plan 33 UE09: from the Build screens, run a refresh plan and the project's tests. The project `refreshing` (made by playwright.config.ts, Build server on port 8541) has a mapped model over staging.events and an
// incremental mart with a refresh plan `nightly` and two checks: amount_not_negative passes and amount_small is broken by one of the three rows the story inserts. Ada deploys the mart (the Plans tab, as the
// deploy story does), then runs the plan: the Run button is off until a dry run of the same choice passed (and says why), the dry run executes nothing, the run loads the three rows, and the checks of the model that
// loaded are shown with the broken one named (plan 23a FQ09: data.checks). The Check tab runs the project's tests. Needs the test PostgreSQL (DBDATAKIT_TEST_E2E_PG); it skips without it.
// Order: the specs run one at a time by file name; this one drops the schemas it made at the end, so the deploy story (template-to-plan, which resets them itself) and the others are not affected.
const check = expect.configure({ timeout: 60_000 })

async function sql(statements: string[]) {
  const parts = Object.fromEntries(warehouseConnection!.split(';').filter(Boolean).map((p) => [p.slice(0, p.indexOf('=')).trim().toLowerCase(), p.slice(p.indexOf('=') + 1)]))
  const client = new pg.Client({ host: parts.host, port: Number(parts.port ?? 5432), user: parts.username, password: parts.password, database: parts.database })
  await client.connect()
  try { for (const s of statements) await client.query(s) } finally { await client.end() }
}

test.skip(!warehouseConnection, 'set DBDATAKIT_TEST_E2E_PG to run the refresh story')

test('refresh a plan from the Plans tab (dry run first, then the run with its checks) and run the project tests', async ({ page, context, baseURL }) => {
  test.setTimeout(300_000)
  execFileSync(process.execPath, [path.join(path.dirname(fileURLToPath(import.meta.url)), '../pgReset.cjs')], { stdio: 'inherit' })
  await sql([
    'CREATE SCHEMA staging',
    'CREATE TABLE staging.events (event_id BIGINT NOT NULL, event_ts TIMESTAMP NOT NULL, amount DECIMAL(14, 2))',
    "INSERT INTO staging.events VALUES (1, '2026-01-01 10:00:00', 10), (2, '2026-01-02 10:00:00', 20), (3, '2026-01-03 10:00:00', 99)",
  ])
  try {
    await signInNewAdmin(page, context, baseURL!, 'Ada Lovelace', 'ada.refresh@example.com')
    await page.goto('/instances')
    await page.getByTestId('pair-url-input').fill(`https://localhost:${refreshBuild.port}`)
    await page.getByTestId('pair-code-input').fill(fs.readFileSync(path.join(scratchDirectory, 'refresh-build-pairing-code.txt'), 'utf8').trim())
    await page.getByTestId('pair-name-input').fill('Refresh build')
    await page.getByTestId('pair-button').click()
    await check(page.getByTestId('instance-row-Refresh build').getByTestId('instance-status')).toHaveText('Ready', { timeout: 30_000 })
    await page.getByTestId('instance-row-Refresh build').getByTestId('instance-open-link').click()
    await page.getByTestId(`build-project-${refreshBuild.project}`).getByTestId('build-project-link').click()

    // The deploy that makes the mart: tracking tables, a plan, applied (an administrator holds every role).
    await page.getByTestId('build-tab-status').click()
    await page.getByTestId('build-initialise-tracking').click()
    // what the command left: its line, or (when it failed) the problems or the banner, so a failure names its cause
    await check(page.getByTestId('build-initialise-done').or(page.getByTestId('build-command-problems')).or(page.getByRole('alert')).first()).toContainText('tracking tables')
    await page.getByTestId('build-tab-plans').click()
    await page.getByTestId('build-plan-write').click()
    await check(page.getByTestId('build-plan-review')).toBeVisible()
    await page.getByTestId('build-plan-apply').click()
    await check(page.getByTestId('build-plan-applied')).toBeVisible()

    // The refresh: the plan is offered, the run is off and says it wants a dry run first.
    await page.getByTestId('build-refresh-plan').selectOption({ label: 'postgres / nightly (1 loads)' })
    await expect(page.getByTestId('build-refresh-run')).toBeDisabled()
    await expect(page.getByTestId('build-refresh-why')).toContainText('dry run')
    await page.getByTestId('build-refresh-dry-run').click()
    await check(page.getByTestId('build-refresh-headline')).toContainText('dry run passed')
    await expect(page.getByTestId('build-refresh-headline')).toContainText('Nothing was executed')
    await expect(page.getByTestId('build-refresh-checks')).toHaveCount(0)   // a dry run evaluates no checks
    await expect(page.getByTestId('build-refresh-run')).toBeEnabled()

    // Changing the choice makes the dry run stale: the run is off again until it is repeated.
    await page.getByTestId('build-refresh-selectors').fill('marts.fct_events')
    await expect(page.getByTestId('build-refresh-run')).toBeDisabled()
    await page.getByTestId('build-refresh-selectors').fill('')
    await page.getByTestId('build-refresh-dry-run').click()
    await check(page.getByTestId('build-refresh-run')).toBeEnabled()

    // The run: the load is done, and the checks of the model that loaded are shown, the broken one named.
    await page.getByTestId('build-refresh-run').click()
    await check(page.getByTestId('build-refresh-headline')).toContainText('The refresh completed')
    await expect(page.locator('[data-testid^="build-refresh-load-marts.fct_events"]')).toHaveCount(1)
    await expect(page.getByTestId('build-refresh-load-state')).toHaveText('done')
    await expect(page.getByTestId('build-refresh-checks-counts')).toContainText('2 evaluated: 1 passed, 1 failed')
    await expect(page.getByTestId('build-refresh-check-marts.fct_events-amount_small').getByTestId('build-refresh-check-status')).toHaveText('fail')
    await expect(page.getByTestId('build-refresh-check-marts.fct_events-amount_not_negative').getByTestId('build-refresh-check-status')).toHaveText('pass')
    expect(await rows()).toBe(3)

    // The Status tab reads the refresh plan's last run from the tracking tables.
    await page.getByTestId('build-tab-status').click()
    await page.getByTestId('build-status-refresh').click()
    await check(page.getByTestId('build-status-refresh-plans')).toContainText('nightly')

    // The project's tests, from the Check tab (this project has none: the answer says so).
    await page.getByTestId('build-tab-check').click()
    await page.getByTestId('build-tests-button').click()
    await check(page.getByTestId('build-tests-counts')).toContainText('This project has no tests.')
  } finally {
    await sql(['DROP SCHEMA IF EXISTS staging CASCADE', 'DROP SCHEMA IF EXISTS marts CASCADE', 'DROP SCHEMA IF EXISTS dbdatabuild CASCADE'])
  }
})

async function rows(): Promise<number> {
  const parts = Object.fromEntries(warehouseConnection!.split(';').filter(Boolean).map((p) => [p.slice(0, p.indexOf('=')).trim().toLowerCase(), p.slice(p.indexOf('=') + 1)]))
  const client = new pg.Client({ host: parts.host, port: Number(parts.port ?? 5432), user: parts.username, password: parts.password, database: parts.database })
  await client.connect()
  try { return Number((await client.query('SELECT count(*) AS n FROM marts.fct_events')).rows[0].n) } finally { await client.end() }
}
