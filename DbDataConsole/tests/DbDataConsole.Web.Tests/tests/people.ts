import { expect, type BrowserContext, type Page } from '@playwright/test'
import { execFileSync } from 'node:child_process'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { scratchDirectory } from '../playwright.config'

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../../..')
const consoleDll = path.join(repoRoot, 'DbDataConsole/src/DbDataConsole.Server/bin/Debug/net10.0/dbdatakit.dll')

/**
 * Makes a new administrator the way an installer would (an invitation from the command line) and signs them in on `page` with a passkey held by a virtual authenticator of that page's browser context. Each person needs a
 * context of their own: a passkey lives in one authenticator, and a session in one context. The first-run invitation is not used (the first-run story spends it).
 */
export async function signInNewAdmin(page: Page, context: BrowserContext, baseURL: string, name: string, email: string) {
  const cdp = await context.newCDPSession(page)
  await cdp.send('WebAuthn.enable')
  await cdp.send('WebAuthn.addVirtualAuthenticator', {
    options: { protocol: 'ctap2', transport: 'internal', hasResidentKey: true, hasUserVerification: true, isUserVerified: true, automaticPresenceSimulation: true },
  })
  const made = execFileSync('dotnet', ['exec', consoleDll, 'user', 'invite', '--email', email, '--role', 'admin', '--url', baseURL, '--repo', scratchDirectory], { encoding: 'utf8' })
  await page.goto(`/invite#${/\/invite#(\S+)/.exec(made)![1]}`)
  await page.getByTestId('invite-name-input').fill(name)
  await page.getByTestId('invite-enrol-button').click()
  await expect(page.getByTestId('home-page')).toBeVisible()
}
