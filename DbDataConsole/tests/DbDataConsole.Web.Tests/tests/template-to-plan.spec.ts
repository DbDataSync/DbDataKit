import { expect, test } from '@playwright/test'
import { execFileSync } from 'node:child_process'
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import pg from 'pg'
import { buildDll, endToEndBuild, scratchDirectory, warehouseConnection } from '../playwright.config'
import { signInNewAdmin } from './people'

// Plan 13a, W10: from a template to an applied plan, all in the console, by two people. Ada makes a project from the starter template on a Build server that hosts none, starts a workspace, points the project at the
// PostgreSQL (its settings, in the YAML editor), describes a model (a form), has the compiler write the rendered files a deploy plans from, commits and merges. Then, on the test PostgreSQL (DBDATA_E2E_PG; the
// story stops after the merge when it is unset), Ada plans the deploy, and Grace, a second person signed in on her own, reviews it, tries it, approves and applies it, and the tracking tables name her as the approver.
//
// Two steps are not the console's yet and are done as the server's administrator would, on the server: the connection file for the PostgreSQL (the Edit tab cannot create a file; W07's story adds its origin the same
// way) and the tracking tables and raw tables the models read (`connection init` and `connection seed`, which no screen offers). This file runs after first-run.spec.ts (files run in name order).
const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../../..')
const check = expect.configure({ timeout: 60_000 })

/** The starter's own settings, pointed at PostgreSQL: the same replacements the first Build server's deploy project is made with (playwright.config.ts). */
function postgresSettings(): string {
  const original = fs.readFileSync(path.join(repoRoot, 'DbDataBuild/templates/starter/dbdatabuild.yml'), 'utf8')
  let text = original
  for (const [from, to] of [
    ['defaults: {connections: [sqlserver]}', 'defaults: {connections: [postgres]}'],
    ['tracking: { connection: sqlserver }', 'tracking: { connection: postgres }'],
    ['trailing_space: ignored', 'trailing_space: significant'],
    ['default: { duckdb: NFC, sqlserver: Latin1_General_100_CS_AS }', 'default: { duckdb: NFC, postgres: C }'],
  ]) {
    if (!text.includes(from)) throw new Error(`The starter template's settings no longer contain "${from}"; update this story.`)
    text = text.replace(from, to)
  }
  return text
}

function git(cwd: string, ...args: string[]) {
  execFileSync('git', ['-c', 'user.name=console-e2e', '-c', 'user.email=e2e@example.invalid', ...args], { cwd, stdio: 'ignore' })
}

/** Who the tracking tables say applied each plan: the approver the server recorded. */
async function appliedBy(): Promise<string[]> {
  const parts = Object.fromEntries(warehouseConnection!.split(';').filter(Boolean).map((p) => [p.slice(0, p.indexOf('=')).trim().toLowerCase(), p.slice(p.indexOf('=') + 1)]))
  const client = new pg.Client({ host: parts.host, port: Number(parts.port ?? 5432), user: parts.username, password: parts.password, database: parts.database })
  await client.connect()
  try { return (await client.query('SELECT applied_by FROM dbdatabuild.migration_log ORDER BY applied_utc')).rows.map((r) => String(r.applied_by)) } finally { await client.end() }
}

