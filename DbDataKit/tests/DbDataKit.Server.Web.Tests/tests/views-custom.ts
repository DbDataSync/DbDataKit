import { expect, type Page } from '@playwright/test'

const by = (page: Page, id: string) => page.getByTestId(id)

// JSON is YAML
const DOCUMENT = JSON.stringify({
  schemaVersion: 1, kind: 'dashboard', title: 'Custom board',
  widgets: { export: { type: 'custom', component: 'sample/Export' } },
  layouts: [{ name: 'desk', type: 'reflow', default: true, grid: { columns: 12 }, tiles: [{ widget: 'export', x: 0, y: 0, w: 12, h: 2 }] }],
})

/**
 * The `custom` widget in the editor (plan 32 DBR30): a component is code of an app, so the palette of a source that lists no app components has no `custom` button (there is no placeholder name to invent), and a document that
 * names a component is edited in the form with the component's name shown, a select that keeps a name the source does not list, and the note that says where its code is edited. (A source that lists components, and the palette
 * offering them, is `EditorHost.customWidgets`, covered by `editing.test.ts`: no console source lists any yet.)
 */
export async function customStory(page: Page) {
  await page.setViewportSize({ width: 1440, height: 1500 })
  await page.getByTestId('rail-views').click()
  await page.getByTestId('views-tab-dashboards').click()
  await by(page, 'views-new').click()
  await expect(by(page, 'ed-root')).toBeVisible()
  await by(page, 'ed-file-name').fill('custom-board')

  // 1. No component listed: the palette has the standard widgets and no custom one.
  await expect(by(page, 'ed-add-metric')).toBeVisible()
  await expect(by(page, 'ed-add-custom')).toHaveCount(0)

  // 2. A file that names a component, opened as a file of the fixture source and edited: the form shows it.
  await page.goto('/views/dashboards')
  await by(page, 'views-open-file').setInputFiles({ name: 'custom-board.yaml', mimeType: 'application/yaml', buffer: Buffer.from(DOCUMENT) })
  await by(page, 'views-edit').click()
  await expect(by(page, 'ed-widget-export')).toBeVisible()
  await by(page, 'ed-widget-edit-export').click()
  const select = by(page, 'ed-custom-component-export')
  await expect(select).toBeVisible()
  await expect(select).toHaveValue('sample/Export')
  await expect(by(page, 'ed-custom-note-export')).toContainText('Code of an app (sample/Export)')
}
