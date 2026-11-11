import { expect, test } from '@playwright/test'
import fs from 'node:fs'
import path from 'node:path'
import pg from 'pg'
import { modelsBuild, scratchDirectory, warehouseConnection } from '../playwright.config'
import { signInNewAdmin } from './people'

// Plan 33 UE12: publish a project's semantic model to a Gateway from Build's Publish tab, and see what the Gateway serves on its Models screen. The project `shop` (made by playwright.config.ts, Build server on port 8544)
// has a mapped model over sales.facts, which the story creates in the test PostgreSQL (DBDATAKIT_TEST_E2E_PG; it skips without it, because a Gateway publishes a model only when its data source answers and its tables exist), and the
// semantic model `shop_sales` over it. Ada links the Build server to the Gateway (port 8543, its own directory), publishes (version 1, the connection `postgres` published with it), changes the model's description in the
// project's own file and publishes again (version 2: the text is a new version), opens the Gateway's Models screen, serves version 1 again, and retires the model. The Build page's testids are the ones Build has kept
// (`build-publish-proxy`: Build did not rename its words when the Proxy became the Gateway), and the Gateway's words are the Gateway's.
// Order: by file name, after refresh-run; it shares nothing with it (its own servers) and drops the table and schema it made.
const check = expect.configure({ timeout: 60_000 })

async function sql(statements: string[]) {
  const parts = Object.fromEntries(warehouseConnection!.split(';').filter(Boolean).map((p) => [p.slice(0, p.indexOf('=')).trim().toLowerCase(), p.slice(p.indexOf('=') + 1)]))
  const client = new pg.Client({ host: parts.host, port: Number(parts.port ?? 5432), user: parts.username, password: parts.password, database: parts.database })
  await client.connect()
  try { for (const s of statements) await client.query(s) } finally { await client.end() }
}

test.skip(!warehouseConnection, 'set DBDATAKIT_TEST_E2E_PG to run the published models story')