test('end to end: a template, an edit, a commit and a merge, then a plan approved by a second person and applied', async ({ page, context, browser, baseURL }) => {
  test.setTimeout(420_000)
  await signInNewAdmin(page, context, baseURL!, 'Ada Lovelace', 'ada.e2e@example.com')

  await page.goto('/instances')
  await page.getByTestId('pair-url-input').fill(`https://localhost:${endToEndBuild.port}`)
  await page.getByTestId('pair-code-input').fill(fs.readFileSync(path.join(scratchDirectory, 'end-to-end-build-pairing-code.txt'), 'utf8').trim())
  await page.getByTestId('pair-name-input').fill('End to end build')
  await page.getByTestId('pair-button').click()
  const instance = page.getByTestId('instance-row-End to end build')
  await check(instance.getByTestId('instance-status')).toHaveText('Ready', { timeout: 30_000 })
  await instance.getByTestId('instance-open-link').click()
  await check(page.getByTestId('build-no-projects')).toBeVisible()

  // Create: the starter template, as a project named shop.
  await page.getByTestId('build-new-project-open').click()
  await page.getByTestId('build-new-project-template-starter').click()
  await page.getByTestId('build-new-project-name').fill('shop')
  await page.getByTestId('build-new-project-create').click()
  await check(page).toHaveURL(/\/projects\/shop$/)
  await check(page.getByTestId('build-model-marts.orders')).toBeVisible()

  // The server's administrator adds the connection to the PostgreSQL (a file with no login in it; the login is the server's environment) and commits it, as W07's story does for its origin.
  const checkout = path.join(endToEndBuild.directory, 'projects', 'shop')
  fs.writeFileSync(path.join(checkout, 'connections', 'postgres.yaml'), 'schema: connection/1\nname: postgres\nengine: postgres\ntls:\n  encrypt: false\n  trust_server_certificate: true\n')
  git(checkout, 'add', '-A')
  git(checkout, 'commit', '-qm', 'A connection to the warehouse')

  // Edit, in a workspace of her own: the project's settings point at PostgreSQL (the YAML of the settings file, replaced), and a model gets a description through its form.
  await page.getByTestId('build-tab-edit').click()
  await page.getByTestId('build-edit-workspace-name').fill('warehouse')
  await page.getByTestId('build-edit-workspace-make').click()
  await page.getByTestId('build-edit-file-dbdatabuild.yml').click()
  await expect(page.getByTestId('build-edit-open')).toHaveText('dbdatabuild.yml')
  await page.getByTestId('build-edit-mode-yaml').click()
  await page.getByTestId('build-edit-editor').locator('.monaco-editor').click()
  await page.keyboard.press('Control+A')
  // pasted, not inserted: the editor indents each new line of inserted text under the one before it, and a paste it leaves as it is
  await context.grantPermissions(['clipboard-read', 'clipboard-write'])
  await page.evaluate((text) => navigator.clipboard.writeText(text), postgresSettings())
  await page.keyboard.press('Control+V')
  await page.getByTestId('build-edit-save').click()
  await check(page.getByTestId('build-edit-saved')).toBeVisible()
  await check(page.getByTestId('build-edit-clean')).toBeVisible()

  await page.getByTestId('build-edit-file-models/staging/orders.yml').click()
  await page.getByTestId('build-edit-mode-form').click()
  await expect(page.getByTestId('form-field-name')).toHaveValue('staging.orders')
  await page.getByTestId('form-add-description').click()
  await page.getByTestId('form-field-description').fill('Orders with their statuses grouped')
  await page.getByTestId('build-edit-save').click()
  await check(page.getByTestId('build-edit-clean')).toBeVisible()

  // The compiler writes the rendered files the deploy plans from (saving only checks): the screen says the files are out of date, and after the button says what it wrote.
  await check(page.getByTestId('build-compile-hint')).toContainText('out of date')
  await page.getByTestId('build-compile-button').click()
  await check(page.getByTestId('build-compile-done')).toContainText('Wrote')
  await check(page.getByTestId('build-compile-hint')).toContainText('match the models')
  await check(page.getByTestId('build-edit-clean')).toBeVisible()

  // Commit and merge: the draft shows what it changes (the two files edited and the files rendered), is committed, and is merged into the project (an approver's act; administrators are approvers), which ends the workspace.
  await check(page.getByTestId('build-git-change').filter({ hasText: 'dbdatabuild.yml' })).not.toHaveCount(0)
  await check(page.getByTestId('build-git-change').filter({ hasText: 'models/staging/orders.yml' })).toHaveCount(1)
  await check(page.getByTestId('build-git-change').filter({ hasText: 'rendered/postgres/loads.yml' })).toHaveCount(1)
  await page.getByTestId('build-git-message').fill('Build the shop on the warehouse')
  await page.getByTestId('build-git-commit').click()
  await expect(page.getByTestId('build-git-commit-row')).toContainText('Build the shop on the warehouse')
  await page.getByTestId('build-git-merge').click()
  await check(page.getByTestId('build-edit-none')).toBeVisible()
  expect(fs.existsSync(path.join(checkout, 'rendered', 'postgres', 'loads.yml'))).toBe(true)   // the project the server holds has them now

  if (!warehouseConnection) return   // no PostgreSQL to deploy to: the story ends at the merge

  // The server's administrator makes the tracking tables and the raw tables the models read, in a database that has never been deployed to.
  execFileSync(process.execPath, [path.join(path.dirname(fileURLToPath(import.meta.url)), '../pgReset.cjs')], { stdio: 'inherit' })
  const logins = { ...process.env, DBDATA_BUILD_POSTGRES_READ: warehouseConnection, DBDATA_BUILD_POSTGRES_WRITE: warehouseConnection }
  for (const command of ['init', 'seed']) execFileSync('dotnet', ['exec', buildDll, 'connection', command, 'postgres', '--apply'], { cwd: checkout, env: logins, stdio: 'ignore' })

  // Plan: where the project stands (nothing deployed), and a plan written by Ada.
  await page.getByTestId('build-tab-status').click()
  await check(page.getByTestId('build-status-structure')).toHaveText('pending')
  await expect(page.getByTestId('build-status-object-marts.orders').getByTestId('build-status-state')).toContainText('missing')
  await page.getByTestId('build-tab-plans').click()
  await expect(page.getByTestId('build-plans-empty')).toBeVisible()
  await page.getByTestId('build-plan-write').click()
  await check(page.getByTestId('build-plan-review')).toBeVisible()
  await expect(page.getByTestId('build-plan-facts')).toContainText('11 steps')
  expect(await appliedBy()).toEqual([])   // planned, nothing applied

  // Approve and apply, by a second person: Grace signs in on her own (a context and a passkey of her own), opens the same server and project, reads the plan Ada wrote, tries it, approves and applies it.
  const hers = await browser.newContext({ baseURL: baseURL!, ignoreHTTPSErrors: true })
  try {
    const grace = await hers.newPage()
    await signInNewAdmin(grace, hers, baseURL!, 'Grace Hopper', 'grace.e2e@example.com')
    await grace.goto('/instances')
    await grace.getByTestId('instance-row-End to end build').getByTestId('instance-open-link').click()
    await grace.getByTestId('build-project-shop').getByTestId('build-project-link').click()
    await grace.getByTestId('build-tab-plans').click()
    await check(grace.locator('[data-testid^="build-plan-2"]')).toHaveCount(1)   // the plan's row is named by its id, which starts with the date
    await grace.locator('[data-testid^="build-plan-2"]').click()
    await check(grace.getByTestId('build-plan-facts')).toContainText('11 steps')
    await grace.getByTestId('build-plan-step-5').locator('summary').click()
    await expect(grace.getByTestId('build-plan-step-5')).toContainText('CREATE TABLE')
    await grace.getByTestId('build-plan-dry-run').click()
    await check(grace.getByTestId('build-plan-dry-ok')).toBeVisible()
    await grace.getByTestId('build-plan-apply').click()
    await check(grace.getByTestId('build-plan-applied')).toBeVisible()

    // What was deployed, and who the tracking tables name.
    await grace.getByTestId('build-tab-status').click()
    await grace.getByTestId('build-status-refresh').click()
    await check(grace.getByTestId('build-status-object-marts.orders').getByTestId('build-status-state')).not.toContainText('missing')
    await grace.getByTestId('build-tab-compare').click()
    await grace.getByTestId('build-compare-table').fill('marts.orders')
    await grace.getByTestId('build-compare-against').fill('staging.orders')
    await grace.getByTestId('build-compare-key').fill('order_id')
    await grace.getByTestId('build-compare-button').click()
    await check(grace.getByTestId('build-compare-verdict')).toHaveText('Identical')
  } finally {
    await hers.close()
  }
  const approvers = await appliedBy()
  expect(approvers).toHaveLength(1)
  expect(approvers[0]).toContain('Grace Hopper')
  expect(approvers[0]).not.toContain('Ada Lovelace')
})
