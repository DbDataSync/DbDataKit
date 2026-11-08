import { test, expect, BuildPage, screenText } from '../fixtures'

// Every screen draws the project's own data and none shows an error.
test.describe('screens', () => {
  const build = new BuildPage()
  test.beforeAll(async () => { await build.start() })
  test.afterAll(async () => { await build.stop() })

  test('every screen draws the project\'s own data and none shows an error', async ({ page }) => {
    const name = build.firstModel()
    const expectations: [string, string][] = [
      ['#/', 'tests passed'],
      ['#/lineage', name],
      [`#/lineage/${name}`, 'connected'],
      [`#/models/${name}`, 'grain'],
      [`#/models/${name}/sql`, 'SELECT'],
      [`#/models/${name}/lowered`, 'SELECT'],
      ['#/sample', 'Run models on sample data'],
      ['#/diff', 'Compare two tables'],
      ['#/tests', 'tests'],
      ['#/matrix', 'construct'],
    ]
    await build.open(page)
    for (const [route, expected] of expectations) {
      await page.evaluate(r => { location.hash = r }, route)
      await expect(page.locator('main#view'), route).toContainText(expected, { ignoreCase: true })
      const text = await screenText(page)
      expect(text, route).not.toContain('Could not load')
      expect(text, route).not.toContain('[object')                         // a list of elements put in as text: an array that was not flattened
    }
  })
})
