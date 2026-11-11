import { expect, test } from '@playwright/test'
import { execFileSync, spawn, type ChildProcess } from 'node:child_process'
import fs from 'node:fs'
import https from 'node:https'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { signInNewAdmin } from './people'

// Plan 13a U21: the identity home is administered through the management API of whatever hosts it, and the console shows its screens through the relay. An identity home that runs on its own (`dbdatakit identity run`)
// is paired with the console like any server; an administrator lists and removes a role assignment and an external group mapping on it from the console. The home itself serves none of the console's screens.
const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../../..')
const consoleDll = path.join(repoRoot, 'DbDataKit/src/DbDataKit.Server/bin/Debug/net10.0/dbdatakit.dll')
const homeDirectory = path.join(os.tmpdir(), 'dbdatakit-console-e2e-identity-admin')
const homePort = 8499
const homeUrl = `https://localhost:${homePort}`

let home: ChildProcess | undefined

/** `dbdatakit identity ...` on the home's directory, as its operator runs it; the JSON document's data. */
function identity(...args: string[]): Record<string, any> {
  return JSON.parse(execFileSync('dotnet', ['exec', consoleDll, 'identity', ...args, '--repo', homeDirectory, '--format', 'json'], { encoding: 'utf8' })).data
}

function get(url: string) {
  return new Promise<{ status: number; type: string; body: string }>((resolve) => {
    https.get(url, { rejectUnauthorized: false }, (response) => {
      let body = ''
      response.on('data', (chunk) => { body += chunk })
      response.on('end', () => resolve({ status: response.statusCode ?? 0, type: String(response.headers['content-type'] ?? ''), body }))
    }).on('error', () => resolve({ status: 0, type: '', body: '' }))
  })
}

test.beforeAll(async () => {
  fs.rmSync(homeDirectory, { recursive: true, force: true })
  fs.mkdirSync(homeDirectory, { recursive: true })
  // what there is to administer: a service account with a role on a server, and an Entra group mapped to a role on another
  const account = identity('service-account-add', '--name', 'nightly-refresh')
  identity('assign', '--user', account.id, '--server', 'build-1', '--roles', 'operator')
  identity('map-group', '--source', 'entra', '--group', '7f3c1b0e-0000-4b1d-9c11-00000000beef', '--server', 'build-2', '--roles', 'viewer')

  home = spawn('dotnet', ['exec', consoleDll, 'identity', 'run', '--repo', homeDirectory, '--listen', homeUrl], { stdio: 'ignore' })
  for (let i = 0; i < 120 && (await get(`${homeUrl}/health/live`)).status !== 200; i++) await new Promise((r) => setTimeout(r, 500))
})

test.afterAll(() => { home?.kill() })

test('an administrator lists and removes a role assignment and an external group mapping on the identity home through the console', async ({ page, context, baseURL }) => {
  // the home serves no screen of the console's: a browser that opens it is told to pair a console
  const users = await get(`${homeUrl}/users`)
  expect(users.type).toContain('text/plain')
  expect(users.body).toContain('pair a console')

  await signInNewAdmin(page, context, baseURL!, 'Grace Hopper', `grace-${Date.now()}@example.com`)
  const made = execFileSync('dotnet', ['exec', consoleDll, 'identity', 'pair', '--max-role', 'viewer,operator,admin', '--repo', homeDirectory], { encoding: 'utf8' })
  await page.goto('/instances')
  await page.getByTestId('pair-url-input').fill(homeUrl)
  await page.getByTestId('pair-code-input').fill(/Pairing code: (\S+)/.exec(made)![1])
  await page.getByTestId('pair-name-input').fill('Identity home')
  await page.getByTestId('pair-button').click()
  const row = page.getByTestId('instance-row-Identity home')
  await expect(row.getByTestId('instance-status')).toHaveText('Ready')
  await row.getByTestId('instance-open-link').click()
  await expect(page.getByTestId('identity-admin')).toBeVisible()

  // the role assignment, listed through the relay, and removed
  const assignment = page.getByTestId('identity-assignment-nightly-refresh')
  await expect(assignment).toContainText('build-1')
  await expect(assignment).toContainText('operator')
  const removed = page.waitForResponse((r) => r.url().includes('/api/v1/identity/assignments/') && r.request().method() === 'DELETE')
  await assignment.getByTestId('identity-assignment-remove').click()
  expect((await removed).status()).toBe(204)
  await expect(assignment).toHaveCount(0)
  await expect(page.getByTestId('identity-assignments')).toContainText('No role is assigned.')

  // the external group mapping, likewise
  const mapping = page.getByTestId('identity-mapping-7f3c1b0e-0000-4b1d-9c11-00000000beef')
  await expect(mapping).toContainText('entra')
  await expect(mapping).toContainText('build-2')
  await mapping.getByTestId('identity-mapping-remove').click()
  await expect(mapping).toHaveCount(0)

  // gone at the home itself, as its command line sees it
  await page.reload()
  await expect(page.getByTestId('identity-mappings')).toContainText('No Entra or Windows group gives a role.')
  const listed = JSON.parse(execFileSync('dotnet', ['exec', consoleDll, 'identity', 'service-account-list', '--repo', homeDirectory, '--format', 'json'], { encoding: 'utf8' })).data
  expect(listed.accounts.map((a: { name: string }) => a.name)).toContain('nightly-refresh')
})
