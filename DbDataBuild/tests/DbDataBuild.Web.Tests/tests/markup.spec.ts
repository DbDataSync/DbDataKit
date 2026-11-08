import fs from 'node:fs'
import { test, expect, BuildPage, screenText } from '../fixtures'

// Text from the project is shown as text, never as markup.
test.describe('project text', () => {
  const build = new BuildPage()
  test.beforeAll(async () => { await build.start() })
  test.afterAll(async () => { await build.stop() })

  test('a model with markup in a comment shows the characters and runs nothing', async ({ page }) => {
    fs.appendFileSync(build.modelFile(), '\n-- <img src=x onerror="document.title=\'pwned\'"> <script>document.title=\'pwned\'</script>\n')
    await build.open(page, `#/models/${build.firstModel()}/sql`)
    await expect(page.locator('main#view')).toContainText('<script>document.title')
    const text = await screenText(page)
    expect(text).toContain('<img src=x')
    expect(text).toContain('<script>document.title')
    expect(await page.title()).not.toBe('pwned')
  })
})
