import { test } from '@playwright/test'
import { signInNewAdmin } from './people'
import { editorLayoutsStory } from './views-editor-layouts'

// Plan 32, DBR26 (M2 Edit, layouts): drag and resize on a reflow grid and a canvas, a tile below its minimum or overlapping, a selector and "test at W x H", parameters and refresh. The story is in views-editor-layouts.ts.
// Same shape as `views-editor`: a new administrator, the console only, no port.
test('the editor lays out a dashboard: drag, resize, snap, refuse, mark, select, save', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Layout Tester', `views-editor-layouts-${Date.now()}@example.com`)
  await editorLayoutsStory(page)
})
