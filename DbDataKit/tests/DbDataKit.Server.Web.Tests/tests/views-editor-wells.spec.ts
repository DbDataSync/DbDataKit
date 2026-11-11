import { test } from '@playwright/test'
import { signInNewAdmin } from './people'
import { editorWellsStory } from './views-editor-wells'

// Plan 32, DBR25 (M2 Edit, field wells): the widget editor's wells over the retail catalog, the derived request, the type chooser and what the source's check says. The story is in views-editor-wells.ts.
// Same shape as `views-editor` and `views-editor-layouts`: a new administrator, the console only, no port.
test('the widget editor fills wells from the catalog, shows the derived request and refuses a wrong field', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Wells Tester', `views-editor-wells-${Date.now()}@example.com`)
  await editorWellsStory(page)
})
