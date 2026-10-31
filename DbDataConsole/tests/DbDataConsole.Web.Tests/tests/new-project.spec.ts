import { expect, test, type BrowserContext, type Page } from '@playwright/test'
import { execFileSync } from 'node:child_process'
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { authoringBuild, scratchDirectory } from '../playwright.config'

// Plan 13a, W07: authoring from nothing, in the console. A Build server that hosts no project: the person makes one from the `retail` template, starts a workspace, adds a model, changes its query and answers the questions the
// tool asks about the new column's definition, and imports tables from a connection (a DuckDB file: no engine is needed) as mapped models. The controls that create are for an author: the console gives a viewer no
// way to open a server's screens yet, so who may see them is covered by the server's tests (BuildServerNewProjectTests) and the screens' own (NewProjectPanel, the Edit tab). This file runs after first-run.spec.ts (files
// run in name order): redeeming an invitation here would spend the first-run one that story needs.
const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../../..')
const consoleDll = path.join(repoRoot, 'DbDataConsole/src/DbDataConsole.Server/bin/Debug/net10.0/dbdatakit.dll')

// The retail template is a large project (about a hundred files): the compiler takes some twelve seconds on it in a Debug build, and a workspace's commands run one after the other in its warm process, so a step that
// follows a check waits for it. The assertions wait for that, and the story's time limit is set for it.
const check = expect.configure({ timeout: 90_000 })

async function signIn(page: Page, context: BrowserContext, baseURL: string, role: 'admin', name: string, email: string) {
  const cdp = await context.newCDPSession(page)
  await cdp.send('WebAuthn.enable')
  await cdp.send('WebAuthn.addVirtualAuthenticator', {
    options: { protocol: 'ctap2', transport: 'internal', hasResidentKey: true, hasUserVerification: true, isUserVerified: true, automaticPresenceSimulation: true },
  })
  const made = execFileSync('dotnet', ['exec', consoleDll, 'user', 'invite', '--role', role, '--url', baseURL, '--repo', scratchDirectory], { encoding: 'utf8' })
  await page.goto(`/invite#${/\/invite#(\S+)/.exec(made)![1]}`)
  await page.getByTestId('invite-name-input').fill(name)
  await page.getByTestId('invite-email-input').fill(email)
  await page.getByTestId('invite-enrol-button').click()
  await expect(page.getByTestId('home-page')).toBeVisible()
}

