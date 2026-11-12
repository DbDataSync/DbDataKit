import { expect, type Page } from '@playwright/test'

const by = (page: Page, id: string) => page.getByTestId(id)

/**
 * Actions from a visual (plan 32 DBR53), on the fixture source's sample `retail-order-actions` (a table of orders whose status icon closes the order, a click on a row deletes it; the answers a Store would give are recorded).
 * Console only: no server, no port. A click on a status icon opens the shell's popup in the declaration's words, with the values to be written listed; confirming reports what the Store answered: waiting for approval (the
 * Store's 202), a stale version as a conflict, a broken rule by field; a double click is one call; a delete is destructive and Enter does not confirm it; the page behind the popup is inert and Escape cancels.
 */
export async function actionsStory(page: Page) {
  await page.setViewportSize({ width: 1440, height: 1000 })
  await page.goto('/views/dashboards/retail-order-actions')
  const table = by(page, 'vz-table-orders')
  await expect(table).toBeVisible()
  const order = (number: string) => table.getByRole('row').filter({ hasText: number })

  // 1. A click on a status icon opens a popup that says what will change; nothing is sent yet.
  await order('SO-1001').locator('[data-link="close-order"]').click()
  await expect(by(page, 'vz-action')).toBeVisible()
  await expect(by(page, 'vz-action-title')).toHaveText('Close order SO-1001')
  await expect(by(page, 'vz-action-shows')).toContainText('Acme')
  await expect(by(page, 'vz-action-write-status')).toHaveText('statusClosed')
  await expect(by(page, 'vz-action-writes')).toContainText('orders.orders')
  await expect(by(page, 'vz-action-writes')).toContainText('1001')
  await expect(by(page, 'vz-action-writes')).toContainText('version3')
  await expect(by(page, 'vz-action-forecast')).toContainText('wait for approval by the sales owner')
  await expect(by(page, 'vz-action-confirm')).toHaveText('Close order')
  await expect(by(page, 'vz-dashboard-host')).toHaveAttribute('inert', '')

  // 2. Confirming a model that asks for approval reports "waiting for approval", and a double click is one call.
  await by(page, 'vz-action-confirm').dblclick()
  await expect(by(page, 'vz-action-done')).toHaveAttribute('data-status', 'waiting')
  await expect(by(page, 'vz-action-done')).toContainText('Waiting for approval')
  await expect(by(page, 'vz-action-confirm')).toHaveCount(0)
  await by(page, 'vz-action-cancel').click()
  await expect(by(page, 'vz-action')).toHaveCount(0)
  await expect(by(page, 'vz-dashboard-host')).not.toHaveAttribute('inert', '')

  // 3. A stale row version is reported as a conflict, and the popup stays so the person can read it.
  await order('SO-1002').locator('[data-link="close-order"]').click()
  await expect(by(page, 'vz-action-title')).toHaveText('Close order SO-1002')
  await by(page, 'vz-action-confirm').click()
  await expect(by(page, 'vz-action-failed')).toHaveAttribute('data-status', 'conflict')
  await expect(by(page, 'vz-action-failed')).toContainText('now version 4')
  await expect(by(page, 'vz-action-confirm')).toBeEnabled()
  await page.keyboard.press('Escape')
  await expect(by(page, 'vz-action')).toHaveCount(0)

  // 4. A rule the Store broke is listed by field.
  await order('SO-1003').locator('[data-link="close-order"]').click()
  await by(page, 'vz-action-confirm').click()
  await expect(by(page, 'vz-action-failed')).toContainText('status: An order with an unpaid invoice cannot be closed.')
  await by(page, 'vz-action-cancel').click()

  // 5. A click on the row (not the status) is a delete: destructive, so the popup starts on Cancel and Enter does not confirm it.
  await order('SO-1004').getByText('Umbrella').click()
  await expect(by(page, 'vz-action-title')).toHaveText('Delete order SO-1004')
  await expect(by(page, 'vz-action-confirm')).toHaveText('Delete the row')
  await expect(by(page, 'vz-action-cancel')).toBeFocused()
  await page.keyboard.press('Enter')
  await expect(by(page, 'vz-action')).toBeVisible()
  await expect(by(page, 'vz-action-done')).toHaveCount(0)
  await by(page, 'vz-action-confirm').click()
  await expect(by(page, 'vz-action-done')).toHaveAttribute('data-status', 'applied')

  // 6. A set: three holds are staged (no popup each), the bar says 3 changes, one review lists them; the Store's reason for one line is shown against it
  //    and the set stays; removing that line and submitting applies the rest together.
  await page.reload()
  await expect(table).toBeVisible()
  for (const number of ['SO-1001', 'SO-1002', 'SO-1003']) await order(number).locator('[data-link="hold-order"]').click()
  await expect(by(page, 'vz-action')).toHaveCount(0)
  await expect(by(page, 'vz-pending-count')).toHaveText('3 changes')
  await expect(order('SO-1002')).toHaveAttribute('data-pending', '')
  await by(page, 'vz-pending-review').click()
  await expect(by(page, 'vz-pending-title')).toHaveText('Review 3 changes')
  await expect(by(page, 'vz-pending-line-1')).toContainText('status: Held')
  await expect(by(page, 'vz-pending-forecast')).toContainText('apply together')
  await by(page, 'vz-pending-submit').click()
  await expect(by(page, 'vz-pending-failed')).toHaveAttribute('data-status', 'refused')
  await expect(by(page, 'vz-pending-problem-2')).toContainText('status: An order with an unpaid invoice cannot be held.')
  await expect(by(page, 'vz-pending-title')).toHaveText('Review 3 changes')          // the set is kept
  await by(page, 'vz-pending-remove-2').click()
  await expect(by(page, 'vz-pending-title')).toHaveText('Review 2 changes')
  await by(page, 'vz-pending-submit').click()
  await expect(by(page, 'vz-pending-done')).toContainText('2 changes were made together')
  await expect(by(page, 'vz-pending-bar')).toHaveCount(0)
  await by(page, 'vz-pending-close').click()

  // 7. Leaving the page with changes pending asks.
  await order('SO-1004').locator('[data-link="hold-order"]').click()
  await expect(by(page, 'vz-pending-count')).toHaveText('1 change')
  expect(await page.evaluate(() => { const e = new Event('beforeunload', { cancelable: true }); window.dispatchEvent(e); return e.defaultPrevented })).toBe(true)
  await by(page, 'vz-pending-discard').click()
  await expect(by(page, 'vz-pending-bar')).toHaveCount(0)
}
