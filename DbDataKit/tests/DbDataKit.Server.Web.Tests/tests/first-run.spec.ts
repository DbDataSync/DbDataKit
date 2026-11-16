import { expect, test, type BrowserContext, type Page } from '@playwright/test'
import fs from 'node:fs'
import path from 'node:path'
import { firstRunRunner, gatewayUrl, scratchDirectory } from '../playwright.config'
import { viewsStory } from './views'

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
// passkey, sees themselves in the users list, makes an invitation for someone else, and pairs a runner with the code made on it (a Build server until plan 33 SC23 deleted it, with its screens and the publishing it did). A test per step would each need the one before it.
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

  // Views: fixture views (a matrix, a chart) are opened and drawn by the shared visuals, in both themes, and a chart that names a URL is refused.
  await viewsStory(page)

  // An invitation for someone else: a link, shown once.
  await page.getByTestId('rail-users').click()
  await expect(page.getByTestId('users-page')).toBeVisible()
  await page.getByTestId('invite-email-field').fill('grace@example.com')
  await page.getByTestId('invite-create-button').click()
  await expect(page.getByTestId('invite-link')).toHaveValue(/\/invite#.+/)

  // Servers: none yet; pair a real runner with the code made on it, see it answer, forget it.
  await page.getByTestId('rail-instances').click()
  await expect(page.getByTestId('instances-empty')).toBeVisible()

  // A wrong code is refused in words and nothing is paired.
  await page.getByTestId('pair-url-input').fill(firstRunRunner.url)
  await page.getByTestId('pair-code-input').fill('AAAAA-BBBBB-CCCCC-DDDDD')
  await page.getByTestId('pair-button').click()
  await expect(page.getByText('did not accept the code')).toBeVisible()
  await expect(page.getByTestId('instances-empty')).toBeVisible()

  await page.getByTestId('pair-code-input').fill(fs.readFileSync(path.join(scratchDirectory, 'first-run-runner-pairing-code.txt'), 'utf8').trim())
  await page.getByTestId('pair-name-input').fill('Runner one')
  await page.getByTestId('pair-button').click()
  const instance = page.getByTestId('instance-row-Runner one')
  await expect(instance).toBeVisible()
  await expect(instance.getByTestId('instance-status')).toHaveText('Ready')
  await expect(instance).toContainText('dbdatarunner')

  // Its screens open inside the console, under the server's address, through the relay: the tasks it runs (none yet).
  await instance.getByTestId('instance-open-link').click()
  await expect(page).toHaveURL(/\/servers\/[^/]+$/)
  await expect(page.getByTestId('runner-tasks-page')).toBeVisible()

  await page.getByTestId('rail-home-link').click()
  await expect(page.getByTestId('instances-page')).toBeVisible()

  await instance.getByTestId('instance-forget-button').click()
  await expect(page.getByTestId('instances-empty')).toBeVisible()

  // A Gateway: its screens open inside the console too. The overview, the data source it was started with, a JWT claim mapping made here, the certificate and the history of its configuration.
  await page.getByTestId('pair-url-input').fill(gatewayUrl)
  await page.getByTestId('pair-code-input').fill(fs.readFileSync(path.join(scratchDirectory, 'gateway-pairing-code.txt'), 'utf8').trim())
  await page.getByTestId('pair-name-input').fill('Gateway one')
  await page.getByTestId('pair-button').click()
  const gateway = page.getByTestId('instance-row-Gateway one')
  await expect(gateway.getByTestId('instance-status')).toHaveText('Ready')
  await expect(gateway).toContainText('dbdatagateway')
  await gateway.getByTestId('instance-open-link').click()
  await expect(page.getByTestId('gateway-overview')).toBeVisible()

  await page.getByTestId('rail-data-sources').click()
  await expect(page).toHaveURL(/\/servers\/gateway-[0-9a-f]+\/data-sources$/)
  await expect(page.getByTestId('data-source-erp')).toBeVisible()

  await page.getByTestId('rail-identities').click()
  // Gateway users are retired (plan 13a, P13): people reach a data source as home users, in the groups its file names, so there is no user to make on this screen.

  // a JWT claim mapping, saved as a list into gateway/auth.yaml and shown again from the server
  await page.getByTestId('jwt-mapping-add').click()
  await page.getByTestId('jwt-claim-input').fill('group')
  await page.getByTestId('jwt-value-input').fill('analysts')
  await page.getByTestId('jwt-sources-input').fill('erp')
  const saved = page.waitForResponse((r) => r.url().includes('/api/v1/auth') && r.request().method() === 'PUT')
  await page.getByTestId('jwt-mappings-save').click()
  const answer = await saved
  expect(answer.status(), await answer.text()).toBe(200)
  await expect(page.getByTestId('jwt-mapping-0').getByTestId('jwt-value-input')).toHaveValue('analysts')
  await page.reload()
  await expect(page.getByTestId('jwt-mapping-0').getByTestId('jwt-claim-input')).toHaveValue('group')

  await page.getByTestId('rail-tls').click()
  await expect(page.getByTestId('tls-details')).toBeVisible()

  await page.getByTestId('rail-activity').click()
  await expect(page.getByTestId('gateway-activity')).toBeVisible()
  await expect(page.getByTestId('lockouts-table')).toContainText('Nobody is locked out.')

  // The mapping made above is a commit by the person who made it.
  await page.getByTestId('rail-config').click()
  await expect(page.getByTestId('commits-table').locator('[data-testid^="commit-"]').first()).toBeVisible()

  await page.getByTestId('rail-home-link').click()

  // E05, the Connections view: the Gateway's data sources are listed, and one is copied on the same Gateway under another name (a copy to or from a Sync server went with the Sync server, plan 33 SC23). The target reads
  // the file with its own strict loader.
  await page.goto('/connections')
  await expect(page.getByTestId('connections-page')).toBeVisible()
  const connection = (server: string, name: string) => page.locator(`[data-testid=connection-row][data-server="${server}"][data-name="${name}"]`)
  await expect(connection('Gateway one', 'ledger')).toBeVisible()
  await connection('Gateway one', 'ledger').getByTestId('connection-copy-button').click()
  await expect(page.getByTestId('copy-submit')).toBeDisabled()   // a server first
  await page.getByTestId('copy-target').selectOption({ label: 'Gateway one (Gateway)' })
  await page.getByTestId('copy-name').fill('ledger-copy')
  await page.getByTestId('copy-submit').click()
  await expect(page.getByTestId('copy-done')).toContainText('Copied to Gateway one as ledger-copy')
  await expect(connection('Gateway one', 'ledger-copy')).toBeVisible()
  // a name the target already has is a conflict, and the file there is left alone
  await page.getByTestId('copy-submit').click()
  await expect(page.getByTestId('copy-failed')).toContainText('did not accept the copy')

  // One view across the servers: the Gateway answers and nothing is failing (the rows themselves are tested on what the servers return)
  await page.goto('/monitoring')   // the rail inside a server's screens is that product's own
  await expect(page.getByTestId('monitoring-none')).toBeVisible()
  await expect(page.getByTestId('monitoring-problem')).toHaveCount(0)
})
