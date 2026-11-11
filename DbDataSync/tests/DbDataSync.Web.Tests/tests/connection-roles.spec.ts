import path from 'node:path'
import { test, expect } from '../fixtures'
import { screenshotDir } from '../screenshots'
import { MSSQL_PORT, SA_PASSWORD } from '../test-db'

const screenshotsDir = screenshotDir('connection-roles')

// Plan 28c E20: the connection screen's two logins, read and run, against the real SQL Server of testenv. The read login is the form's own (this server browses, tests and previews with it); a second, run login is the one a replication pass connects with.
// The story gives the run login a wrong password first (the read test works, the run test fails and the screen never shows the password), then the right one (both work), then takes the second login away. It needs the shared SQL Server and nothing else: the Sync server and the console of
// the suite are the ones every spec uses, so it takes no port of its own. It cleans up its connection.
const NAME = `roles-e2e-${Date.now()}`
const WRONG = 'not-the-run-password'

async function open(page: import('@playwright/test').Page) {
  await page.goto(`/connections/${NAME}`)
  await expect(page.getByTestId('connection-name-input')).toHaveValue(NAME)
}

test.describe.serial('connection screen: read and run logins (plan 28c E20)', () => {
  test('a connection is saved with a second, run login', async ({ page }) => {
    await page.goto('/connections')
    await page.getByTestId('new-connection-button').click()
    await expect(page).toHaveURL(/\/connections\/new$/)
    await page.getByTestId('connection-name-input').fill(NAME)
    await page.getByTestId('connection-parameters-host').fill('localhost')
    await page.getByTestId('connection-parameters-port').fill(String(MSSQL_PORT))
    await page.getByTestId('connection-parameters-database').fill('master')
    await page.getByTestId('connection-parameters-userId').fill('sa')
    await page.getByTestId('connection-parameters-password').fill(SA_PASSWORD)
    const tlsTable = page.getByTestId('connection-parameters-properties')
    await tlsTable.getByPlaceholder('Custom properties name').fill('TrustServerCertificate')
    await tlsTable.getByRole('button', { name: '+ Add' }).click()
    await tlsTable.getByLabel('TrustServerCertificate value').fill('true')

    // one login serves both until a second is asked for: the card starts without one
    await expect(page.getByTestId('connection-run-login-card')).toBeVisible()
    await expect(page.getByTestId('connection-run-user-input')).toHaveCount(0)
    await page.getByTestId('connection-run-login-toggle').check()
    await page.getByTestId('connection-run-user-input').fill('sa')
    await page.getByTestId('connection-run-password-input').fill(WRONG)
    await page.screenshot({ path: path.join(screenshotsDir, '01-run-login.png'), fullPage: true })
    await page.getByTestId('save-connection-button').click()
    await expect(page.getByTestId('connections-table')).toContainText(NAME)
  })

  test('the read login tests fine, the run login fails and its password is never shown', async ({ page }) => {
    await open(page)
    await expect(page.getByTestId('connection-run-login-toggle')).toBeChecked()
    await expect(page.getByTestId('connection-run-user-input')).toHaveValue('sa')
    // never sent back: blank, with the placeholder saying the stored one is kept
    await expect(page.getByTestId('connection-run-password-input')).toHaveValue('')
    await expect(page.getByTestId('connection-run-secret-ref')).toHaveValue(`dbdatakit:sync:connection:${NAME}:run`)
    await expect(page.getByTestId('credential-run-env-var')).toHaveValue(/_RUN$/)

    await page.getByTestId('test-connection-button').click()
    await expect(page.getByTestId('connection-test-result').locator('.dot-ok')).toBeVisible({ timeout: 20_000 })

    await page.getByTestId('test-run-login-button').click()
    const result = page.getByTestId('run-login-test-result')
    await expect(result).toContainText('run login failed', { timeout: 20_000 })
    await expect(result.locator('.dot-bad')).toBeVisible()
    await expect(page.locator('body')).not.toContainText(WRONG)
    await page.screenshot({ path: path.join(screenshotsDir, '02-run-login-failed.png'), fullPage: true })
  })

  test('a corrected run password makes the run login work; a blank one keeps it', async ({ page }) => {
    await open(page)
    await page.getByTestId('connection-run-password-input').fill(SA_PASSWORD)
    await page.getByTestId('save-connection-button').click()
    await expect(page.getByTestId('connections-table')).toContainText(NAME)

    await open(page)
    await page.getByTestId('test-run-login-button').click()
    await expect(page.getByTestId('run-login-test-result')).toContainText('run login works', { timeout: 20_000 })
    await expect(page.getByTestId('run-login-test-result').locator('.dot-ok')).toBeVisible()

    // saved again with the password blank: the stored one stays
    await page.getByTestId('save-connection-button').click()
    await expect(page.getByTestId('connections-table')).toContainText(NAME)
    await open(page)
    await page.getByTestId('test-run-login-button').click()
    await expect(page.getByTestId('run-login-test-result')).toContainText('run login works', { timeout: 20_000 })
  })

  test('taking the second login away leaves one login for both roles, and the connection is removed', async ({ page }) => {
    await open(page)
    await page.getByTestId('connection-run-login-toggle').uncheck()
    await expect(page.getByTestId('connection-run-user-input')).toHaveCount(0)
    await page.getByTestId('save-connection-button').click()
    await expect(page.getByTestId('connections-table')).toContainText(NAME)

    await open(page)
    await expect(page.getByTestId('connection-run-login-toggle')).not.toBeChecked()
    await expect(page.getByTestId('credential-run-env-var')).toHaveCount(0)
    await page.getByTestId('test-connection-button').click()
    await expect(page.getByTestId('connection-test-result').locator('.dot-ok')).toBeVisible({ timeout: 20_000 })

    await page.getByTestId(`delete-connection-${NAME}`).click()
    await expect(page.getByTestId('connections-table')).not.toContainText(NAME)
  })
})
