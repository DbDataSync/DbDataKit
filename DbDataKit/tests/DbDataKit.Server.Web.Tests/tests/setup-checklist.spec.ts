import { expect, test } from '@playwright/test'
import { signInNewAdmin } from './people'

// Plan 23a FQ21: the setup checklist. A new administrator signs in with a passkey, so the sign-in step is finished by what the install has; the servers are answered empty in the page (the other stories pair servers into this
// console, and the checklist reads what is there), so the steps that need one are still to do. An item opens its page; skipping one takes it off the Home banner and not off the checklist, where it can be put back; a step is
// never finished by a click. The skip is kept by the console (`/api/setup/items`, the `setup` module) and put back at the end so the story can run again. Sorts after `identity-home` and `first-run`.
test('the checklist is finished by what the install has, a skipped step leaves the banner and stays on the list', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Setup Tester', `setup-${Date.now()}-${Math.random().toString(36).slice(2, 8)}@example.com`)
  await page.route('**/api/instances', (route) => (route.request().method() === 'GET' ? route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }) : route.fallback()))

  // the banner on Home counts what is left: the servers, the connections, the alerts of failed checks and the Build project (sign-in is finished: a person has a passkey; the test console has a delivery channel, so that step is finished too)
  await page.goto('/')
  const banner = page.getByTestId('setup-banner')
  await expect(banner).toHaveAttribute('data-left', '4')
  await banner.getByTestId('setup-banner-open').click()

  await expect(page.getByTestId('setup-page')).toBeVisible()
  await expect(page.getByTestId('setup-item-sign-in')).toHaveAttribute('data-status', 'done')
  await expect(page.getByTestId('setup-item-servers')).toHaveAttribute('data-status', 'todo')
  await expect(page.getByTestId('setup-item-git-remotes')).toHaveAttribute('data-status', 'unavailable')
  await expect(page.getByTestId('setup-item-delivery-channels')).toHaveAttribute('data-status', 'done')
  await expect(page.getByTestId('setup-item-quality-alerts')).toHaveAttribute('data-status', 'todo')
  await expect(page.getByTestId('setup-skip-sign-in')).toHaveCount(0)             // a finished step is not skipped

  // skipping hides it from the banner and keeps it in the list
  await page.getByTestId('setup-skip-connections').click()
  await expect(page.getByTestId('setup-item-connections')).toHaveAttribute('data-status', 'skipped')
  await expect(page.getByTestId('setup-summary')).toContainText('3 steps left')
  await page.goto('/')
  await expect(page.getByTestId('setup-banner')).toHaveAttribute('data-left', '3')

  // an item opens its page
  await page.goto('/setup')
  await page.getByTestId('setup-open-servers').click()
  await expect(page.getByTestId('instances-page')).toBeVisible()

  // put it back
  await page.goto('/setup')
  await page.getByTestId('setup-unskip-connections').click()
  await expect(page.getByTestId('setup-item-connections')).toHaveAttribute('data-status', 'todo')
})
