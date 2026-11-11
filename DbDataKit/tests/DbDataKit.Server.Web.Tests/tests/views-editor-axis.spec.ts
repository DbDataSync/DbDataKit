import { test } from '@playwright/test'
import { signInNewAdmin } from './people'
import { editorAxisStory } from './views-editor-axis'

// Plan 32, DBR42 and DBR44: the nested axis and the level controls of the widget editor. The story is in views-editor-axis.ts. Same shape as `views-editor-query`: a new administrator, the console only, no port.
// Written without a browser: not run.
test('the widget editor builds a nested axis, refuses a fourth level and a column beside it', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Axis Tester', `views-editor-axis-${Date.now()}@example.com`)
  await editorAxisStory(page)
})
