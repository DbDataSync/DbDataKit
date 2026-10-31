import { expect, test } from '@playwright/test'
import { execFileSync } from 'node:child_process'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { scratchDirectory } from '../playwright.config'

// The console's own docs entry (plan 13a, U11): every module that registers docs in its package.json is listed, and a module's docs open in the console's frame under /docs/<module>, through the same viewer
// the module shows inside its server's screens (Sync's own Playwright suite covers that one). The person is a viewer made with the console's command line: the docs are readable by every role. This file is named to run after
// first-run.spec.ts (files run in name order): redeeming any invitation spends the first-run one, which that story needs, and a second administrator would change what it checks.
const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../../..')
const consoleDll = path.join(repoRoot, 'DbDataConsole/src/DbDataConsole.Server/bin/Debug/net10.0/dbdatakit.dll')

test('the docs of every module are listed, and a module\'s page opens in the console with its pictures and its links', async ({ page, context, baseURL }) => {
  const cdp = await context.newCDPSession(page)
  await cdp.send('WebAuthn.enable')
  await cdp.send('WebAuthn.addVirtualAuthenticator', {
    options: { protocol: 'ctap2', transport: 'internal', hasResidentKey: true, hasUserVerification: true, isUserVerified: true, automaticPresenceSimulation: true },
  })
  const made = execFileSync('dotnet', ['exec', consoleDll, 'user', 'invite', '--email', 'reader@example.com', '--role', 'viewer', '--url', baseURL!, '--repo', scratchDirectory], { encoding: 'utf8' })
  await page.goto(`/invite#${/\/invite#(\S+)/.exec(made)![1]}`)
  await page.getByTestId('invite-name-input').fill('Docs Reader')
  await page.getByTestId('invite-enrol-button').click()
  await expect(page.getByTestId('home-page')).toBeVisible()

  // The console-level entry lists the module.
  await page.getByTestId('rail-docs').click()
  await expect(page).toHaveURL(/\/docs$/)
  await expect(page.getByTestId('docs-directory')).toBeVisible()
  await page.getByTestId('docs-module-sync').click()

  // Its index, in the order of the module's ordering file, under the module's own route.
  await expect(page).toHaveURL(/\/docs\/sync$/)
  await expect(page.getByTestId('docs-index')).toContainText('DbDataSync')
  const nav = page.getByTestId('docs-nav').getByRole('link')
  await expect(nav.first()).toHaveText('Install')
  await expect(nav.nth(1)).toHaveText('Configuration')

  // A page with pictures: served from the module's assets under /docs/sync, every one decoded.
  await page.getByTestId('docs-nav').getByRole('link', { name: 'Getting started' }).click()
  await expect(page).toHaveURL(/\/docs\/sync\/getting-started$/)
  const pictures = page.getByTestId('docs-content').locator('img')
  await expect(pictures.first()).toBeVisible()
  for (const src of await pictures.evaluateAll((imgs) => imgs.map((img) => (img as HTMLImageElement).getAttribute('src'))))
    expect(src).toMatch(/^\/docs\/sync\/assets\/screenshots\/golden-path\/[A-Za-z0-9._-]+\.png$/)
  await expect.poll(() => pictures.evaluateAll((imgs) => imgs.every((img) => (img as HTMLImageElement).naturalWidth > 0))).toBe(true)

  // A link between pages stays under the module's route and lands on the heading.
  await page.goto('/docs/sync/replication-concepts')
  await page.getByTestId('docs-content').getByRole('link', { name: 'descriptor driver' }).first().click()
  await expect(page).toHaveURL(/\/docs\/sync\/drivers-and-libraries#descriptor-drivers$/)
  await expect(page.getByTestId('docs-content').locator('#descriptor-drivers')).toBeInViewport()

  // A module with no docs, and a page that is not there, say so.
  await page.goto('/docs/nothing-here')
  await expect(page.getByTestId('docs-module-unknown')).toBeVisible()
  await page.goto('/docs/sync/no-such-page')
  await expect(page.getByTestId('docs-not-found')).toBeVisible()
})
