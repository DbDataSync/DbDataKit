import { test, type Page } from '@playwright/test'
import { instanceStory, pairAndOpenApps, startGateway, stopGateway } from './gateway-apps-editors'
import { signInNewAdmin } from './people'

// Plan 33, UE01 to UE03: the editors of the Gateway's Apps screen against a real Gateway (the fixture is `gateway-apps-editors.ts`: its own directory and ports 8520 to 8523), through the console's relay, the way
// `gateway-apps.spec.ts` shows the lists. The tests of the file run in order on one signed-in console and one pairing. A mocked Gateway runs the same screens in `gateway-apps-editors-screen.spec.ts`; the roles and the
// store's refusals are DbDataGateway.Tests' AppsScreenTests.
// It invites a console administrator (`user invite`), so it must sort after `identity-home.spec.ts` (and `first-run`): the specs run one at a time in the order of their file names, and `gateway-apps-editors` does.
test.describe.configure({ mode: 'serial' })

let screen: Page

test.beforeAll(async ({ browser, baseURL }) => {
  await startGateway()
  const context = await browser.newContext({ baseURL, ignoreHTTPSErrors: true })
  screen = await context.newPage()
  await signInNewAdmin(screen, context, baseURL!, 'Editors Tester', `gateway-apps-editors-${Date.now()}@example.com`)
  await pairAndOpenApps(screen, 'Apps editors gateway')
})

test.afterAll(async () => { await screen?.context().close(); stopGateway() })

test('UE01: an instance is made in a form, served by the apps host, changed, and keeps its settings when it is pinned', async () => {
  await instanceStory(screen)
})
