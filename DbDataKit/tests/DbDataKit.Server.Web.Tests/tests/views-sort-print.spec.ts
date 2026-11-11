import { test } from '@playwright/test'
import { signInNewAdmin } from './people'
import { clickSortStory, printPreviewStory } from './views-sort-print'

// Plan 32, DBR45 (click-to-sort) and DBR27 (the print preview's client half). The stories are in views-sort-print.ts. Same shape as `views-interact`: a new administrator, the console only, no port.
// Written without a browser: not run.
test('a viewer sorts a table by its header, and the sort is not stored', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Sort Tester', `views-sort-${Date.now()}@example.com`)
  await clickSortStory(page)
})

test('the report preview offers a print preview only where the host can make a PDF', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Print Tester', `views-print-${Date.now()}@example.com`)
  await printPreviewStory(page)
})
