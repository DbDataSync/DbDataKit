import { expect, test } from '@playwright/test'
import { signInNewAdmin } from './people'
import { RUNNER, SERVER, mockWorkspace } from './workspace-fake'

// The preflight before a version is served in production (docs/publishing.md, "Preflight before activation"): a Build project's package published to the Runner's prod and
// served waits for its preflight on the staged version; an error leaves it staged, the publish panel shows the findings with the run's link, and Activate anyway confirms
// them. Re-check on the Runs tab runs the preflight of the version the channel serves through the console and shows what it found. The Workspace server and the console's
// deploy and runs are answered in the page (`workspace-fake.ts`); the real deploy, preflight and activation are ConsoleDeployTests.
// It signs in a new administrator, so its name sorts after `first-run` and `identity-home` (ci-lessons).
test.setTimeout(120_000)

test('a Build package served in prod waits for its preflight: an error keeps it staged until confirmed, and Re-check on the Runs tab shows the findings', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Workspace Preflight', `workspace-preflight-${Date.now()}@example.com`)
  const fake = await mockWorkspace(page)

  // a Build project, a draft of it and a build, made on the console's screens of the Workspace server
  await page.goto(`/servers/${SERVER}`)
  await expect(page.getByTestId('ws-projects')).toBeVisible()
  await page.getByTestId('ws-new-project-open').click()
  await page.getByTestId('ws-new-project-id').fill('sales')
  await page.getByTestId('ws-new-project-kind').selectOption('build')
  await page.getByTestId('ws-new-project-create').click()
  await page.getByTestId('ws-new-name-sales').fill('preflight-edit')
  await page.getByTestId('ws-new-create-sales').click()
  const listed = page.locator('[data-testid="ws-workspace"][data-workspace$="-preflight-edit"]')
  await expect(listed).toBeVisible()
  const draft = (await listed.getAttribute('data-workspace'))!
  await page.getByTestId(`ws-open-${draft}`).click()
  await expect(page.getByTestId('wsp-page')).toBeVisible()
  await page.getByTestId('wsp-nav-files').click()
  await page.getByTestId('wsp-tab-builds').click()
  await page.getByTestId('wsp-build').click()
  const built = page.locator('[data-testid="wsp-build-detail"][data-status="succeeded"]')
  await expect(built).toBeVisible({ timeout: 15_000 })

  // published to prod and served at once: the preflight on the staged version finds an error, so the version stays staged and the panel says what was found
  await built.getByTestId('wsp-publish-target').selectOption(RUNNER)
  await built.getByTestId('wsp-publish-channel').fill('prod')
  await built.getByTestId('wsp-publish-serve').check()
  await built.getByTestId('wsp-publish').click()
  const card = built.getByTestId('wsp-preflight')
  await expect(card).toHaveAttribute('data-activation', 'refused')
  await expect(card.getByTestId('wsp-preflight-words')).toContainText('so the version was not activated')
  await expect(card.getByTestId('wsp-preflight-finding')).toHaveCount(2)
  await expect(card.getByTestId('wsp-preflight-finding').first()).toHaveAttribute('data-severity', 'error')
  await expect(card.getByTestId('wsp-preflight-finding').first()).toContainText('The deployed structure is behind the project')
  await expect(card.getByTestId('wsp-preflight-run-link')).toHaveAttribute('href', new RegExp(`^/servers/${RUNNER}/runs\\?run=`))
  expect(fake.published.at(-1)?.body).toMatchObject({ channel: 'prod', serve: true })

  // Activate anyway: the person confirms what the preflight found, and the version is active
  await card.getByTestId('wsp-preflight-activate').click()
  await expect(card).toHaveAttribute('data-activation', 'activated')
  await expect(card.getByTestId('wsp-preflight-words')).toContainText('the confirmation is kept in the Runner\'s record')
  await expect(built.getByTestId('wsp-preflight-activated')).toHaveText('Version 1 of sales is active in prod.')
  expect(fake.activations).toEqual([expect.objectContaining({ confirm: true })])

  // Re-check on the Runs tab: the preflight of the version prod serves, through the console, and its findings, errors first
  await page.getByTestId('wsp-tab-runs').click()
  await expect(page.getByTestId('wsp-runs-version')).toContainText(`sales version 1 on prod of ${RUNNER}`)
  await page.getByTestId('wsp-runs-preflight-recheck').click()
  const result = page.getByTestId('wsp-runs-result')
  await expect(result).toHaveAttribute('data-outcome', 'succeeded')
  await expect(result.getByTestId('wsp-runs-preflight-summary')).toHaveText('The preflight found 1 error and 1 warning.')
  await expect(result.getByTestId('wsp-preflight-finding').first()).toHaveAttribute('data-severity', 'error')
  await expect(result.getByTestId('wsp-preflight-finding').nth(1)).toContainText('marts.dim_store was changed outside dbdatabuild')
  expect(fake.runs.at(-1)).toMatchObject({ task: 'preflight', served: true, inputs: {} })

  // the newest-check card of prod is that Re-check now: its time, its run's link and what it found
  const newest = page.locator('[data-testid="wsp-newest-check"][data-channel="prod"]')
  await expect(newest).toHaveAttribute('data-errors', '1')
  await expect(newest.getByTestId('wsp-newest-check-when')).toContainText('finished 2026-11-11T11:00:05Z')
  await expect(newest.getByTestId('wsp-newest-check-link')).toHaveAttribute('href', new RegExp(`^/servers/${RUNNER}/runs\\?run=`))
  await expect(newest.getByTestId('wsp-preflight-finding').first()).toHaveAttribute('data-severity', 'error')
  expect(fake.workspaceRuns).toEqual([])
})
