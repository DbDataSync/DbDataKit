import { expect, test, type BrowserContext, type Page } from '@playwright/test'
import { execFileSync } from 'node:child_process'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { announcers, scratchDirectory } from '../playwright.config'

// Plan 13a, L03: servers that announce themselves wait on the Servers page for an administrator, who confirms or rejects each. Two real Build servers are started with the console's announce address and
// the address to give (see playwright.config.ts); nobody pairs them with a code. The administrator here is made with the console's own command line (an invitation, redeemed with a passkey), so this
// file does not depend on the first-run story having run before it.
const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../../..')
const consoleDll = path.join(repoRoot, 'DbDataConsole/src/DbDataConsole.Server/bin/Debug/net10.0/dbdatakit.dll')
const [toConfirm, toReject] = announcers

async function addAuthenticator(context: BrowserContext, page: Page) {
  const cdp = await context.newCDPSession(page)
  await cdp.send('WebAuthn.enable')
  await cdp.send('WebAuthn.addVirtualAuthenticator', {
    options: { protocol: 'ctap2', transport: 'internal', hasResidentKey: true, hasUserVerification: true, isUserVerified: true, automaticPresenceSimulation: true },
  })
}

test.describe.serial('servers waiting for confirmation', () => {
  test.beforeEach(async ({ page, context, baseURL }) => {
    await addAuthenticator(context, page)
    const made = execFileSync('dotnet', ['exec', consoleDll, 'user', 'invite', '--email', `pending-${Date.now()}-${Math.random().toString(36).slice(2, 8)}@example.com`, '--role', 'admin', '--url', baseURL!, '--repo', scratchDirectory], { encoding: 'utf8' })
    await page.goto(`/invite#${/\/invite#(\S+)/.exec(made)![1]}`)
    await page.getByTestId('invite-name-input').fill('Pending Tester')
    await page.getByTestId('invite-enrol-button').click()
    await expect(page.getByTestId('home-page')).toBeVisible()
    await page.goto('/instances')
    await expect(page.getByTestId('instances-page')).toBeVisible()
  })

  test('a server that announced itself is listed with its name and address, and rejecting it removes it', async ({ page }) => {
    // Announcements arrive within seconds of the server starting and the list asks again every few: no reload is needed.
    const row = page.getByTestId(`pending-row-${toReject.name}`)
    await expect(row).toBeVisible({ timeout: 30_000 })
    await expect(row.getByTestId('pending-url')).toHaveText(`https://localhost:${toReject.port}/`)
    await expect(row).toContainText('dbdatabuild')
    await expect(row.getByTestId('pending-last-seen')).toContainText(/ago$/)
    // The code the server made for the console is never on the page.
    await expect(page.getByTestId('pending-card')).not.toContainText(/pairing code/i)

    // Nothing is paired by announcing.
    await expect(page.getByTestId(`instance-row-${toReject.name}`)).toHaveCount(0)

    await row.getByTestId('pending-reject-button').click()
    await expect(row).toHaveCount(0)
    await expect(page.getByTestId(`instance-row-${toReject.name}`)).toHaveCount(0)
  })

  test('confirming a waiting server pairs it: it leaves the waiting list and is listed as ready', async ({ page }) => {
    const row = page.getByTestId(`pending-row-${toConfirm.name}`)
    await expect(row).toBeVisible({ timeout: 30_000 })

    await row.getByTestId('pending-confirm-button').click()

    await expect(page.getByTestId(`pending-row-${toConfirm.name}`)).toHaveCount(0)
    const paired = page.getByTestId(`instance-row-${toConfirm.name}`)
    await expect(paired.getByTestId('instance-status')).toHaveText('Ready', { timeout: 30_000 })
    await expect(paired).toContainText('dbdatabuild')
    await expect(paired).toContainText(`https://localhost:${toConfirm.port}`)
    // It stays paired (and does not come back as waiting) when the page is read again.
    await page.reload()
    await expect(page.getByTestId(`instance-row-${toConfirm.name}`)).toBeVisible()
    await expect(page.getByTestId(`pending-row-${toConfirm.name}`)).toHaveCount(0)

    // Tidy: forget it, so the console's list is as the other stories expect it.
    await page.getByTestId(`instance-row-${toConfirm.name}`).getByTestId('instance-forget-button').click()
    await expect(page.getByTestId(`instance-row-${toConfirm.name}`)).toHaveCount(0)
  })
})
