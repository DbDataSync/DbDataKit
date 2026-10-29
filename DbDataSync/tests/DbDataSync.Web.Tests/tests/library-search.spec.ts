import path from 'node:path'
import { test, expect } from '@playwright/test'
import { screenshotDir } from '../screenshots'

const screenshotsDir = screenshotDir('library-search')

/**
 * Phase 119's NuGet search box on the Libraries screen. The server's call to the public index is
 * answered in the browser with a fixed result: the test used to reach nuget.org through the API and
 * failed whenever that round trip was slow or refused, which says nothing about this screen (the real
 * call is covered at the API layer by `LibrarySearchTests`, as are the disabled and unavailable
 * answers).
 */
test.describe('admin: Libraries — NuGet search', () => {
  test('searching finds a package, and a version is required before the install command appears', async ({ page }) => {
    await page.route('**/api/libraries/search**', (route) => route.fulfill({
      contentType: 'application/json',
      body: JSON.stringify({
        status: 'ok',
        results: new URL(route.request().url()).searchParams.get('q')
          ? [{ id: 'MySqlConnector', description: 'A MySQL driver', latestVersion: '2.6.2', versions: ['2.6.2', '2.4.0'], totalDownloads: 1234567, verified: true }]
          : [],
      }),
    }))
    await page.goto('/drivers/libraries')

    const searchInput = page.getByTestId('admin-libraries-search-input')
    // The probe-on-mount call has to settle (proving search is reachable) before the box replaces the
    // "checking..." placeholder.
    await expect(searchInput).toBeVisible({ timeout: 15_000 })

    await searchInput.fill('MySqlConnector')
    await page.getByTestId('admin-libraries-search-submit').click()

    const result = page.getByTestId('admin-libraries-result-MySqlConnector')
    await expect(result).toBeVisible({ timeout: 15_000 })
    await expect(result).toContainText('downloads')

    // No command yet — a version hasn't been picked.
    await expect(page.getByTestId('admin-libraries-install-command')).not.toBeVisible()

    const versionSelect = page.getByTestId('admin-libraries-result-version-MySqlConnector')
    const firstRealVersion = await versionSelect.locator('option').nth(1).getAttribute('value')
    await versionSelect.selectOption(firstRealVersion!)

    const command = page.getByTestId('admin-libraries-install-command')
    await expect(command).toBeVisible()
    await expect(command).toContainText(`dbdatasync library install MySqlConnector --version ${firstRealVersion}`)
    // MySqlConnector is a bundled, curated entry (phase 117) — no trust warning for it.
    await expect(command).not.toContainText("isn't one of the bundled")

    await page.screenshot({ path: path.join(screenshotsDir, '119-nuget-search-results.png'), fullPage: true })
  })

  test('a quick-add chip for a curated library not yet installed pre-fills its id', async ({ page }) => {
    await page.goto('/drivers/libraries')

    // Npgsql is bundled (phase 117) and not seeded by global setup, so its chip should be offered.
    const chip = page.getByTestId('admin-libraries-chip-npgsql')
    await expect(chip).toBeVisible()
    await chip.click()

    await expect(page.getByTestId('admin-libraries-command-version')).toBeVisible()
    await page.getByTestId('admin-libraries-command-version').fill('9.0.0')

    const command = page.getByTestId('admin-libraries-install-command')
    await expect(command).toContainText('dbdatasync library install Npgsql --version 9.0.0')

    await page.screenshot({ path: path.join(screenshotsDir, '119-curated-quick-add.png'), fullPage: true })
  })
})
