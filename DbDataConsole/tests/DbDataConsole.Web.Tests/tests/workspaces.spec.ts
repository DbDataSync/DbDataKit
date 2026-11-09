import { expect, test, type BrowserContext, type Page } from '@playwright/test'
import { execFileSync } from 'node:child_process'
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { scratchDirectory, workspaceServer } from '../playwright.config'

// Plan 22a GP31: the console and a real Workspace server. The console pairs with a `dbdataworkspace` that hosts the project `orders`, the Workspace screen lists its projects, an administrator makes a workspace in it (a git worktree on the
// server), sees it listed as theirs with its history ("made"), makes a second project from the screen, and abandons the workspace (asked twice), after which the history of the project says so. Everything is asked of the server through the
// console's relay, as the signed-in administrator. It invites an administrator, so it sorts after identity-home, which needs a console with no user.
const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../../..')
const consoleDll = path.join(repoRoot, 'DbDataConsole/src/DbDataConsole.Server/bin/Debug/net10.0/dbdatakit.dll')
const name = 'Workspace e2e'

async function signInAsAdmin(page: Page, context: BrowserContext, baseURL: string) {
  const cdp = await context.newCDPSession(page)
  await cdp.send('WebAuthn.enable')
  await cdp.send('WebAuthn.addVirtualAuthenticator', {
    options: { protocol: 'ctap2', transport: 'internal', hasResidentKey: true, hasUserVerification: true, isUserVerified: true, automaticPresenceSimulation: true },
  })
  const made = execFileSync('dotnet', ['exec', consoleDll, 'user', 'invite', '--email', `workspace-${Date.now()}-${Math.random().toString(36).slice(2, 8)}@example.com`, '--role', 'admin', '--url', baseURL, '--repo', scratchDirectory], { encoding: 'utf8' })
  await page.goto(`/invite#${/\/invite#(\S+)/.exec(made)![1]}`)
  await page.getByTestId('invite-name-input').fill('Workspace Tester')
  await page.getByTestId('invite-enrol-button').click()
  await expect(page.getByTestId('home-page')).toBeVisible()
}

test('the console pairs with a Workspace server, makes a workspace, reads its history, makes a project and abandons the workspace', async ({ page, context, baseURL }) => {
  test.setTimeout(120_000)
  await signInAsAdmin(page, context, baseURL!)

  // ---- pair it like any server
  await page.goto('/instances')
  await page.getByTestId('pair-url-input').fill(workspaceServer.url)
  await page.getByTestId('pair-code-input').fill(fs.readFileSync(path.join(scratchDirectory, 'workspace-pairing-code.txt'), 'utf8').trim())
  await page.getByTestId('pair-name-input').fill(name)
  await page.getByTestId('pair-button').click()
  const row = page.getByTestId(`instance-row-${name}`)
  await expect(row.getByTestId('instance-status')).toHaveText('Ready', { timeout: 30_000 })
  const list = await (await context.request.get('/api/instances')).json() as { name: string; product: string }[]
  expect(list.find((i) => i.name === name)!.product).toBe('dbdataworkspace')
  await row.getByTestId('instance-open-link').click()

  // ---- the screen: the project the server hosts, with no workspace yet
  await expect(page.getByTestId('ws-projects')).toBeVisible()
  const orders = page.locator(`[data-testid="ws-project"][data-project="${workspaceServer.project}"]`)
  await expect(orders).toBeVisible()
  await expect(page.getByTestId(`ws-empty-${workspaceServer.project}`)).toBeVisible()

  // ---- make a workspace: an invalid name is refused as you type, a good one is made and listed as the person's own
  const nameInput = page.getByTestId(`ws-new-name-${workspaceServer.project}`)
  const create = page.getByTestId(`ws-new-create-${workspaceServer.project}`)
  await expect(create).toBeDisabled()
  await nameInput.fill('Has Spaces')
  await expect(create).toBeDisabled()
  await expect(page.getByTestId(`ws-new-why-${workspaceServer.project}`)).toContainText('Lower-case')
  await nameInput.fill('new-orders')
  await expect(create).toBeEnabled()
  await create.click()
  const workspace = page.locator('[data-testid="ws-workspace"][data-workspace$="-new-orders"]')
  await expect(workspace).toBeVisible()
  await expect(workspace.getByTestId('ws-name')).toHaveText('new-orders')
  await expect(workspace.getByTestId('ws-owner')).toContainText('(you)')
  const id = (await workspace.getAttribute('data-workspace'))!
  // the worktree is on the server's disk
  expect(fs.existsSync(path.join(workspaceServer.directory, 'workspaces', workspaceServer.project, id, 'README.md'))).toBe(true)

  // the same name again is refused by the form before the server is asked
  await nameInput.fill('new-orders')
  await expect(create).toBeDisabled()
  await expect(page.getByTestId(`ws-new-why-${workspaceServer.project}`)).toContainText('already')
  await nameInput.fill('')

  // ---- its history says it was made
  await page.getByTestId(`ws-history-toggle-${id}`).click()
  await expect(page.locator(`[data-testid="ws-history-${id}"] [data-testid="ws-event"][data-kind="created"]`)).toHaveCount(1)

  // ---- a project is made from the screen (an administrator's)
  await page.getByTestId('ws-new-project-open').click()
  await page.getByTestId('ws-new-project-id').fill('billing')
  await page.getByTestId('ws-new-project-title').fill('Billing')
  await page.getByTestId('ws-new-project-create').click()
  await expect(page.locator('[data-testid="ws-project"][data-project="billing"]')).toBeVisible()
  expect(fs.existsSync(path.join(workspaceServer.directory, 'projects', 'billing', '.git'))).toBe(true)

  // ---- abandon is asked twice, and keeping it changes nothing
  await page.getByTestId(`ws-abandon-${id}`).click()
  await page.getByTestId(`ws-abandon-cancel-${id}`).click()
  await expect(workspace).toBeVisible()
  await page.getByTestId(`ws-abandon-${id}`).click()
  await page.getByTestId(`ws-abandon-confirm-${id}`).click()
  await expect(workspace).toHaveCount(0)
  await expect(page.getByTestId(`ws-empty-${workspaceServer.project}`)).toBeVisible()
  expect(fs.existsSync(path.join(workspaceServer.directory, 'workspaces', workspaceServer.project, id))).toBe(false)
})
