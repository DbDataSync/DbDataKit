import { expect, test, type BrowserContext, type Page } from '@playwright/test'
import { execFileSync } from 'node:child_process'
import crypto from 'node:crypto'
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { scratchDirectory, upgradeBuild } from '../playwright.config'

// Plan 24a UC08 and UC09: the Upgrades screen. A mirror folder (written here, set in the console's settings.yaml: no internet) holds a newer release of the Build server; the Servers page says "Newer available" with the target version,
// the Upgrades screen lists the versions per product and the server's current and newest, and Check says why this server (started by hand, not as a service) is not upgraded from the console and shows the command. The apply (drain,
// swap, health, rollback) needs a service, so it is the library's tests (`DbDataKit.Upgrade.Tests`), not this story.
// Sorts after `identity-home` (it invites an administrator, which uses the first-run invitation up): see docs/ci-lessons.md.
const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../../..')
const consoleDll = path.join(repoRoot, 'DbDataKit/src/DbDataKit.Server/bin/Debug/net10.0/dbdatakit.dll')
const serverName = 'Upgrade build'
const mirror = upgradeBuild.mirror
const newest = '2099.1.1.1200'

function release(version: string) {
  const folder = path.join(mirror, 'dbdatabuild', `v${version}`)
  fs.mkdirSync(folder, { recursive: true })
  const zip = path.join(folder, `dbdatabuild-v${version}-linux-x64.zip`)
  fs.writeFileSync(zip, `zip ${version}`)
  fs.writeFileSync(path.join(folder, 'SHA256SUMS'), `${crypto.createHash('sha256').update(fs.readFileSync(zip)).digest('hex')} *${path.basename(zip)}\n`)
}

async function signInAsAdmin(page: Page, context: BrowserContext, baseURL: string) {
  const cdp = await context.newCDPSession(page)
  await cdp.send('WebAuthn.enable')
  await cdp.send('WebAuthn.addVirtualAuthenticator', {
    options: { protocol: 'ctap2', transport: 'internal', hasResidentKey: true, hasUserVerification: true, isUserVerified: true, automaticPresenceSimulation: true },
  })
  const made = execFileSync('dotnet', ['exec', consoleDll, 'user', 'invite', '--email', `upgrades-${Date.now()}-${Math.random().toString(36).slice(2, 8)}@example.com`, '--role', 'admin', '--url', baseURL, '--repo', scratchDirectory], { encoding: 'utf8' })
  await page.goto(`/invite#${/\/invite#(\S+)/.exec(made)![1]}`)
  await page.getByTestId('invite-name-input').fill('Upgrades Tester')
  await page.getByTestId('invite-enrol-button').click()
  await expect(page.getByTestId('home-page')).toBeVisible()
}

test('the Servers page and the Upgrades screen show what is available, and a server started by hand is told how to upgrade', async ({ page, context, baseURL }) => {
  release('2098.1.1.1000')
  release(newest)
  fs.writeFileSync(path.join(scratchDirectory, 'settings.yaml'), `upgrade:\n  feed:\n    mirror: ${mirror.replace(/\\/g, '/')}\n`)
  await signInAsAdmin(page, context, baseURL!)

  await page.goto('/instances')
  await expect(page.getByTestId('instances-page')).toBeVisible()
  await page.getByTestId('pair-url-input').fill(`https://localhost:${upgradeBuild.port}`)
  await page.getByTestId('pair-code-input').fill(fs.readFileSync(path.join(scratchDirectory, 'upgrade-build-pairing-code.txt'), 'utf8').trim())
  await page.getByTestId('pair-name-input').fill(serverName)
  await page.getByTestId('pair-button').click()
  const row = page.getByTestId(`instance-row-${serverName}`)
  await expect(row.getByTestId('instance-status')).toHaveText('Ready', { timeout: 30_000 })
  await expect(row.getByTestId('instance-newer')).toContainText(`Newer available: ${newest}`, { timeout: 30_000 })

  await page.getByTestId('upgrades-link').click()
  await expect(page.getByTestId('upgrades-page')).toBeVisible()
  await expect(page.getByTestId('upgrades-feed')).toContainText('a mirror')
  await expect(page.getByTestId('upgrades-versions-dbdatabuild')).toContainText(newest)
  await expect(page.getByTestId('upgrades-versions-dbdatabuild')).toContainText('2098.1.1.1000')
  await expect(page.getByTestId(`upgrade-newest-${serverName}`)).toHaveAttribute('data-newer', 'true')
  await expect(page.getByTestId(`upgrade-newest-${serverName}`)).toHaveText(`Newer available: ${newest}`)

  // started by hand: not a service, so the console does not upgrade it, and Check shows the instruction instead of downloading anything
  await expect(page.getByTestId(`upgrade-cannot-${serverName}`)).toContainText('not started as a Windows service or a systemd unit')
  await page.getByTestId(`upgrade-check-${serverName}`).click()
  await expect(page.getByTestId(`upgrade-report-${serverName}`)).toHaveAttribute('data-state', 'instructions')
  await expect(page.getByTestId(`upgrade-command-${serverName}`)).toContainText(newest)
  await expect(page.getByTestId(`upgrade-apply-${serverName}`)).toHaveCount(0)
})
