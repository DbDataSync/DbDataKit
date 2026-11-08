import { test } from '@playwright/test'
import { signInNewAdmin } from './people'
import { editorStory } from './views-editor'

// Plan 32, DBR24 (M2 Edit, the editor's core): build a dashboard in the Views page's Edit mode, break it, save, reload, conflict, YAML round trip, duplicate, delete. The story is in views-editor.ts.
// It signs in a new administrator like `views-dashboards`, needs only the console (the documents are the fixture source, kept in the browser's storage), so it takes no port.
test('the Views page edits a dashboard: build, break, save, reload, conflict, YAML, duplicate, delete', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Editor Tester', `views-editor-${Date.now()}@example.com`)
  await editorStory(page, context)
})
