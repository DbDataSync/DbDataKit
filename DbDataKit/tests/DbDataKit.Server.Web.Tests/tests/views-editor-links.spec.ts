import { test } from '@playwright/test'
import { signInNewAdmin } from './people'
import { editorLinksStory } from './views-editor-links'

// Plan 32, DBR38 and DBR49: the link editor and the format of a chart's measure and colour channels. The story is in views-editor-links.ts. Same shape as `views-editor-axis`: a new administrator, the console only,
// no port. Written without a browser: not run (the console-only recipe of ci-lessons runs it).
test('the widget editor adds a form link shown as a dialog, locks a prefilled field, follows it on a click and formats a chart channel', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Links Tester', `views-editor-links-${Date.now()}@example.com`)
  await editorLinksStory(page)
})
