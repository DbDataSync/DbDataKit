import { expect, test, type Page } from '@playwright/test'
import { signInNewAdmin } from './people'
import { SERVER, mockWorkspace } from './workspace-fake'

// The two areas of a draft (Files and Designer, DbDataWorkspace/README.md) and the sync Designer, through the console against a Workspace server
// mocked in the page (`workspace-fake.ts`), so the story needs the signed-in console only and takes no port. It signs in a new administrator, so its
// name sorts after `first-run` and `identity-home` (ci-lessons: a spec that signs in a new administrator sorts after them).
test.setTimeout(120_000)

async function makeDraft(page: Page, id: string, kind: string, workspace: string) {
  await page.goto(`/servers/${SERVER}`)
  await expect(page.getByTestId('ws-projects')).toBeVisible()
  await page.getByTestId('ws-new-project-open').click()
  await page.getByTestId('ws-new-project-id').fill(id)
  await page.getByTestId('ws-new-project-kind').selectOption(kind)
  await page.getByTestId('ws-new-project-create').click()
  await expect(page.locator(`[data-testid="ws-project"][data-project="${id}"]`)).toBeVisible()
  await page.getByTestId(`ws-new-name-${id}`).fill(workspace)
  await page.getByTestId(`ws-new-create-${id}`).click()
  const listed = page.locator(`[data-testid="ws-workspace"][data-workspace$="-${workspace}"]`)
  await expect(listed).toBeVisible()
  await page.getByTestId(`ws-open-${await listed.getAttribute('data-workspace')}`).click()
  await expect(page.getByTestId('wsp-page')).toBeVisible()
}

const area = (page: Page) => page.getByTestId('wsp-page')

/** A picture of the screen for people reviewing the Designer, written only when DBDATAKIT_TEST_SHOTS names a folder (not in CI). */
async function shot(page: Page, name: string) {
  const folder = process.env.DBDATAKIT_TEST_SHOTS
  if (folder) await page.screenshot({ path: `${folder}/${name}.png`, fullPage: false })
}

test('a draft has two areas: an app draft shows Files only, a sync draft opens on its Designer, and an unsaved edit and the address survive moving between them', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Sync Designer', `sync-designer-${Date.now()}@example.com`)
  await mockWorkspace(page)

  // a kind without a Designer: only Files, and no Designer entries
  await makeDraft(page, 'site', 'app', 'site-edit')
  await expect(area(page)).toHaveAttribute('data-area', 'files')
  await expect(page).toHaveURL(/\/files$/)
  await expect(page.getByTestId('wsp-nav-designer')).toHaveCount(0)

  // a sync draft lands on its Designer, on its first replication; the navigation lists the replications under Designer and the files under Files
  await makeDraft(page, 'plant', 'sync', 'designer-edit')
  await expect(area(page)).toHaveAttribute('data-area', 'designer')
  await expect(page).toHaveURL(/\/designer\/replications\/orders$/)
  await expect(page.locator('[data-testid="sd-nav-replication"]')).toHaveCount(1)
  await expect(page.locator('[data-testid="sd-nav-replication"][data-name="orders"]')).toHaveAttribute('aria-current', 'page')
  await expect(page.locator('[data-testid="wsp-file"][data-path="config/replications/orders/task.yaml"]')).toBeVisible()
  await shot(page, '01-designer-landing')

  // an edit in Files left not saved is still there, not saved, after a click into the Designer and back
  await page.getByTestId('wsp-nav-files').click()
  await expect(area(page)).toHaveAttribute('data-area', 'files')
  await page.getByTestId('wsp-new-path').fill('notes.md')
  await page.getByTestId('wsp-new-file').click()
  await expect(page.getByTestId('wsp-editor')).toHaveAttribute('data-path', 'notes.md')
  await page.getByTestId('wsp-code').locator('.monaco-editor').click()
  await page.keyboard.insertText('Orders move every quarter hour.')
  await expect(page.getByTestId('wsp-dirty')).toBeVisible()
  await expect(page.getByTestId('wsp-unsaved')).toHaveAttribute('data-count', '1')
  await page.locator('[data-testid="sd-nav-replication"][data-name="orders"]').click()
  await expect(area(page)).toHaveAttribute('data-area', 'designer')
  await expect(page.getByTestId('wsp-unsaved')).toHaveAttribute('data-count', '1')
  await expect(page.locator('[data-testid="wsp-file"][data-path="notes.md"]')).toHaveAttribute('data-dirty', 'true')
  await page.locator('[data-testid="wsp-file"][data-path="notes.md"]').click()
  await expect(page.getByTestId('wsp-dirty')).toBeVisible()
  await expect(page.getByTestId('wsp-code')).toContainText('Orders move every quarter hour.')
  await shot(page, '02-files-unsaved-edit')

  // the chrome's Save is the same in both areas
  await page.getByTestId('wsp-save-all').click()
  await expect(page.getByTestId('wsp-unsaved')).toHaveCount(0)

  // a reload keeps the area the address names
  await page.reload()
  await expect(area(page)).toHaveAttribute('data-area', 'files')
  await expect(page.getByTestId('wsp-editor')).toHaveAttribute('data-path', 'notes.md')
  await page.locator('[data-testid="sd-nav-replication"][data-name="orders"]').click()
  await expect(page).toHaveURL(/\/designer\/replications\/orders$/)
  await page.reload()
  await expect(area(page)).toHaveAttribute('data-area', 'designer')
  await expect(page.getByTestId('sd-replication')).toHaveAttribute('data-name', 'orders')
})
