import { expect, test, type Page } from '@playwright/test'
import { signInNewAdmin } from './people'

// Plan 32 RC (report catalogs): the connection form of an external report server (SSRS, Power BI Report Server) on the console's real connections API: the template starts with the Windows sign-in (a user and a password kept as a secret), the file
// is saved, the screen says the sign-in and that the password is not set until it is typed (kept in the secret store, never in the file), and the file is changed to `integrated` (the identity the service runs as: no user and no password in it), after which
// the screen asks for no secret. A `windows` sign-in with no user is refused with its line. The story needs the signed-in console only. It signs in a new administrator, so it sorts after `identity-home` and `first-run`.
// the text is inserted whole: Monaco keeps the indentation of the line before after a newline, so the file is written in flow style (no nested line to dedent)
async function replaceEditorText(page: Page, text: string) {
  await page.getByTestId('outside-yaml').locator('.monaco-editor').click()
  await page.keyboard.press('Control+A')
  await page.keyboard.insertText(text)
}

test('a report server connection starts with the Windows sign-in, keeps its password as a secret, and changes to the integrated sign-in with no secret', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Catalog Tester', `report-catalog-${Date.now()}@example.com`)
  await page.goto('/connections')
  await expect(page.getByTestId('outside-connections')).toBeVisible()

  await page.getByTestId('outside-new-name').fill('ssrs')
  await page.getByTestId('outside-new-kind').selectOption('report-catalog')
  await expect(page.getByTestId('outside-new-meaning')).toContainText('SQL Server Reporting Services')
  await expect(page.getByTestId('outside-new-meaning')).toContainText('integrated, no password in the file')
  await page.getByTestId('outside-new-start').click()
  await expect(page.getByTestId('outside-editor')).toBeVisible()
  const editor = page.getByTestId('outside-yaml').locator('.monaco-editor')
  await expect(editor).toBeVisible()
  await expect(editor).toContainText('report-catalog')
  await expect(editor).toContainText('method: windows')

  // the template is a valid file as it is: the user and the reference of the password; nothing is typed into the file
  await page.getByTestId('outside-save').click()
  const row = page.getByTestId('outside-row')
  await expect(row).toHaveAttribute('data-kind', 'report-catalog')
  await expect(page.getByTestId('outside-kind')).toHaveText('report server (SSRS, Power BI)')
  await expect(page.getByTestId('outside-detail')).toContainText('https://reports.example.com/Reports, Windows sign-in (NTLM or Negotiate, user and password)')
  await expect(page.getByTestId('outside-diagnostic')).toHaveCount(0)
  await expect(page.getByTestId('outside-secret')).toHaveCount(1)
  await expect(page.getByTestId('outside-secret')).toHaveAttribute('data-field', 'password')
  await expect(page.getByTestId('outside-secret')).toHaveAttribute('data-available', 'false')
  await expect(page.getByTestId('outside-test')).toBeVisible()          // a report server can be tried
  await page.getByTestId('outside-secret-input').fill('the-typed-password')
  await page.getByTestId('outside-secret-save').click()
  await expect(page.getByTestId('outside-secret')).toHaveAttribute('data-available', 'true')
  expect(await page.content()).not.toContain('the-typed-password')

  // the integrated sign-in has no user and no password: the file says so, and the screen asks for no secret
  await replaceEditorText(page, 'schema: connection/2\nkind: report-catalog\nname: ssrs\nendpoint: https://reports.example.com/Reports\nreport_catalog: { provider: ssrs }\nsign_in: { method: integrated }\n')
  await page.getByTestId('outside-save').click()
  await expect(page.getByTestId('outside-detail')).toContainText('https://reports.example.com/Reports, Windows sign-in as the service account (integrated)')
  await expect(page.getByTestId('outside-secret')).toHaveCount(0)
  await expect(page.getByTestId('outside-diagnostic')).toHaveCount(0)

  // a Windows sign-in that names no user is refused with its line, and the saved file stays
  await replaceEditorText(page, 'schema: connection/2\nkind: report-catalog\nname: ssrs\nendpoint: https://reports.example.com/Reports\nreport_catalog: { provider: ssrs }\nsign_in: { method: windows, password_secret: "secret:dbdatakit:server:connection:ssrs:password" }\n')
  await page.getByTestId('outside-save').click()
  await expect(page.getByTestId('outside-diagnostic').first()).toBeVisible()
  await expect(page.getByTestId('outside-diagnostic').first()).toContainText('line')
  await expect(page.getByTestId('outside-diagnostic').first()).toHaveAttribute('data-severity', 'error')
  await expect(page.getByTestId('outside-detail')).not.toContainText('user and password')
})
