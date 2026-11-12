import { expect, test } from '@playwright/test'
import { signInNewAdmin } from './people'
import { SERVER, mockWorkspace } from './workspace-fake'

// Plan 27a IX24 (and IX20): the integration editor in a workspace, through the console. Make an integration project, see its tasks, code and tests, add a task (the manifest, a job class and a test change together), break a source file
// and see the check name it with its line, fix it, run the tests (a build) and publish the package to a Runner channel. The Workspace server is mocked in the page (`workspace-fake.ts`), so the story needs the signed-in console only.
// It signs in a new administrator, so its name sorts after `first-run` and `identity-home` (ci-lessons). WRITTEN, NOT RUN where it was written (no console build or browser there): the screens' own logic is vitest (`IntegrationPanel.test.tsx`).
test.setTimeout(120_000)

test('an integration project: tasks, code and tests, a new task, a finding with its file, the tests run as a build, and the package is published to a Runner', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Integration Author', `integration-editor-${Date.now()}@example.com`)
  await mockWorkspace(page)
  await page.goto(`/servers/${SERVER}`)
  await expect(page.getByTestId('ws-projects')).toBeVisible()

  await page.getByTestId('ws-new-project-open').click()
  await page.getByTestId('ws-new-project-id').fill('orders-pull')
  await page.getByTestId('ws-new-project-kind').selectOption('integration')
  await expect(page.getByTestId('ws-new-project-kind-help')).toContainText('Published to')
  await page.getByTestId('ws-new-project-create').click()
  await page.getByTestId('ws-new-name-orders-pull').fill('edit-int')
  await page.getByTestId('ws-new-create-orders-pull').click()
  const listed = page.locator('[data-testid="ws-workspace"][data-workspace$="-edit-int"]')
  await page.getByTestId(`ws-open-${await listed.getAttribute('data-workspace')}`).click()
  await expect(page.getByTestId('wsp-page')).toBeVisible()

  // the overview: the task of the manifest, the class and the test
  await expect(page.locator('[data-testid="wsp-int-task"][data-name="hello"]')).toBeVisible()
  await expect(page.locator('[data-testid="wsp-int-code"][data-path="src/hello.csx"]')).toHaveAttribute('data-errors', '0')
  await expect(page.locator('[data-testid="wsp-int-test"][data-path="tests/hello.csx"]')).toBeVisible()

  // the manifest opens as a form, with its YAML under a tab
  await page.getByTestId('wsp-int-open-manifest').click()
  await expect(page.getByTestId('wsp-structured')).toBeVisible()
  await expect(page.getByTestId('wsp-int-slots-note')).toBeVisible()

  // a new task adds its entry, its class and its test together
  await page.getByTestId('wsp-new-task-name').fill('pull-orders')
  await page.getByTestId('wsp-new-task-go').click()
  await expect(page.locator('[data-testid="wsp-int-task"][data-name="pull-orders"]')).toBeVisible({ timeout: 10_000 })
  await expect(page.locator('[data-testid="wsp-int-code"][data-path="src/pull-orders.csx"]')).toBeVisible()
  await expect(page.locator('[data-testid="wsp-int-test"][data-path="tests/pull-orders.csx"]')).toBeVisible()

  // a source that does not compile: the file is marked with the count of errors, the check says where
  await page.getByTestId('wsp-int-code-open').first().click()
  await page.getByTestId('wsp-code').locator('.monaco-editor').click()
  await page.keyboard.insertText('// BROKEN\n')
  await page.getByTestId('wsp-save').click()
  await expect(page.getByTestId('wsp-dirty')).toHaveCount(0)
  await page.getByTestId('wsp-tab-check').click()
  await page.getByTestId('wsp-check-run').click()   // the Check tab shows nothing until it is asked
  await expect(page.locator('[data-testid="wsp-check-finding"][data-code="DBD-INT-CS1002"]')).toBeVisible({ timeout: 10_000 })
  await page.getByTestId('wsp-code').locator('.monaco-editor').click()
  await page.keyboard.press('Control+A')   // the editor's undo history does not outlive the save, so the file is written again as it was
  await page.keyboard.insertText('namespace Integration;\n\n[RunnerTask("hello")]\npublic sealed class Hello : RunnerJob<NoInput, NoProgress, NoSlots>\n{\n    protected override Task RunAsync(CancellationToken ct) => Task.CompletedTask;\n}\n')
  await page.getByTestId('wsp-save').click()
  await expect(page.getByTestId('wsp-dirty')).toHaveCount(0)

  // run the tests: a build, shown on the Builds tab, then publish to the Runner target
  await page.getByTestId('wsp-int-run-tests').click()
  const done = page.locator('[data-testid="wsp-build-detail"][data-status="succeeded"]')
  await expect(done).toBeVisible({ timeout: 15_000 })
  await done.getByTestId('wsp-publish-target').fill('plant')
  await done.getByTestId('wsp-publish-channel').fill('prod')
  await done.getByTestId('wsp-publish').click()
  await expect(done.locator('[data-testid="wsp-publish-record"][data-ok="true"]')).toContainText('tester to plant')
})
