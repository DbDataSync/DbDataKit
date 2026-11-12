import { test } from '@playwright/test'
import { signInNewAdmin } from './people'
import { downloadsStory } from './views-published'

// Plan 32, DBR17: the console's download buttons over a published dashboard. The story is in views-published.ts (it shares that file's mocked relay). Console only, no port.
test('the Views page downloads the Excel of a layout and a chart\'s picture through the relay', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Downloads Tester', `views-downloads-${Date.now()}@example.com`)
  await downloadsStory(page)
})
