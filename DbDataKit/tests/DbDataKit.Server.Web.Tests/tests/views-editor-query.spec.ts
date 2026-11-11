import { test } from '@playwright/test'
import { signInNewAdmin } from './people'
import { editorQueryStory } from './views-editor-query'

// Plan 32, DBR43: the Filter and Sort sections of the widget editor. The story is in views-editor-query.ts. Same shape as `views-editor-wells`: a new administrator, the console only, no port.
// Written without a browser: not run.
test('the widget editor adds a filter and a sort to a chart and refuses an operator the type does not take', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Query Tester', `views-editor-query-${Date.now()}@example.com`)
  await editorQueryStory(page)
})
