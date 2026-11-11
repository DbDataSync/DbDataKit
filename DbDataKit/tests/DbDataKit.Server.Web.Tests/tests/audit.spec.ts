import { expect, test } from '@playwright/test'
import { signInNewAdmin } from './people'

// The console's Audit page (UI05): a person who redeems an invitation is the first thing the console records, so a new administrator finds their own redemption and sign-in in the list, can narrow it by what was done and by
// outcome, and reads their own name as the person. It signs in a new administrator, so it sorts after `identity-home` and `first-run`.
test('the Audit page lists the administrator\'s own redemption and narrows by what was done', async ({ page, context, baseURL }) => {
  const name = `Audit Tester ${Date.now()}`
  await signInNewAdmin(page, context, baseURL!, name, `audit-${Date.now()}@example.com`)

  await page.getByTestId('rail-audit').click()
  await expect(page.getByTestId('audit-page')).toBeVisible()
  const redeemed = page.getByTestId('audit-row').filter({ hasText: 'redeemed an invitation' })
  await expect(redeemed.first()).toContainText(name)
  await expect(redeemed.first().getByTestId('audit-outcome-text')).toHaveText('Done')

  await page.getByTestId('audit-verb').selectOption('server.')
  await expect(page.getByTestId('audit-none')).toBeVisible()
  await page.getByTestId('audit-clear').click()
  await expect(redeemed.first()).toBeVisible()

  await page.getByTestId('audit-outcome').selectOption('refused')
  await expect(page.getByTestId('audit-none')).toBeVisible()
  await page.getByTestId('audit-clear').click()

  await redeemed.first().getByTestId('audit-actor').click()
  await expect(page.getByTestId('audit-clear-actor')).toBeVisible()
  await expect(redeemed.first()).toBeVisible()
})