test('authoring: a project from a template, a model and its questions, tables imported from a connection', async ({ page, context, baseURL }) => {
  test.setTimeout(420_000)
  await signIn(page, context, baseURL!, 'admin', 'Authoring Tester', 'authoring@example.com')

  await page.goto('/instances')
  await page.getByTestId('pair-url-input').fill(`https://localhost:${authoringBuild.port}`)
  await page.getByTestId('pair-code-input').fill(fs.readFileSync(path.join(scratchDirectory, 'authoring-build-pairing-code.txt'), 'utf8').trim())
  await page.getByTestId('pair-name-input').fill('Authoring build')
  await page.getByTestId('pair-button').click()
  const instance = page.getByTestId('instance-row-Authoring build')
  await check(instance.getByTestId('instance-status')).toHaveText('Ready', { timeout: 30_000 })
  await instance.getByTestId('instance-open-link').click()
  await check(page.getByTestId('build-no-projects')).toBeVisible()
  const serverUrl = page.url()

  // New project: the templates the server has, a name the server would refuse is said so, and the project is made and opened.
  await page.getByTestId('build-new-project-open').click()
  await check(page.getByTestId('build-new-project-template-retail')).toContainText('Northstar Outfitters')
  await check(page.getByTestId('build-new-project-create')).toBeDisabled()
  await page.getByTestId('build-new-project-template-retail').click()
  await page.getByTestId('build-new-project-name').fill('Retail Shop')
  await check(page.getByTestId('build-new-project-name-problem')).toBeVisible()
  await check(page.getByTestId('build-new-project-create')).toBeDisabled()
  await page.getByTestId('build-new-project-name').fill('retail')
  await page.getByTestId('build-new-project-create').click()
  await check(page).toHaveURL(/\/projects\/retail$/)
  await check(page.getByTestId('build-model-staging.orders')).toBeVisible()

  // The same name again is refused in the server's words, and nothing changes.
  await page.goto(serverUrl)
  await page.getByTestId('build-new-project-open').click()
  await page.getByTestId('build-new-project-template-starter').click()
  await page.getByTestId('build-new-project-name').fill('retail')
  await page.getByTestId('build-new-project-create').click()
  await check(page.getByTestId('build-new-project-failed')).toContainText('already hosts a project called')
  await page.getByTestId('build-new-project-cancel').click()
  await check(page.getByTestId('build-project-retail')).toHaveCount(1)

  // The project is a git repository on the server; its connection to the origin is added the way an administrator would, and committed.
  const retail = path.join(authoringBuild.directory, 'projects', 'retail')
  fs.writeFileSync(path.join(retail, 'connections', 'origin.yaml'), 'schema: connection/1\nname: origin\nengine: duckdb\naccess: readonly\n')
  for (const args of [['add', '-A'], ['commit', '-qm', 'An origin connection']])
    execFileSync('git', ['-c', 'user.name=console-e2e', '-c', 'user.email=e2e@example.invalid', ...args], { cwd: retail, stdio: 'ignore' })

  await page.getByTestId('build-project-retail').getByTestId('build-project-link').click()
  await page.getByTestId('build-tab-edit').click()
  await page.getByTestId('build-edit-workspace-name').fill('draft')
  await page.getByTestId('build-edit-workspace-make').click()

  // New model: a name, a kind; the model is made in the workspace and its query opens.
  await page.getByTestId('build-edit-new-model').click()
  await page.getByTestId('build-new-model-name').fill('returns')
  await check(page.getByTestId('build-new-model-name-problem')).toBeVisible()
  await check(page.getByTestId('build-new-model-create')).toBeDisabled()
  await page.getByTestId('build-new-model-name').fill('marts.fct_store_visits')
  await page.getByTestId('build-new-model-kind').selectOption('full')
  await page.getByTestId('build-new-model-create').click()
  await check(page.getByTestId('build-edit-open')).toHaveText('models/marts/fct_store_visits.sql')
  await check(page.getByTestId('build-edit-file-models/marts/fct_store_visits.yml')).toBeVisible()
  await check(page.getByTestId('build-edit-clean')).toBeVisible()

  // The same model again is refused in the tool's words.
  await page.getByTestId('build-edit-new-model').click()
  await page.getByTestId('build-new-model-name').fill('marts.fct_store_visits')
  await page.getByTestId('build-new-model-create').click()
  await check(page.getByTestId('build-new-model-failed')).toContainText('already exists')
  await page.getByTestId('build-new-model-cancel').click()

  // Its query gets a column; the definition has no answer for it yet, so the tool asks, and the answers are the form.
  await page.getByTestId('build-edit-editor').locator('.monaco-editor').click()
  await page.keyboard.press('Control+A')
  await page.keyboard.insertText("SELECT CAST(1 AS BIGINT) AS id, 'walk-in' AS channel\n")
  await check(page.getByTestId('build-definition-update')).toBeDisabled()          // the definition follows the saved file
  await page.getByTestId('build-edit-save').click()
  await check(page.getByTestId('build-edit-saved')).toBeVisible()
  await page.getByTestId('build-definition-update').click()
  await check(page.getByTestId('build-definition-questions')).toBeVisible()
  await check(page.getByTestId('build-definition-answer')).toBeDisabled()          // nothing is answered for the person
  const nullable = page.getByTestId('build-question-Q-define-marts.fct_store_visits-columns.channel.nullable')
  const type = page.getByTestId('build-question-Q-define-marts.fct_store_visits-columns.channel.type')
  await check(nullable).toContainText('Can column `channel` be NULL?')
  await nullable.getByTestId('build-question-option-nullable').check()
  await type.getByTestId('build-question-option-use_type').check()
  await check(page.getByTestId('build-definition-answer')).toBeDisabled()          // a type needs its value
  await type.getByTestId('build-question-value').fill('VARCHAR(40)')
  await page.getByTestId('build-definition-answer').click()
  await check(page.getByTestId('build-definition-written')).toContainText('models/marts/fct_store_visits.yml')
  await check(page.getByTestId('build-edit-clean')).toBeVisible()
  await page.getByTestId('build-edit-file-models/marts/fct_store_visits.yml').click()
  await page.getByTestId('build-edit-mode-yaml').click()
  await check(page.getByTestId('build-edit-editor')).toContainText('name: channel')
  await check(page.getByTestId('build-edit-editor')).toContainText('VARCHAR(40)')
  await page.getByTestId('build-edit-mode-form').click()

  // Import from a connection: the connection's tables are listed (read with the login the server holds, never shown), some are chosen and written as mapped models; what the tool says about the others is listed as markers.
  await page.getByTestId('build-edit-import').click()
  await page.getByTestId('build-import-connection').selectOption('origin')
  await page.getByTestId('build-import-list').click()
  await check(page.getByTestId('build-import-row-humanresources.department')).toContainText('not mapped yet')
  await check(page.getByTestId('build-import-diagnostic').first()).toContainText('DDB-228')
  await check(page.getByTestId('build-import-diagnostic').first()).toHaveAttribute('data-severity', 'warning')
  await check(page.getByTestId('build-import-write')).toBeDisabled()
  await page.getByTestId('build-import-check-humanresources.department').check()
  await page.getByTestId('build-import-check-humanresources.employee').check()
  await page.getByTestId('build-import-write').click()
  await check(page.getByTestId('build-import-written')).toContainText('models/humanresources/department.yml')
  await check(page.getByTestId('build-import-row-humanresources.department')).toContainText('already mapped')
  expect(await page.content()).not.toContain(authoringBuild.origin)                   // the login is the file's path: it is not on the page
  await page.getByTestId('build-edit-file-models/humanresources/department.yml').click()
  await check(page.getByTestId('form-field-name')).toHaveValue('humanresources.department')
  await check(page.getByTestId('build-edit-clean')).toBeVisible()

  // The draft has the model and the two mapped models, for the usual commit and merge.
  await check(page.getByTestId('build-git-change').filter({ hasText: 'models/marts/fct_store_visits.sql' })).toHaveCount(1)
  await check(page.getByTestId('build-git-change').filter({ hasText: 'models/humanresources/employee.yml' })).toHaveCount(1)
})
