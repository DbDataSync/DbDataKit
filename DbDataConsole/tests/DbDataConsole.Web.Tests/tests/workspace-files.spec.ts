import { expect, test, type BrowserContext, type Page } from '@playwright/test'
import { execFileSync } from 'node:child_process'
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { scratchDirectory, workspaceEditServer } from '../playwright.config'

// Plan 22a GP35a: editing in a workspace, through the console. The console pairs with a `dbdataworkspace` that hosts the project `orders`; a person makes a workspace, opens it, makes a file (a path that leaves the workspace or touches .git is
// refused as they type), edits it in the code editor and saves, reads what changed side by side, commits, moves the file, sees the server's own words for a push (the project has no git remote), commits again and merges: the project's own copy
// then holds the file and the workspace is gone. Everything goes through the console's relay as the signed-in administrator. It invites an administrator, so it sorts after identity-home, which needs a console with no user.
const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../../..')
const consoleDll = path.join(repoRoot, 'DbDataConsole/src/DbDataConsole.Server/bin/Debug/net10.0/dbdatakit.dll')
const name = 'Workspace files e2e'

async function signInAsAdmin(page: Page, context: BrowserContext, baseURL: string) {
  const cdp = await context.newCDPSession(page)
  await cdp.send('WebAuthn.enable')
  await cdp.send('WebAuthn.addVirtualAuthenticator', {
    options: { protocol: 'ctap2', transport: 'internal', hasResidentKey: true, hasUserVerification: true, isUserVerified: true, automaticPresenceSimulation: true },
  })
  const made = execFileSync('dotnet', ['exec', consoleDll, 'user', 'invite', '--email', `workspace-files-${Date.now()}-${Math.random().toString(36).slice(2, 8)}@example.com`, '--role', 'admin', '--url', baseURL, '--repo', scratchDirectory], { encoding: 'utf8' })
  await page.goto(`/invite#${/\/invite#(\S+)/.exec(made)![1]}`)
  await page.getByTestId('invite-name-input').fill('Workspace Editor')
  await page.getByTestId('invite-enrol-button').click()
  await expect(page.getByTestId('home-page')).toBeVisible()
}

