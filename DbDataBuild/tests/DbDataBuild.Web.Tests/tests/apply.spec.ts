import { test, expect, BuildPage, screenText } from '../fixtures'

// The apply panel is only there when the server allows it (`ui mcp --allow-apply`).
test.describe('apply panel', () => {
  const closed = new BuildPage(false)
  const allowing = new BuildPage(true)
  test.beforeAll(async () => { await closed.start(); await allowing.start() })
  test.afterAll(async () => { await closed.stop(); await allowing.stop() })

  test('a server started without --allow-apply says so and offers nothing', async ({ page }) => {
    const plan = closed.addPlan('2026-10-06-ffff0006')
    await closed.open(page, '#/plans/' + encodeURIComponent(plan))
    await expect(page.locator('main#view')).toContainText('was not started with --allow-apply')
  })

  test('a server started with --allow-apply shows the confirmation, apply and dry run', async ({ page }) => {
    const plan = allowing.addPlan('2026-10-06-ffff0006')
    await allowing.open(page, '#/plans/' + encodeURIComponent(plan))
    await expect(page.locator('main#view')).toContainText('I allow the destructive steps on marts.customers')
    const text = await screenText(page)
    expect(text).toContain('Apply to postgres')
    expect(text).toContain('Dry run')
  })
})
