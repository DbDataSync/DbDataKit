import { test } from '@playwright/test'
import fs from 'node:fs'
import path from 'node:path'
import { scratchDirectory, viewsBuild } from '../playwright.config'
import { signInNewAdmin } from './people'
import { buildViewsStory } from './views-build'
import { expect } from '@playwright/test'

// Plan 32 DBR29: dashboards and reports as files of a Build project, edited in a workspace with the shared editor, checked with the project's own models and listed in the Edit tab. The project is the retail template (made by
// playwright.config.ts, Build server on port 8551, no Gateway paired). It signs in a new administrator, so it sorts after `identity-home` and `first-run` (the console must have its users).
test('dashboards and reports of a Build project: new, saved as a file, checked with the project\'s model, conflict, Edit tab, merge, and what can be published', async ({ page, context, baseURL }) => {
  test.setTimeout(300_000)
  await signInNewAdmin(page, context, baseURL!, 'Views Build Tester', `views-build-${Date.now()}@example.com`)
  await page.goto('/instances')
  await page.getByTestId('pair-url-input').fill(`https://localhost:${viewsBuild.port}`)
  await page.getByTestId('pair-code-input').fill(fs.readFileSync(path.join(scratchDirectory, 'views-build-pairing-code.txt'), 'utf8').trim())
  await page.getByTestId('pair-name-input').fill('Views build')
  await page.getByTestId('pair-button').click()
  await expect(page.getByTestId('instance-row-Views build').getByTestId('instance-status')).toHaveText('Ready', { timeout: 30_000 })
  await page.getByTestId('instance-row-Views build').getByTestId('instance-open-link').click()
  await page.getByTestId(`build-project-${viewsBuild.project}`).getByTestId('build-project-link').click()
  await buildViewsStory(page, context, viewsBuild.project)
})