test('publish a semantic model from Build to a Gateway, see its versions on the Gateway, serve the first again and retire it', async ({ page, context, baseURL }) => {
  test.setTimeout(300_000)
  await sql(['CREATE SCHEMA IF NOT EXISTS sales', 'DROP TABLE IF EXISTS sales.facts', 'CREATE TABLE sales.facts (sale_id BIGINT NOT NULL, region VARCHAR(20), amount DECIMAL(14, 2))'])
  try {
    await signInNewAdmin(page, context, baseURL!, 'Ada Lovelace', 'ada.models@example.com')
    await page.goto('/instances')
    for (const [url, codeFile, name] of [
      [`https://localhost:${modelsBuild.port}`, 'models-build-pairing-code.txt', 'Models build'],
      ['https://localhost:8543', 'models-gateway-pairing-code.txt', 'Models gateway'],
    ]) {
      await page.getByTestId('pair-url-input').fill(url!)
      await page.getByTestId('pair-code-input').fill(fs.readFileSync(path.join(scratchDirectory, codeFile!), 'utf8').trim())
      await page.getByTestId('pair-name-input').fill(name!)
      await page.getByTestId('pair-button').click()
      await check(page.getByTestId(`instance-row-${name}`).getByTestId('instance-status')).toHaveText('Ready', { timeout: 30_000 })
    }
    await page.getByTestId('link-from-select').selectOption({ label: 'Models build' })
    await page.getByTestId('link-to-select').selectOption({ label: 'Models gateway' })
    await page.getByTestId('link-button').click()
    await expect(page.getByTestId('link-done')).toBeVisible()

    // The Gateway has nothing published yet: its Models screen says so.
    await page.getByTestId('instance-row-Models gateway').getByTestId('instance-open-link').click()
    await page.getByTestId('rail-models').click()
    await check(page.getByTestId('models-semantic-empty')).toBeVisible()
    await expect(page.getByTestId('models-entity-empty')).toBeVisible()

    // Build's Publish tab: the button is off and says what is missing; then the model goes (with the connection it reads).
    await page.getByTestId('rail-home-link').click()
    await page.getByTestId('instance-row-Models build').getByTestId('instance-open-link').click()
    await page.getByTestId(`build-project-${modelsBuild.project}`).getByTestId('build-project-link').click()
    await page.getByTestId('build-tab-publish').click()
    await expect(page.getByTestId('build-publish-model-button')).toBeDisabled()
    await expect(page.getByTestId('build-publish-model-why')).toContainText('Choose the Gateway')
    await page.getByTestId('build-publish-model-proxy').selectOption({ index: 1 })   // the one Gateway it is linked to
    await page.getByTestId('build-publish-model-name').selectOption(modelsBuild.model)
    await expect(page.getByTestId('build-publish-model-reads')).toContainText('postgres')
    await page.getByTestId('build-publish-model-button').click()
    await check(page.getByTestId('build-publish-model-done')).toContainText('now served')
    await expect(page.getByTestId('build-publish-model-version')).toHaveText('1')

    // The same bytes again are not a new version; a change of the model is. (The description is changed in the project's own file, which the server reads again on every publish.)
    await page.getByTestId('build-publish-model-button').click()
    await check(page.getByTestId('build-publish-model-done')).toContainText('updated')
    await expect(page.getByTestId('build-publish-model-version')).toHaveText('1')
    const file = path.join(modelsBuild.projectDirectory, 'semantic', 'shop_sales.yml')
    fs.writeFileSync(file, fs.readFileSync(file, 'utf8').replace('description: Sales by region.', 'description: Sales by region and channel.'))
    await page.getByTestId('build-publish-model-button').click()
    await check(page.getByTestId('build-publish-model-version')).toHaveText('2')

    // The Gateway's Models screen: two versions kept, version 2 served here; go back to 1, then retire.
    await page.getByTestId('rail-home-link').click()
    await page.getByTestId('instance-row-Models gateway').getByTestId('instance-open-link').click()
    await page.getByTestId('rail-models').click()
    const row = page.getByTestId(`semantic-row-${modelsBuild.model}`)
    await check(row.getByTestId('semantic-version')).toContainText('2')
    await expect(row.getByTestId('semantic-version')).toContainText('2 kept')
    await expect(row.getByTestId('semantic-served')).toContainText('serving version 2')
    await page.getByTestId(`semantic-versions-${modelsBuild.model}`).click()
    await expect(page.getByTestId('semantic-version-1')).toBeVisible()
    await page.getByTestId('semantic-serve-1').click()
    await check(page.getByTestId('models-notice')).toContainText('serves version 1 now')
    await check(row.getByTestId('semantic-served')).toContainText('serving version 1')
    // The model browser (plan 14a SL13): the model's name opens its content, with a measure and the link that follows it to the lineage; back to the list.
    await page.getByTestId(`semantic-browse-${modelsBuild.model}`).click()
    await check(page.getByTestId('model-name')).toHaveText(modelsBuild.model)
    await expect(page.getByTestId('model-version')).toContainText('version 1')
    await expect(page.getByTestId('model-measures').locator('[data-testid^="measure-"]').first()).toBeVisible()
    await expect(page.locator('[data-testid^="lineage-"]').first()).toHaveAttribute('href', /\/lineage\?find=/)
    await page.getByTestId('rail-models').click()
    await page.getByTestId(`semantic-remove-${modelsBuild.model}`).click()
    await expect(page.getByTestId(`semantic-remove-warning-${modelsBuild.model}`)).toContainText('every instance of the group')
    await page.getByTestId(`semantic-remove-confirm-${modelsBuild.model}`).click()
    await check(page.getByTestId('models-semantic-empty')).toBeVisible()
  } finally {
    await sql(['DROP TABLE IF EXISTS sales.facts', 'DROP SCHEMA IF EXISTS sales'])
  }
})
