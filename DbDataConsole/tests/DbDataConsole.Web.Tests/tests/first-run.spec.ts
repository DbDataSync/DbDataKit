import { expect, test, type BrowserContext, type Page } from '@playwright/test'
import fs from 'node:fs'
import path from 'node:path'
import { buildUrl, scratchDirectory } from '../playwright.config'

/** A browser-side authenticator that holds the passkeys, so the test can register one and sign in with it the way a person's device would. */
async function addAuthenticator(context: BrowserContext, page: Page) {
  const cdp = await context.newCDPSession(page)
  await cdp.send('WebAuthn.enable')
  await cdp.send('WebAuthn.addVirtualAuthenticator', {
    options: { protocol: 'ctap2', transport: 'internal', hasResidentKey: true, hasUserVerification: true, isUserVerified: true, automaticPresenceSimulation: true },
  })
}

function firstRunCode() {
  const text = fs.readFileSync(path.join(scratchDirectory, 'FIRST-RUN.txt'), 'utf8')
  return text.split('/invite#')[1].split(/\s/)[0]
}

// One story, in order: the person who installed the console redeems the first-run invitation, is signed in as the administrator, signs out and back in with the same
// passkey, sees themselves in the users list, makes an invitation for someone else, and pairs a Build server with the code made on it. A test per step would each need the one before it.
test('first run: invitation, passkey, sign-in, users, servers', async ({ page, context }) => {
  await addAuthenticator(context, page)

  // Nobody is signed in: the app asks them to.
  await page.goto('/')
  await expect(page.getByTestId('sign-in-screen')).toBeVisible()

  // A code that is not an invitation says so; the real one lets the person name themselves and make a passkey.
  await page.goto('/invite#not-a-code')
  await expect(page.getByTestId('invite-invalid')).toBeVisible()

  // A new load, as opening the link is: a change of the fragment alone would keep the page (and the code it read) that was already open.
  await page.goto('about:blank')
  await page.goto(`/invite#${firstRunCode()}`)
  await page.getByTestId('invite-name-input').fill('Ada Lovelace')
  await page.getByTestId('invite-email-input').fill('ada@example.com')
  await page.getByTestId('invite-enrol-button').click()

  // Signed in as the first administrator.
  await expect(page.getByTestId('home-page')).toBeVisible()
  await expect(page.getByTestId('signed-in-as')).toContainText('Ada Lovelace')

  // The invitation is spent, and so is the file that carried it.
  expect(fs.existsSync(path.join(scratchDirectory, 'FIRST-RUN.txt'))).toBe(false)

  // Sign out, then back in with the passkey the browser kept.
  await page.getByTestId('sign-out-button').click()
  await expect(page.getByTestId('sign-in-screen')).toBeVisible()
  await page.getByTestId('sign-in-passkey').click()
  await expect(page.getByTestId('home-page')).toBeVisible()

  // The users list has the administrator, with one way to sign in.
  await page.getByTestId('rail-users').click()
  await expect(page.getByTestId('users-page')).toBeVisible()
  const row = page.getByTestId('user-row-Ada Lovelace')
  await expect(row).toBeVisible()
  await expect(row.getByTestId('user-role-select')).toHaveValue('Admin')
  await expect(row.getByTestId('user-credential')).toHaveCount(1)

  // The only administrator cannot be disabled (it would lock everybody out), and the page says why.
  await row.getByTestId('user-enabled-checkbox').click()
  await expect(page.getByText('only enabled administrator')).toBeVisible()
  await expect(row.getByTestId('user-enabled-checkbox')).toBeChecked()

  // An invitation for someone else: a link, shown once.
  await page.getByTestId('invite-create-button').click()
  await expect(page.getByTestId('invite-link')).toHaveValue(/\/invite#.+/)

  // Servers: none yet; pair a real Build server with the code made on it, see it answer, forget it.
  await page.getByTestId('rail-instances').click()
  await expect(page.getByTestId('instances-empty')).toBeVisible()

  // A wrong code is refused in words and nothing is paired.
  await page.getByTestId('pair-url-input').fill(buildUrl)
  await page.getByTestId('pair-code-input').fill('AAAAA-BBBBB-CCCCC-DDDDD')
  await page.getByTestId('pair-button').click()
  await expect(page.getByText('did not accept the code')).toBeVisible()
  await expect(page.getByTestId('instances-empty')).toBeVisible()

  await page.getByTestId('pair-code-input').fill(fs.readFileSync(path.join(scratchDirectory, 'build-pairing-code.txt'), 'utf8').trim())
  await page.getByTestId('pair-name-input').fill('Build one')
  await page.getByTestId('pair-button').click()
  const instance = page.getByTestId('instance-row-Build one')
  await expect(instance).toBeVisible()
  await expect(instance.getByTestId('instance-status')).toHaveText('Ready')
  await expect(instance).toContainText('dbdatabuild')

  await instance.getByTestId('instance-forget-button').click()
  await expect(page.getByTestId('instances-empty')).toBeVisible()
})
