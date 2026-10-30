import { expect, test } from '@playwright/test'
import fs from 'node:fs'
import path from 'node:path'
import { consoleScratch, consoleServerIdFile, consoleStorageState } from './console-paths'
import { consoleDirectory, syncUrl } from './playwright.console.config'
import { MSSQL_PORT, SA_PASSWORD } from './test-db'

// The setup of the run through the console (plan 13a, U08): the person who installed the console redeems the first-run invitation with a passkey (a browser-side authenticator holds it), is
// signed in as the administrator, and pairs the Sync server with the code made on it. What the specs need is left in files: the browser's signed-in state, and the id the console gave the server.
test('sign in to the console and pair the Sync server', async ({ page, context }) => {
  const cdp = await context.newCDPSession(page)
  await cdp.send('WebAuthn.enable')
  await cdp.send('WebAuthn.addVirtualAuthenticator', {
    options: { protocol: 'ctap2', transport: 'internal', hasResidentKey: true, hasUserVerification: true, isUserVerified: true, automaticPresenceSimulation: true },
  })

  const invite = fs.readFileSync(path.join(consoleDirectory, 'FIRST-RUN.txt'), 'utf8').split('/invite#')[1].split(/\s/)[0]
  await page.goto(`/invite#${invite}`)
  await page.getByTestId('invite-name-input').fill('Playwright Administrator')
  await page.getByTestId('invite-email-input').fill('admin@example.com')
  await page.getByTestId('invite-enrol-button').click()
  await expect(page.getByTestId('home-page')).toBeVisible()

  const code = fs.readFileSync(path.join(consoleScratch, 'sync-pairing-code.txt'), 'utf8').trim()
  const paired = await page.request.post('/api/instances', { data: { url: syncUrl, code, name: 'Sync under test' } })
  expect(paired.ok(), await paired.text()).toBeTruthy()
  const instances: { id: string; product: string; reachable: boolean }[] = await (await page.request.get('/api/instances')).json()
  const sync = instances.find((i) => i.product === 'dbdatasync')
  expect(sync?.reachable).toBe(true)
  fs.writeFileSync(consoleServerIdFile, sync!.id)

  // The first SQL Server connection a Sync process opens takes about fifteen seconds on some machines (observed on a container host: the connection test reports it as `connectMs`), and the
  // first spec to need one (the golden path's endpoints, which waits fifteen seconds for the database list) would lose that race. A throwaway connection opened here pays it before the specs start.
  const warm = '/relay/' + sync!.id + '/api/connections/warm-up'
  const saved = await page.request.put(warm, { data: { name: 'warm-up', driverType: 'MsSql', host: '127.0.0.1', port: MSSQL_PORT, database: 'master', authMode: 'SqlAuth', userId: 'sa', password: SA_PASSWORD, properties: { TrustServerCertificate: 'true' } } })
  expect(saved.ok(), await saved.text()).toBeTruthy()
  const tested = await page.request.post(`${warm}/test`, { timeout: 60_000 })
  expect(tested.ok(), await tested.text()).toBeTruthy()
  expect((await (await page.request.delete(warm)).status())).toBeLessThan(300)
  await context.storageState({ path: consoleStorageState })
})
