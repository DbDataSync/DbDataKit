import { expect, test } from '@playwright/test'
import { execFileSync, spawn, type ChildProcess } from 'node:child_process'
import fs from 'node:fs'
import https from 'node:https'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { signInNewAdmin } from './people'

// The Add a server screen: an administrator makes the command a Runner joins the console with, runs it against a real Runner (one this story starts in a
// directory of its own, port 8641: no other story or fixture uses it), and the Runner is listed ready; a second command is cancelled and is audited. The
// story invites its own administrator, so it sorts after identity-home (docs/ci-lessons.md, "Playwright (console)").
const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../../..')
const runnerDll = path.join(repoRoot, 'DbDataRunner/src/DbDataRunner.Server/bin/Debug/net10.0/dbdatarunner.dll')
const runnerPort = 8641
const runnerUrl = `https://localhost:${runnerPort}`
const runnerDirectory = path.join(os.tmpdir(), 'ddk-e2e-join')

let runner: ChildProcess | null = null
let runnerOutput = ''

function answers(url: string) {
  return new Promise<boolean>((resolve) => {
    https.get(url, { rejectUnauthorized: false }, (response) => { response.resume(); resolve(response.statusCode === 200) }).on('error', () => resolve(false))
  })
}

test.describe.serial('adding a server by the command the console makes', () => {
  test.beforeAll(() => {
    fs.rmSync(runnerDirectory, { recursive: true, force: true })
    fs.mkdirSync(runnerDirectory, { recursive: true })
  })

  test.afterAll(() => {
    runner?.kill()
    fs.rmSync(runnerDirectory, { recursive: true, force: true })
  })

  test('the command made on the screen joins a Runner, which is then listed ready, and a cancelled command is audited', async ({ page, context, baseURL }) => {
    test.setTimeout(180_000)
    await signInNewAdmin(page, context, baseURL!, 'Joining Admin', `join-${Date.now()}@example.com`)

    // the screen is an entry of the Servers group and is linked from All servers
    await page.goto('/instances')
    await page.getByTestId('instances-add-server-link').click()
    await expect(page.getByTestId('add-server-page')).toBeVisible()
    await expect(page.getByTestId('rail-add-server')).toHaveClass(/active/)

    // nothing is made until a server is chosen; the address is the one this browser reached the console at
    await page.getByTestId('add-server-make-button').click()
    await expect(page.getByTestId('add-server-problem-product')).toBeVisible()
    await expect(page.getByTestId('add-server-url-input')).toHaveValue(baseURL!)
    await expect(page.getByTestId('add-server-made')).toHaveCount(0)

    // a command for a Gateway, cancelled: it leaves the waiting list and the audit records who cancelled it
    await page.getByTestId('add-server-product-gateway').check()
    await page.getByTestId('add-server-make-button').click()
    const gatewayCommand = await page.getByTestId('add-server-command').inputValue()
    expect(gatewayCommand).toMatch(/^dbdatagateway pair --host /)
    const waitingRows = page.locator('[data-testid^="waiting-token-jt-"]')
    await expect(waitingRows).toHaveCount(1)
    await waitingRows.first().getByTestId('waiting-token-cancel-button').click()
    await expect(waitingRows).toHaveCount(0)
    await expect(page.getByTestId('add-server-waiting-empty')).toBeVisible()
    const audit = await (await context.request.get('/api/audit?verb=server.invite.cancel')).json()
    expect(audit.entries.length).toBeGreaterThanOrEqual(1)
    expect(JSON.stringify(audit)).not.toContain(gatewayCommand.split(' ')[5])

    // the Runner's command, with a ceiling of viewer, operator and admin
    await page.getByTestId('add-server-product-runner').check()
    await page.getByTestId('add-server-role-viewer').check()
    await page.getByTestId('add-server-role-operator').check()
    await page.getByTestId('add-server-make-button').click()
    await expect(page.getByTestId('add-server-made')).toContainText("Run this on the runner's machine")
    const command = await page.getByTestId('add-server-command').inputValue()
    expect(command).toMatch(new RegExp(`^dbdatarunner pair --host ${baseURL} --token [a-z2-7]{26} --console-thumbprint sha256:[0-9a-f]{64}$`))
    const token = command.split(' ')[5]
    // localhost: the screen says only a server on this machine reaches it, which is where this Runner is
    await expect(page.getByTestId('add-server-loopback')).toBeVisible()
    await expect(waitingRows).toHaveCount(1)
    await expect(waitingRows.first()).toContainText('viewer, operator, admin')
    await expect(waitingRows.first()).toContainText('Joining Admin')
    // the waiting list never shows a token
    await expect(page.getByTestId('add-server-waiting')).not.toContainText(token)

    // run the command on the Runner's machine as printed: with no service registered it says to start the server, which then joins at its start
    const [program, ...args] = command.split(' ')
    expect(program).toBe('dbdatarunner')
    const paired = execFileSync('dotnet', ['exec', runnerDll, ...args, '--repo', runnerDirectory], { encoding: 'utf8' })
    expect(paired).toContain('Start the server to join')
    runner = spawn('dotnet', ['exec', runnerDll, 'server', 'run', '--repo', runnerDirectory, '--listen', runnerUrl], { stdio: ['ignore', 'pipe', 'pipe'] })
    runner.stdout!.on('data', (chunk) => { runnerOutput += chunk })
    runner.stderr!.on('data', (chunk) => { runnerOutput += chunk })
    await expect.poll(() => answers(`${runnerUrl}/health/live`), { timeout: 90_000, message: `the Runner did not start:\n${runnerOutput}` }).toBe(true)
    // the join step comes after the ports step, so the listener answers a moment before the request is spent
    await expect.poll(() => fs.existsSync(path.join(runnerDirectory, 'join-request.yaml')), { timeout: 30_000, message: `the join request is still there:\n${runnerOutput}` }).toBe(false)

    // the token is spent: it leaves the waiting list without a reload
    await expect(waitingRows).toHaveCount(0, { timeout: 30_000 })

    // the Runner is listed ready under All servers, at the address it gave
    await page.getByTestId('rail-instances').click()
    const row = page.locator('[data-testid^="instance-row-"]').filter({ hasText: runnerUrl })
    await expect(row.getByTestId('instance-status')).toHaveText('Ready', { timeout: 30_000 })
    await expect(row).toContainText('dbdatarunner')

    // tidy: forget it, so the console's list is as the other stories expect it
    await row.getByTestId('instance-forget-button').click()
    await expect(page.locator('[data-testid^="instance-row-"]').filter({ hasText: runnerUrl })).toHaveCount(0)
  })
})
