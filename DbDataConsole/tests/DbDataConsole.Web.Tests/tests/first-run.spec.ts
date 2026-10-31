import { expect, test, type BrowserContext, type Page } from '@playwright/test'
import fs from 'node:fs'
import path from 'node:path'
import { buildUrl, proxyUrl, scratchDirectory, syncUrl, warehouseConnection } from '../playwright.config'
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

  // Views: fixture views (a matrix, a chart) are opened and drawn by the shared visuals, in both themes, and a chart that names a URL is refused.
  await viewsStory(page)

  // An invitation for someone else: a link, shown once.
  await page.getByTestId('rail-users').click()
  await expect(page.getByTestId('users-page')).toBeVisible()
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
  await page.getByTestId('build-project-starter').getByTestId('build-project-link').click()
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

  // Editing, in a workspace of one's own: break a model and the compiler's diagnostic is on the file, then fix it and it is gone.
  await page.getByTestId('build-tab-edit').click()
  await page.getByTestId('build-edit-workspace-name').fill('draft')
  await page.getByTestId('build-edit-workspace-make').click()

  // Forms (W05): a file with a schema is a form as well as YAML, and both are the same text. A form changes the part it shows and leaves the rest of the file, comments included, as it was.
  const expectYaml = async (...expected: (string | RegExp)[]) => { await page.getByTestId('build-edit-mode-yaml').click(); for (const e of expected) await expect(page.getByTestId('build-edit-editor')).toContainText(e); await page.getByTestId('build-edit-mode-form').click() }
  await page.getByTestId('build-edit-file-models/staging/orders.yml').click()                       // a model: add a description with the add chip
  await expect(page.getByTestId('form-field-name')).toHaveValue('staging.orders')
  await expect(page.getByTestId('form-field-columns.0.name')).toHaveValue('order_id')
  await page.getByTestId('form-add-description').click()
  await page.getByTestId('form-field-description').fill('Orders with their statuses grouped')
  await expectYaml('description: Orders with their statuses grouped', 'nullable: false')
  await page.getByTestId('build-edit-save').click()
  await expect(page.getByTestId('build-edit-clean')).toBeVisible()
  await page.getByTestId('build-edit-file-models/staging/_dbdatabuild.yml').click()                // a folder's settings: a tag added to a list
  await expect(page.getByTestId('form-field-defaults.tags.0')).toHaveValue('staging')
  await page.getByTestId('form-append-defaults.tags').click()
  await page.getByTestId('form-field-defaults.tags.1').fill('curated')
  await expectYaml(/tags:\s*\[\s*staging,\s*curated\s*\]/)
  await page.getByTestId('build-edit-save').click()
  await expect(page.getByTestId('build-edit-clean')).toBeVisible()
  await page.getByTestId('build-edit-file-tests/models/staging/orders.yml').click()                 // a model test: a case renamed, the header comments stay
  await expect(page.getByTestId('form-field-cases.0.name')).toHaveValue('every raw status lands in one group')
  await page.getByTestId('form-field-cases.0.name').fill('every raw status has a group')
  await expectYaml('# description: staging.orders groups the raw statuses', '- name: every raw status has a group')
  await page.getByTestId('build-edit-save').click()
  await expect(page.getByTestId('build-edit-clean')).toBeVisible()
  await page.getByTestId('build-edit-file-connections/sqlserver.yaml').click()                    // a connection file: its title (the tool's own settings are under extensions.build)
  await page.getByTestId('form-field-extensions.build.title').fill('Main warehouse')
  await expectYaml('Main warehouse', '# Connections are files (connection/1), one per connection')
  await page.getByTestId('build-edit-save').click()
  await expect(page.getByTestId('build-edit-clean')).toBeVisible()
  await page.getByTestId('build-edit-file-models/staging/orders.sql').click()
  await expect(page.getByTestId('build-edit-open')).toHaveText('models/staging/orders.sql')
  await expect(page.getByTestId('build-edit-clean')).toBeVisible()
  // the lowered text and sample rows beside the editor follow the saved file: limit the query and both change
  await expect(page.getByTestId('build-preview-text').first()).toContainText('status_group')
  await expect(page.getByTestId('build-preview-text').first()).not.toContainText('LIMIT 2')
  await expect.poll(() => page.getByTestId('build-preview-row').count()).toBeGreaterThan(2)
  await page.getByTestId('build-edit-editor').locator('.monaco-editor').click()
  await page.keyboard.press('Control+End')
  await page.keyboard.insertText(' LIMIT 2')   // inserted, not typed: typing lets the editor's suggestions finish the word
  await page.getByTestId('build-edit-save').click()
  await expect(page.getByTestId('build-preview-text').first()).toContainText('LIMIT 2')
  await expect.poll(() => page.getByTestId('build-preview-row').count()).toBe(2)
  await page.getByTestId('build-edit-editor').locator('.monaco-editor').click()
  await page.keyboard.press('Control+End')
  await page.keyboard.type(')')
  await page.getByTestId('build-edit-save').click()
  await expect(page.getByTestId('build-edit-diagnostic').first()).toContainText('DDB-306')
  await expect(page.getByTestId('build-edit-editor').locator('.squiggly-error').first()).toBeVisible()
  await page.getByTestId('build-edit-editor').locator('.monaco-editor').click()
  await page.keyboard.press('Control+End')
  await page.keyboard.press('Backspace')
  await page.getByTestId('build-edit-save').click()
  await expect(page.getByTestId('build-edit-clean')).toBeVisible()

  // Taking the draft in: it shows the changed file, is committed, and merged into the project (an approver's act; the administrator is one), which ends the workspace.
  await expect(page.getByTestId('build-git-change').filter({ hasText: 'models/staging/orders.sql' })).toHaveCount(1)
  await expect(page.getByTestId('build-git-change')).toHaveCount(5)                // the four files edited through forms, and the model's query
  await expect(page.getByTestId('build-git-merge')).toBeDisabled()
  // the changed query is shown side by side: the text it had on the left, the saved one (with the limit) on the right
  await page.getByTestId('build-git-change').filter({ hasText: 'models/staging/orders.sql' }).click()
  const diff = page.getByTestId('build-git-diff-editor')
  await expect(diff.locator('.monaco-diff-editor.side-by-side')).toBeVisible()
  await expect(diff.locator('.editor.original')).toBeVisible()
  await expect(diff.locator('.editor.modified')).toContainText('LIMIT 2')
  await expect(diff.locator('.editor.original')).not.toContainText('LIMIT 2')
  await page.getByTestId('build-git-message').fill('Limit staging orders')
  await page.getByTestId('build-git-commit').click()
  await expect(page.getByTestId('build-git-commit-row')).toContainText('Limit staging orders')
  await page.getByTestId('build-git-merge').click()
  await expect(page.getByTestId('build-edit-none')).toBeVisible()

  // The models run on generated rows, offline.
  await page.getByTestId('build-tab-sample').click()
  await page.getByTestId('build-sample-button').click()
  await expect(page.getByTestId('build-sample-table-marts.orders')).toContainText('rows')

  // A deploy, when there is a PostgreSQL to deploy to: where it stands, a plan written and reviewed, a dry run, then approved and applied by the person at the console.
  if (warehouseConnection) {
    const serverUrl = page.url().replace(/\/projects\/starter\/sample$/, '')
    await page.goto(`${serverUrl}/projects/warehouse/status`)
    await expect(page.getByTestId('build-status-structure')).toHaveText('pending')
    await expect(page.getByTestId('build-status-object-marts.orders').getByTestId('build-status-state')).toContainText('missing')
    await page.getByTestId('build-tab-plans').click()
    await expect(page.getByTestId('build-plans-empty')).toBeVisible()
    await page.getByTestId('build-plan-write').click()
    await expect(page.getByTestId('build-plan-review')).toBeVisible()
    await expect(page.getByTestId('build-plan-facts')).toContainText('11 steps')
    await page.getByTestId('build-plan-step-5').locator('summary').click()
    await expect(page.getByTestId('build-plan-step-5')).toContainText('CREATE TABLE')
    await page.getByTestId('build-plan-dry-run').click()
    await expect(page.getByTestId('build-plan-dry-ok')).toBeVisible()
    await page.getByTestId('build-plan-apply').click()
    await expect(page.getByTestId('build-plan-applied')).toBeVisible()
    await page.getByTestId('build-tab-status').click()
    await page.getByTestId('build-status-refresh').click()
    await expect(page.getByTestId('build-status-object-marts.orders').getByTestId('build-status-state')).not.toContainText('missing')

    // Two of the deployed tables compared: counts and digests, never the values.
    await page.getByTestId('build-tab-compare').click()
    await page.getByTestId('build-compare-table').fill('marts.orders')
    await page.getByTestId('build-compare-against').fill('staging.orders')
    await page.getByTestId('build-compare-key').fill('order_id')
    await page.getByTestId('build-compare-button').click()
    await expect(page.getByTestId('build-compare-verdict')).toHaveText('Identical')
    await expect(page.getByTestId('build-compare-rows')).toContainText('251 matched')
  }

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

  // a JWT claim mapping, saved as a list into proxy/auth.yaml and shown again from the server
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
  await expect(page.getByTestId('proxy-activity')).toBeVisible()
  await expect(page.getByTestId('lockouts-table')).toContainText('Nobody is locked out.')

  // The user made above is a commit by the person who made it.
  await page.getByTestId('rail-config').click()
  await expect(page.getByTestId('commits-table').locator('[data-testid^="commit-"]').first()).toBeVisible()

  // Publishing (plan 13a, T11 and T12): the console links the Build server to the Proxy (no code typed), the Build screen publishes the starter's connection, and the Proxy lists the data source it made.
  // The first Build pairing was forgotten above, so the server is paired again, with the second code made for it.
  await page.getByTestId('rail-home-link').click()
  await page.getByTestId('pair-url-input').fill(buildUrl)
  await page.getByTestId('pair-code-input').fill(fs.readFileSync(path.join(scratchDirectory, 'build-pairing-code-again.txt'), 'utf8').trim())
  await page.getByTestId('pair-name-input').fill('Build one')
  await page.getByTestId('pair-button').click()
  await expect(page.getByTestId('instance-row-Build one').getByTestId('instance-status')).toHaveText('Ready')
  await expect(page.getByTestId('link-card')).toBeVisible()
  await page.getByTestId('link-from-select').selectOption({ label: 'Build one' })
  await page.getByTestId('link-to-select').selectOption({ label: 'Proxy one' })
  await page.getByTestId('link-button').click()
  await expect(page.getByTestId('link-done')).toBeVisible()
  await page.getByTestId('instance-row-Build one').getByTestId('instance-open-link').click()
  await page.getByTestId('build-project-starter').getByTestId('build-project-link').click()
  await page.getByTestId('build-tab-publish').click()
  await expect(page.getByTestId('build-publish-button')).toBeDisabled()   // a proxy and a connection first
  await page.getByTestId('build-publish-proxy').selectOption({ label: 'proxy-one' })
  await page.getByTestId('build-publish-connection').fill('sqlserver')
  await page.getByTestId('build-publish-data-source').fill('warehouse')
  await page.getByTestId('build-publish-button').click()
  await expect(page.getByTestId('build-publish-done')).toContainText('warehouse')
  await expect(page.getByTestId('build-publish-done')).toContainText('(new)')
  // a second time is a change to the data source the Build server made, not another one
  await page.getByTestId('build-publish-button').click()
  await expect(page.getByTestId('build-publish-done')).toContainText('updated')
  await expect(page.getByTestId('build-publish-done')).not.toContainText('(new)')
  // a data source made by hand on the Proxy (erp) is refused, in the Proxy's words, with the way an administrator settles it
  await page.getByTestId('build-publish-data-source').fill('erp')
  await page.getByTestId('build-publish-button').click()
  await expect(page.getByTestId('build-publish-failed')).toContainText('not made by a publisher')
  await expect(page.getByTestId('build-publish-adopt')).toBeVisible()
  // T13: the administrator hands it over from the offer (the call goes to the Proxy through the console's relay), and the next publish is a change to a data source that is the Build server's now
  await page.getByTestId('build-publish-adopt-button').click()
  await expect(page.getByTestId('build-publish-adopted')).toContainText('erp')
  await expect(page.getByTestId('build-publish-failed')).toHaveCount(0)
  await page.getByTestId('build-publish-button').click()
  await expect(page.getByTestId('build-publish-done')).toContainText('erp')
  await expect(page.getByTestId('build-publish-done')).toContainText('updated')
  // the other hand-made data source is refused the same way, and handed over from the Proxy's own page instead
  await page.getByTestId('build-publish-data-source').fill('legacy')
  await page.getByTestId('build-publish-button').click()
  await expect(page.getByTestId('build-publish-failed')).toContainText('not made by a publisher')
  await expect(page.getByTestId('build-publish-adopt')).toBeVisible()
  // the Proxy shows what was made, and who manages each data source
  await page.getByTestId('rail-home-link').click()
  await page.getByTestId('instance-row-Proxy one').getByTestId('instance-open-link').click()
  await page.getByTestId('rail-data-sources').click()
  await expect(page.getByTestId('data-source-warehouse')).toBeVisible()
  await expect(page.getByTestId('data-source-erp').getByTestId('data-source-owner')).not.toHaveText('by hand')
  const legacy = page.getByTestId('data-source-legacy')
  await expect(legacy.getByTestId('data-source-owner')).toHaveText('by hand')
  await expect(legacy.getByTestId('hand-over-button')).toBeDisabled()   // a server first
  await legacy.getByTestId('hand-over-client').selectOption({ index: 1 })
  await legacy.getByTestId('hand-over-button').click()
  await expect(legacy.getByTestId('hand-over-done')).toContainText('Handed to')
  await expect(legacy.getByTestId('data-source-owner')).not.toHaveText('by hand')
  // and the Build server publishes it now
  await page.getByTestId('rail-home-link').click()
  await page.getByTestId('instance-row-Build one').getByTestId('instance-open-link').click()
  await page.getByTestId('build-project-starter').getByTestId('build-project-link').click()
  await page.getByTestId('build-tab-publish').click()
  await page.getByTestId('build-publish-proxy').selectOption({ label: 'proxy-one' })
  await page.getByTestId('build-publish-connection').fill('sqlserver')
  await page.getByTestId('build-publish-data-source').fill('legacy')
  await page.getByTestId('build-publish-button').click()
  await expect(page.getByTestId('build-publish-done')).toContainText('legacy')
  await expect(page.getByTestId('build-publish-done')).toContainText('updated')

  // One view across the servers: the Build, Sync and Proxy servers all answer, and none has a replication or a scheduled plan yet (a failed run and a lagging replication need real runs, so the rows themselves are tested on what the servers return)
  await page.goto('/monitoring')   // the rail inside a server's screens is that product's own
  await expect(page.getByTestId('monitoring-none')).toBeVisible()
  await expect(page.getByTestId('monitoring-problem')).toHaveCount(0)
})
