import { test } from '@playwright/test'
import { signInNewAdmin } from './people'
import { publishedStory } from './views-published'

// Plan 32, DBR11b: the Views page lists and opens the dashboards published to a Proxy that runs the views module, through the console's relay, and falls back to the fixtures when there is none.
// The relay and the instance list are mocked in the page (the Proxy side is DbDataProxy.Tests' ViewsModuleTests, through the real host), so the story needs the signed-in console only and takes no port.
// It signs in a new administrator, so it sorts after `identity-home` and `first-run` (and after `views-dashboards`, whose fixtures it relies on).
test('the Views page opens published dashboards from a Proxy through the relay, per person, and falls back to the fixtures', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Published Tester', `views-published-${Date.now()}@example.com`)
  await publishedStory(page)
})
