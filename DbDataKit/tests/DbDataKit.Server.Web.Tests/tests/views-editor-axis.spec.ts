import { test } from '@playwright/test'
import { signInNewAdmin } from './people'
import { editorAxisStory } from './views-editor-axis'
import { editorColumnStory } from './views-editor-column'

// Plan 32, DBR42 and DBR44: the nested axis and the level and column controls of the widget editor. The stories are in views-editor-axis.ts and views-editor-column.ts. Same shape as `views-editor-query`: a new
// administrator, the console only, no port. Written without a browser: not run.
test('the widget editor builds a nested axis, sets a format and a sort per level, refuses a fourth level and a column beside it', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Axis Tester', `views-editor-axis-${Date.now()}@example.com`)
  await editorAxisStory(page)
})

test('the widget editor formats a table column as currency with a sample, aligns and sizes it', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Column Tester', `views-editor-column-${Date.now()}@example.com`)
  await editorColumnStory(page)
})
