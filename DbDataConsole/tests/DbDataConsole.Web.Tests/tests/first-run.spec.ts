import { expect, test, type BrowserContext, type Page } from '@playwright/test'
import fs from 'node:fs'
import path from 'node:path'
import { buildUrl, proxyUrl, scratchDirectory, syncUrl } from '../playwright.config'

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

  // Its screens open inside the console: the project it hosts, the models, the graph, a check.
  await instance.getByTestId('instance-open-link').click()
  await expect(page).toHaveURL(/\/servers\/build-[0-9a-f]+$/)
  await expect(page.getByTestId('build-project-starter')).toBeVisible()
  await page.getByTestId('build-project-link').click()
  await expect(page).toHaveURL(/\/servers\/build-[0-9a-f]+\/projects\/starter$/)
  await expect(page.getByTestId('build-model-marts.orders')).toBeVisible()
  await expect(page.getByTestId('build-model-raw.customers')).toBeVisible()
  await page.getByTestId('build-tab-graph').click()
  await expect(page.getByTestId('build-graph-node-marts.customers')).toBeVisible()
  expect(await page.getByTestId('build-graph-edge').count()).toBeGreaterThan(5)
  await page.getByTestId('build-tab-check').click()
  await page.getByTestId('build-check-button').click()
  await expect(page.getByTestId('build-check-models')).toHaveText('8 models')
  await expect(page.getByTestId('build-check-errors')).toHaveText('0 errors')
  await expect(page.getByTestId('build-check-out-of-date')).toBeVisible()   // the starter has never been compiled, so its rendered files are not written yet
  await page.getByTestId('rail-home-link').click()
  await expect(page.getByTestId('instances-page')).toBeVisible()

  await instance.getByTestId('instance-forget-button').click()
  await expect(page.getByTestId('instances-empty')).toBeVisible()

  // A Sync server: paired, then its own screens open inside the console, under the server's address, through the relay.
  await page.getByTestId('pair-url-input').fill(syncUrl)
  await page.getByTestId('pair-code-input').fill(fs.readFileSync(path.join(scratchDirectory, 'sync-pairing-code.txt'), 'utf8').trim())
  await page.getByTestId('pair-name-input').fill('Sync one')
  await page.getByTestId('pair-button').click()
  const sync = page.getByTestId('instance-row-Sync one')
  await expect(sync.getByTestId('instance-status')).toHaveText('Ready')
  await expect(sync).toContainText('dbdatasync')
  await sync.getByTestId('instance-open-link').click()

  // Its replications screen (an empty list, from the Sync server, as the person who is signed in here) under /servers/<id>/replications.
  await expect(page).toHaveURL(/\/servers\/sync-[0-9a-f]+\/replications$/)
  await expect(page.getByTestId('rail-replications')).toBeVisible()
  await expect(page.getByText('No replications yet.').first()).toBeVisible()
  // Its links stay inside the console's address for this server, and a reload of a deep link returns to the same screen.
  await page.getByTestId('rail-connections').click()
  await expect(page).toHaveURL(/\/servers\/sync-[0-9a-f]+\/connections$/)
  await page.reload()
  await expect(page).toHaveURL(/\/servers\/sync-[0-9a-f]+\/connections$/)
  await expect(page.getByTestId('rail-connections')).toBeVisible()
  // An administrator opens the server's own configuration through the relay.
  await page.getByTestId('rail-admin').click()
  await expect(page).toHaveURL(/\/servers\/sync-[0-9a-f]+\/admin\/config$/)
  await expect(page.getByTestId('admin-config-table')).toBeVisible()
  // The way back to the console's list of servers.
  await page.getByTestId('rail-home-link').click()
  await expect(page.getByTestId('instances-page')).toBeVisible()

  // A Proxy: its screens open inside the console too. The overview, the data source it was started with, a user made here, the certificate and the history of its configuration.
  await page.getByTestId('pair-url-input').fill(proxyUrl)
  await page.getByTestId('pair-code-input').fill(fs.readFileSync(path.join(scratchDirectory, 'proxy-pairing-code.txt'), 'utf8').trim())
  await page.getByTestId('pair-name-input').fill('Proxy one')
  await page.getByTestId('pair-button').click()
  const proxy = page.getByTestId('instance-row-Proxy one')
  await expect(proxy.getByTestId('instance-status')).toHaveText('Ready')
  await expect(proxy).toContainText('dbdataproxy')
  await proxy.getByTestId('instance-open-link').click()
  await expect(page.getByTestId('proxy-overview')).toBeVisible()

  await page.getByTestId('rail-data-sources').click()
  await expect(page).toHaveURL(/\/servers\/proxy-[0-9a-f]+\/data-sources$/)
  await expect(page.getByTestId('data-source-erp')).toBeVisible()

  await page.getByTestId('rail-identities').click()
  await page.getByTestId('user-name-input').fill('analyst')
  await page.getByTestId('user-password-input').fill('correct horse battery')
  await page.getByTestId('user-sources-input').fill('erp')
  await page.getByTestId('user-save').click()
  const analyst = page.getByTestId('user-analyst')
  await expect(analyst).toBeVisible()
  await expect(analyst.getByTestId('user-has-password')).toHaveText('set')
  await expect(page.getByText('correct horse battery')).toHaveCount(0)   // a password is never shown back

  await page.getByTestId('rail-tls').click()
  await expect(page.getByTestId('tls-details')).toBeVisible()

  await page.getByTestId('rail-activity').click()
  await expect(page.getByTestId('proxy-activity')).toBeVisible()
  await expect(page.getByTestId('lockouts-table')).toContainText('Nobody is locked out.')

  // The user made above is a commit by the person who made it.
  await page.getByTestId('rail-config').click()
  await expect(page.getByTestId('commits-table').locator('[data-testid^="commit-"]').first()).toBeVisible()
})
