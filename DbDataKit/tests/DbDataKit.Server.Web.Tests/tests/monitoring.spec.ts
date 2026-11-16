import { expect, test, type BrowserContext, type Page } from '@playwright/test'
import { execFileSync } from 'node:child_process'
import fs from 'node:fs'
import http2 from 'node:http2'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { monitorGateway, scratchDirectory } from '../playwright.config'

// Plan 13a, M12 (the done-when of M11): the console's Monitoring page across real servers. A Gateway has locked a user out, and the page lists the Gateway's notice. (The Build server whose scheduled plan failed and the Sync
// server's lagging replication went with those servers, plan 33 SC23: a scheduled refresh and a replication's drain are runner tasks, paused and resumed on the runner's screens, runner-schedule.spec.ts.) The Gateway is the one
// playwright.config.ts starts for this file.
const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../../..')
const consoleDll = path.join(repoRoot, 'DbDataKit/src/DbDataKit.Server/bin/Debug/net10.0/dbdatakit.dll')

const gatewayName = 'Monitor gateway'

async function signInAsAdmin(page: Page, context: BrowserContext, baseURL: string) {
  const cdp = await context.newCDPSession(page)
  await cdp.send('WebAuthn.enable')
  await cdp.send('WebAuthn.addVirtualAuthenticator', {
    options: { protocol: 'ctap2', transport: 'internal', hasResidentKey: true, hasUserVerification: true, isUserVerified: true, automaticPresenceSimulation: true },
  })
  const made = execFileSync('dotnet', ['exec', consoleDll, 'user', 'invite', '--email', `monitoring-${Date.now()}-${Math.random().toString(36).slice(2, 8)}@example.com`, '--role', 'admin', '--url', baseURL, '--repo', scratchDirectory], { encoding: 'utf8' })
  await page.goto(`/invite#${/\/invite#(\S+)/.exec(made)![1]}`)
  await page.getByTestId('invite-name-input').fill('Monitoring Tester')
  await page.getByTestId('invite-enrol-button').click()
  await expect(page.getByTestId('home-page')).toBeVisible()
}

/** Pairs a server with the code made for it, through the Servers page, and waits for it to answer. */
async function pair(page: Page, url: string, codeFile: string, name: string) {
  await page.goto('/instances')
  await expect(page.getByTestId('instances-page')).toBeVisible()
  await page.getByTestId('pair-url-input').fill(url)
  await page.getByTestId('pair-code-input').fill(fs.readFileSync(path.join(scratchDirectory, codeFile), 'utf8').trim())
  await page.getByTestId('pair-name-input').fill(name)
  await page.getByTestId('pair-button').click()
  await expect(page.getByTestId(`instance-row-${name}`).getByTestId('instance-status')).toHaveText('Ready', { timeout: 30_000 })
}

/** Failed logins to the Gateway's Flight port: the handshake with a wrong password, as many times as the lockout counts. Nothing else of Flight is needed. */
async function failLogins(port: number, user: string, times: number) {
  const session = http2.connect(`https://localhost:${port}`, { rejectUnauthorized: false })
  try {
    for (let i = 0; i < times; i++) {
      await new Promise<void>((resolve, reject) => {
        const call = session.request({
          ':method': 'POST', ':path': '/arrow.flight.protocol.FlightService/Handshake', 'content-type': 'application/grpc', te: 'trailers',
          authorization: `Basic ${Buffer.from(`${user}:wrong-${i}`).toString('base64')}`,
        })
        call.on('error', reject)
        call.on('close', () => resolve())
        call.resume()
        call.end(Buffer.from([0, 0, 0, 0, 0]))
      })
    }
  } finally { session.close() }
}

test('a Gateway notice is listed on the Monitoring page with its server', async ({ page, context, baseURL }) => {
  test.setTimeout(240_000)
  await signInAsAdmin(page, context, baseURL!)
  await pair(page, `https://localhost:${monitorGateway.managementPort}`, 'monitor-gateway-pairing-code.txt', gatewayName)

  await failLogins(monitorGateway.flightPort, 'intruder', 5)

  await page.goto('/monitoring')
  await expect(page.getByTestId('monitoring-page')).toBeVisible()
  const notice = page.getByTestId('monitoring-row').filter({ hasText: 'Login locked out' })
  await expect(notice).toHaveAttribute('data-state', 'event')
  await expect(notice).toContainText('intruder')
  await expect(notice).toContainText(gatewayName)
  await expect(notice.getByTestId('monitoring-toggle')).toHaveCount(0)
})
