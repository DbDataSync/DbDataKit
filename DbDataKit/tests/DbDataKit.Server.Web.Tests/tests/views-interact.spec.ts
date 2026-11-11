import { test } from '@playwright/test'
import { signInNewAdmin } from './people'
import { interactStory } from './views-interact'

// Plan 32, DBR08: cross-filter (`on_select`), drill and the way back, refresh and kiosk mode on the Views page. The story is in views-interact.ts.
// It signs in a new administrator (an invitation from the command line), so it sorts after `identity-home` and `first-run`, which need a console with no user; it needs no server but the console, so it takes no port.
test('a dashboard cross-filters, drills and returns, refreshes and shows as a kiosk', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Interaction Tester', `views-interact-${Date.now()}@example.com`)
  await interactStory(page)
})