test('a person makes a workspace, edits a file, reads the diff, commits, is told the server\'s words for a push and merges', async ({ page, context, baseURL }) => {
  test.setTimeout(180_000)
  await signInAsAdmin(page, context, baseURL!)
  const project = workspaceEditServer.project
  const server = workspaceEditServer.directory

  // ---- pair it and make a workspace
  await page.goto('/instances')
  await page.getByTestId('pair-url-input').fill(workspaceEditServer.url)
  await page.getByTestId('pair-code-input').fill(fs.readFileSync(path.join(scratchDirectory, 'workspace-edit-pairing-code.txt'), 'utf8').trim())
  await page.getByTestId('pair-name-input').fill(name)
  await page.getByTestId('pair-button').click()
  const row = page.getByTestId(`instance-row-${name}`)
  await expect(row.getByTestId('instance-status')).toHaveText('Ready', { timeout: 30_000 })
  await row.getByTestId('instance-open-link').click()
  await page.getByTestId(`ws-new-name-${project}`).fill('edit-orders')
  await page.getByTestId(`ws-new-create-${project}`).click()
  const listed = page.locator('[data-testid="ws-workspace"][data-workspace$="-edit-orders"]')
  await expect(listed).toBeVisible()
  const id = (await listed.getAttribute('data-workspace'))!
  const folder = path.join(server, 'workspaces', project, id)
  await page.getByTestId(`ws-open-${id}`).click()

  // ---- its page: the files of the draft, the editor empty until a file is picked, a commit not yet possible
  await expect(page.getByTestId('wsp-page')).toBeVisible()
  await expect(page.getByTestId('wsp-title')).toHaveText('edit-orders')
  await expect(page.locator('[data-testid="wsp-file"][data-path="README.md"]')).toBeVisible()
  await expect(page.getByTestId('wsp-pick')).toBeVisible()
  await expect(page.getByTestId('wsp-changes-none')).toBeVisible()
  await expect(page.getByTestId('wsp-commit')).toBeDisabled()
  await expect(page.getByTestId('wsp-merge')).toBeDisabled()
  await expect(page.getByTestId('wsp-merge-why')).toContainText('Commit a change first')

  // ---- a path that leaves the workspace or touches git is refused as it is typed, and nothing is made
  const newPath = page.getByTestId('wsp-new-path')
  for (const [bad, why] of [['../outside.yaml', '..'], ['.git/config', '.git'], ['/etc/passwd', 'start with']] as const) {
    await newPath.fill(bad)
    await expect(page.getByTestId('wsp-new-file')).toBeDisabled()
    await expect(page.getByTestId('wsp-new-why')).toContainText(why)
  }
  expect(fs.existsSync(path.join(folder, '.git', 'config'))).toBe(false)
  expect(fs.existsSync(path.join(server, 'workspaces', project, 'outside.yaml'))).toBe(false)

  // ---- make a file, write it in the editor and save
  await newPath.fill('views/orders.yaml')
  await page.getByTestId('wsp-new-file').click()
  await expect(page.getByTestId('wsp-editor')).toHaveAttribute('data-path', 'views/orders.yaml')
  await expect(page.locator('[data-testid="wsp-file"][data-path="views/orders.yaml"]')).toBeVisible()
  expect(fs.existsSync(path.join(folder, 'views', 'orders.yaml'))).toBe(true)
  await expect(page.getByTestId('wsp-save')).toBeDisabled()
  await page.getByTestId('wsp-code').locator('.monaco-editor').click()
  await page.keyboard.press('Control+A')
  await page.keyboard.insertText('schemaVersion: 1\nkind: grid\ntitle: Orders\n')
  await expect(page.getByTestId('wsp-dirty')).toBeVisible()
  await expect(page.getByTestId('wsp-save')).toBeEnabled()
  await page.getByTestId('wsp-save').click()
  await expect(page.getByTestId('wsp-dirty')).toHaveCount(0)
  await expect.poll(() => fs.readFileSync(path.join(folder, 'views', 'orders.yaml'), 'utf8').replace(/\r\n/g, '\n')).toBe('schemaVersion: 1\nkind: grid\ntitle: Orders\n')
  expect(fs.existsSync(path.join(server, 'projects', project, 'views'))).toBe(false)     // the project's own copy waits for a merge

  // ---- what changed, side by side
  const change = page.locator('[data-testid="wsp-change"][data-path="views/orders.yaml"]')
  await expect(change).toHaveAttribute('data-status', 'added')
  await change.getByTestId('wsp-change-toggle').click()
  await expect(change.locator('.monaco-diff-editor.side-by-side')).toBeVisible()

  // ---- commit it: the history names the commit
  await page.getByTestId('wsp-commit-message').fill('Add the orders view')
  await page.getByTestId('wsp-commit').click()
  await expect(page.getByTestId('wsp-committed')).toContainText('Committed')
  await page.getByTestId('wsp-tab-history').click()
  await expect(page.locator('[data-testid="wsp-commit-row"]')).toHaveCount(1)
  await expect(page.locator('[data-testid="wsp-commit-row"]')).toContainText('Add the orders view')
  await expect(page.locator('[data-testid="wsp-event"][data-kind="committed"]')).toHaveCount(1)
  await page.getByTestId('wsp-tab-changes').click()

  // ---- move the file; the tree follows
  await page.getByTestId('wsp-move-open').click()
  await page.getByTestId('wsp-move-target').fill('views/../customers.yaml')
  await expect(page.getByTestId('wsp-move-go')).toBeDisabled()
  await page.getByTestId('wsp-move-target').fill('views/customers.yaml')
  await page.getByTestId('wsp-move-go').click()
  await expect(page.locator('[data-testid="wsp-file"][data-path="views/customers.yaml"]')).toBeVisible()
  await expect(page.locator('[data-testid="wsp-file"][data-path="views/orders.yaml"]')).toHaveCount(0)
  expect(fs.existsSync(path.join(folder, 'views', 'customers.yaml'))).toBe(true)
  // the merge waits for a commit of the move, and says so
  await expect(page.getByTestId('wsp-merge')).toBeDisabled()
  await expect(page.getByTestId('wsp-merge-why')).toContainText('commit them')

  // ---- a push is the server's answer: this project has no remote
  await page.getByTestId('wsp-push').click()
  await expect(page.getByTestId('error-banner')).toContainText('no git remote')
  await page.getByTestId('wsp-commit-message').fill('Rename the view')
  await page.getByTestId('wsp-commit').click()
  await expect(page.getByTestId('wsp-committed')).toContainText('Committed')

  // ---- merge it: the project's own copy holds the file, and the workspace is finished
  await expect(page.getByTestId('wsp-merge')).toBeEnabled()
  await page.getByTestId('wsp-merge').click()
  await expect(page.getByTestId('wsp-merged')).toBeVisible()
  expect(fs.readFileSync(path.join(server, 'projects', project, 'views', 'customers.yaml'), 'utf8').replace(/\r\n/g, '\n')).toBe('schemaVersion: 1\nkind: grid\ntitle: Orders\n')
  expect(fs.existsSync(folder)).toBe(false)
  await page.getByTestId('wsp-back').click()
  await expect(page.getByTestId(`ws-empty-${project}`)).toBeVisible()
})
