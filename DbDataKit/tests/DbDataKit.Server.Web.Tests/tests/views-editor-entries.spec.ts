import { test } from '@playwright/test'
import { signInNewAdmin } from './people'
import { editorEntriesStory } from './views-editor-entries'

// Plan 32, DBR54: the entry controls (alias, filters, repeat, attribute display, icons, many, link) and the group editor of the widget editor. The story is in views-editor-entries.ts. Same shape as `views-editor-axis`:
// a new administrator, the console only, no port. Written without a browser: not run.
test('the widget editor repeats a measure under an alias with its own filter, shows an attribute as icons in a matrix and edits a field group', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Entries Tester', `views-editor-entries-${Date.now()}@example.com`)
  await editorEntriesStory(page)
})
