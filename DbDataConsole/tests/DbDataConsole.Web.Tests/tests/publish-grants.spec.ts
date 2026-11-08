import { expect, test } from '@playwright/test'
import fs from 'node:fs'
import path from 'node:path'
import { grantsBuild, scratchDirectory } from '../playwright.config'
import { signInNewAdmin } from './people'

// Plan 22a GP03 (the owner's decision of 2026-11-08: who may read a data source is part of the published definition, no merge): the grants are authored in Build and shown, never edited, on the Gateway.
// Servers: the Build server `grantsdemo` (port 8581) and a Gateway (8582 Flight SQL, 8583 management), both made by playwright.config.ts; no database is needed, a publish only writes the data source file.
// 1. Build's Edit tab, in a workspace: the connection file `connections/postgres.yaml` has the card "Who may use it once published" (the project's `grants`, group `finance`); Ada adds `auditors`, saves, and the compiler
//    finds nothing to fix (the file is a connection/2 file Build reads).
// 2. The Publish tab publishes the project's connection (the file on the server's disk, which has `finance` only: the draft is not merged) as the data source `ledger`.
// 3. The Gateway's Data sources screen shows the grants with the sentence 'Granted by the published definition; change them in the project and publish again' and no control that changes them.
// 4. The project's file changes on disk to `auditors` and `analysts`; publishing again replaces the grants exactly: `finance` is gone, the two others are there.
// Order: by file name, after identity-home (it invites an administrator); it shares nothing with the other stories (its own servers and directories).
const check = expect.configure({ timeout: 60_000 })

test('grants are authored in Build, published with the data source, shown read-only on the Gateway and replaced by the next publish', async ({ page, context, baseURL }) => {
  test.setTimeout(300_000)
  await signInNewAdmin(page, context, baseURL!, 'Ada Lovelace', 'ada.grants@example.com')
  await page.goto('/instances')
  for (const [url, codeFile, name] of [
    [`https://localhost:${grantsBuild.port}`, 'grants-build-pairing-code.txt', 'Grants build'],
    ['https://localhost:8583', 'grants-gateway-pairing-code.txt', 'Grants gateway'],
  ]) {
    await page.getByTestId('pair-url-input').fill(url!)
    await page.getByTestId('pair-code-input').fill(fs.readFileSync(path.join(scratchDirectory, codeFile!), 'utf8').trim())
    await page.getByTestId('pair-name-input').fill(name!)
    await page.getByTestId('pair-button').click()
    await check(page.getByTestId(`instance-row-${name}`).getByTestId('instance-status')).toHaveText('Ready', { timeout: 30_000 })
  }
  await page.getByTestId('link-from-select').selectOption({ label: 'Grants build' })
  await page.getByTestId('link-to-select').selectOption({ label: 'Grants gateway' })
  await page.getByTestId('link-button').click()
  await expect(page.getByTestId('link-done')).toBeVisible()

  // 1. The editor in Build: the connection file's grants, one more group, saved; the compiler accepts the file.
  await page.getByTestId('instance-row-Grants build').getByTestId('instance-open-link').click()
  await page.getByTestId(`build-project-${grantsBuild.project}`).getByTestId('build-project-link').click()
  await page.getByTestId('build-tab-edit').click()
  await page.getByTestId('build-edit-workspace-name').fill('grants')
  await page.getByTestId('build-edit-workspace-make').click()
  await check(page.getByTestId('build-edit-file-connections/postgres.yaml')).toBeVisible()
  await page.getByTestId('build-edit-file-connections/postgres.yaml').click()
  await check(page.getByTestId('build-grants')).toBeVisible()
  await expect(page.getByTestId('build-grants-note')).toContainText('replaces the Gateway’s grants completely')
  await expect(page.getByTestId('build-grants-group-finance')).toBeVisible()
  await page.getByTestId('build-grants-group-input').fill('finance')
  await expect(page.getByTestId('build-grants-group-problem')).toContainText('listed already')
  await expect(page.getByTestId('build-grants-add')).toBeDisabled()
  await page.getByTestId('build-grants-group-input').fill('auditors')
  await page.getByTestId('build-grants-add').click()
  await expect(page.getByTestId('build-grants-group-auditors')).toBeVisible()
  await page.getByTestId('build-edit-save').click()
  await check(page.getByTestId('build-edit-saved')).toBeVisible()
  await check(page.getByTestId('build-edit-clean')).toBeVisible()

  // 2. The Publish tab: the connection goes as the data source `ledger` (the project on the server's disk says `finance`).
  await page.getByTestId('build-tab-publish').click()
  await page.getByTestId('build-publish-proxy').selectOption({ index: 1 })   // the one Gateway it is linked to
  await page.getByTestId('build-publish-connection').fill(grantsBuild.connection)
  await page.getByTestId('build-publish-data-source').fill('ledger')
  await page.getByTestId('build-publish-button').click()
  await check(page.getByTestId('build-publish-done')).toContainText('now')

  // 3. The Gateway shows them, and changes nothing.
  const openLedger = async () => {
    await page.getByTestId('rail-home-link').click()
    await page.getByTestId('instance-row-Grants gateway').getByTestId('instance-open-link').click()
    await page.getByTestId('rail-data-sources').click()
    await check(page.getByTestId('data-source-ledger')).toBeVisible()
    await page.getByTestId('data-source-ledger').getByTestId('data-source-link').click()
    await check(page.getByTestId('data-source-grants')).toBeVisible()
  }
  await openLedger()
  await expect(page.getByTestId('data-source-grants-source')).toHaveText('Granted by the published definition; change them in the project and publish again')
  await expect(page.getByTestId('data-source-grants-group-finance')).toBeVisible()
  await expect(page.getByTestId('data-source-grants-group-auditors')).toHaveCount(0)
  await expect(page.getByTestId('data-source-grants').locator('input, button, select, textarea')).toHaveCount(0)

  // 4. The project changes its list; the next publish replaces the Gateway's grants exactly.
  const file = path.join(grantsBuild.projectDirectory, 'connections', 'postgres.yaml')
  fs.writeFileSync(file, fs.readFileSync(file, 'utf8').replace('groups: [finance]', 'groups: [auditors, analysts]'))
  await page.getByTestId('rail-home-link').click()
  await page.getByTestId('instance-row-Grants build').getByTestId('instance-open-link').click()
  await page.getByTestId(`build-project-${grantsBuild.project}`).getByTestId('build-project-link').click()
  await page.getByTestId('build-tab-publish').click()
  await page.getByTestId('build-publish-proxy').selectOption({ index: 1 })
  await page.getByTestId('build-publish-connection').fill(grantsBuild.connection)
  await page.getByTestId('build-publish-data-source').fill('ledger')
  await page.getByTestId('build-publish-button').click()
  await check(page.getByTestId('build-publish-done')).toContainText('updated')
  await openLedger()
  await expect(page.getByTestId('data-source-grants-group-auditors')).toBeVisible()
  await expect(page.getByTestId('data-source-grants-group-analysts')).toBeVisible()
  await expect(page.getByTestId('data-source-grants-group-finance')).toHaveCount(0)
})
