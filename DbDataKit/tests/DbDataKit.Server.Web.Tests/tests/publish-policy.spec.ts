import { expect, test } from '@playwright/test'
import fs from 'node:fs'
import path from 'node:path'
import { policyBuild, scratchDirectory } from '../playwright.config'
import { signInNewAdmin } from './people'

// Plan 13c D19 (the owner's decision of 2026-11-09, corrected the same day): the Gateway's serving policy is part of the published definition, and a publisher adopts a data source made by hand by publishing over it.
// Servers: the Build server `policydemo` (port 8584) and a Gateway (8585 Flight SQL, 8586 management), both made by playwright.config.ts; the Gateway has a data source `erp` made by hand with allow_writes, a batch size and
// trusted directories. No database is needed: a publish only writes the data source file.
// 1. Build's Edit tab: the connection file `connections/postgres.yaml` has the card "How the Gateway serves it" with a field for each policy setting and none for allow_writes or the trusted directories; Ada sets
//    the statement check, saves.
// 2. The Publish tab publishes the project's connection (the file on the server's disk: batch size 500) as `erp`, which the Gateway has by hand: refused, with the offer to publish over it; Ada does, and the answer lists what the
//    definition changed and dropped, allow_writes and the directories among the dropped.
// 3. The Gateway's Data sources screen shows the policy read-only as the definition's, with batch size 500, and no allow_writes.
// Order: by file name, after publish-grants (it shares nothing with it: its own servers and directories).
const check = expect.configure({ timeout: 60_000 })

test('the serving policy is authored in Build, adopts a data source made by hand with a diff, and is shown read-only on the Gateway', async ({ page, context, baseURL }) => {
  test.setTimeout(300_000)
  await signInNewAdmin(page, context, baseURL!, 'Ada Lovelace', 'ada.policy@example.com')
  await page.goto('/instances')
  for (const [url, codeFile, name] of [
    [`https://localhost:${policyBuild.port}`, 'policy-build-pairing-code.txt', 'Policy build'],
    ['https://localhost:8586', 'policy-gateway-pairing-code.txt', 'Policy gateway'],
  ]) {
    await page.getByTestId('pair-url-input').fill(url!)
    await page.getByTestId('pair-code-input').fill(fs.readFileSync(path.join(scratchDirectory, codeFile!), 'utf8').trim())
    await page.getByTestId('pair-name-input').fill(name!)
    await page.getByTestId('pair-button').click()
    await check(page.getByTestId(`instance-row-${name}`).getByTestId('instance-status')).toHaveText('Ready', { timeout: 30_000 })
  }
  await page.getByTestId('link-from-select').selectOption({ label: 'Policy build' })
  await page.getByTestId('link-to-select').selectOption({ label: 'Policy gateway' })
  await expect(page.getByTestId('link-trust')).toContainText('The Publisher role is high-trust')
  await page.getByTestId('link-button').click()
  await expect(page.getByTestId('link-done')).toBeVisible()

  // 1. The editor in Build.
  await page.getByTestId('instance-row-Policy build').getByTestId('instance-open-link').click()
  await page.getByTestId(`build-project-${policyBuild.project}`).getByTestId('build-project-link').click()
  await page.getByTestId('build-tab-edit').click()
  await page.getByTestId('build-edit-workspace-name').fill('policy')
  await page.getByTestId('build-edit-workspace-make').click()
  await check(page.getByTestId('build-edit-file-connections/postgres.yaml')).toBeVisible()
  await page.getByTestId('build-edit-file-connections/postgres.yaml').click()
  await check(page.getByTestId('build-policy')).toBeVisible()
  await expect(page.getByTestId('build-policy-note')).toContainText('replaces the Gateway’s policy for this data source completely')
  await expect(page.getByTestId('build-policy-input-batch_size')).toHaveValue('500')
  await expect(page.getByTestId('build-policy-field-allow_writes')).toHaveCount(0)
  await expect(page.getByTestId('build-policy-field-allowed_directories')).toHaveCount(0)
  await page.getByTestId('build-policy-input-classifier').selectOption('strict')
  await page.getByTestId('build-edit-save').click()
  await check(page.getByTestId('build-edit-saved')).toBeVisible()

  // 2. Publish over the data source made by hand.
  await page.getByTestId('build-tab-publish').click()
  await page.getByTestId('build-publish-proxy').selectOption({ index: 1 })
  await page.getByTestId('build-publish-connection').fill(policyBuild.connection)
  await page.getByTestId('build-publish-data-source').fill('erp')
  await page.getByTestId('build-publish-button').click()
  await check(page.getByTestId('build-publish-replace')).toBeVisible()
  await page.getByTestId('build-publish-replace-button').click()
  await check(page.getByTestId('build-publish-done-adopted')).toBeVisible()
  await expect(page.getByTestId('build-publish-change-extensions.gateway.allow_writes')).toContainText('dropped')
  await expect(page.getByTestId('build-publish-change-extensions.gateway.allowed_directories')).toContainText('dropped')
  await expect(page.getByTestId('build-publish-change-extensions.gateway.batch_size')).toContainText('64')

  // 3. The Gateway shows the policy, and changes nothing.
  await page.getByTestId('rail-home-link').click()
  await page.getByTestId('instance-row-Policy gateway').getByTestId('instance-open-link').click()
  await page.getByTestId('rail-data-sources').click()
  await check(page.getByTestId('data-source-erp')).toBeVisible()
  await expect(page.getByTestId('data-source-erp').getByTestId('data-source-owner')).not.toHaveText('by hand')
  await page.getByTestId('data-source-erp').getByTestId('data-source-link').click()
  await check(page.getByTestId('data-source-policy')).toBeVisible()
  await expect(page.getByTestId('data-source-policy-batch_size')).toContainText('500')
  await expect(page.getByTestId('data-source-policy-origin')).toHaveAttribute('data-origin', 'definition')
  await expect(page.getByTestId('data-source-policy-blocked-none')).toBeVisible()
  await expect(page.getByTestId('data-source-policy').locator('input, button, select, textarea')).toHaveCount(0)
})
