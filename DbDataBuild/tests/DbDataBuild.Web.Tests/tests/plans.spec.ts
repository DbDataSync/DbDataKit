import { test, expect, BuildPage, screenText } from '../fixtures'

// The plans screen lists plans, shows one with its statements and flags an edited one.
test.describe('plans screen', () => {
  const build = new BuildPage()
  test.beforeAll(async () => { await build.start() })
  test.afterAll(async () => { await build.stop() })

  test('lists plans, shows one with its statements and flags an edited one', async ({ page }) => {
    const first = build.addPlan('2026-10-01-aaaa0001', undefined, '# Report of the plan\n')
    const edited = build.addPlan('2026-10-02-bbbb0002', t => t.replace('DROP COLUMN legacy', 'DROP COLUMN other'))

    await build.open(page, '#/plans')
    await expect(page.locator('main#view')).toContainText('2026-10-01-aaaa0001')
    const list = await screenText(page)
    expect(list).toContain('⚠ 2026-10-02-bbbb0002')                       // the edited one is marked

    await page.evaluate(r => { location.hash = r }, '#/plans/' + encodeURIComponent(first))
    await expect(page.locator('main#view')).toContainText('Report of the plan')
    const one = await screenText(page)
    expect(one).toContain('ALTER TABLE marts.customers DROP COLUMN legacy;')
    expect(one).toContain('--allow-destructive marts.customers')
    expect(one).toContain('a note')
    expect(one).not.toContain('null')                                      // an absent panel is left out, not drawn as text

    await page.evaluate(r => { location.hash = r }, '#/plans/' + encodeURIComponent(edited))
    await expect(page.locator('main#view')).toContainText('hash does not match')
  })
})
