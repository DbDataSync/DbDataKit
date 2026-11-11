import { test } from '@playwright/test'
import { signInNewAdmin } from './people'
import { dashboardsStory } from './views-dashboards'

// Plan 32, DBR23 and DBR14 (M1 View): the Views page opens the sample dashboard and the sample report from fixtures. The story is in views-dashboards.ts and views-reports.ts (the report half; it prints with the browser's own `page.pdf()`).
// It signs in a new administrator (an invitation from the command line), so it sorts after `identity-home` and `first-run`, which need a console with no user; it needs no server but the console, so it takes no port.
test('the Views page opens a dashboard from fixtures: it fits, switches layout, resolves only what changed, refuses a bad file, scrolls a report with page-break guides', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Dashboards Tester', `views-dashboards-${Date.now()}@example.com`)
  await dashboardsStory(page)
})
