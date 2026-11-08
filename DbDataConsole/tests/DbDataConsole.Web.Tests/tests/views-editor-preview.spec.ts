import { test } from '@playwright/test'
import { signInNewAdmin } from './people'
import { editorPreviewStory } from './views-editor-preview'

// Plan 32, DBR28 (M2 Edit, live preview on a server): the editor of a report on a Gateway's published documents sends the unsaved draft to the views module's preview route and draws the answer. The Gateway's relay is mocked in the page
// (the module side is DbDataKit.Views.Hosting.Tests' ViewsPreviewTests), so the story needs the signed-in console only and takes no port. It signs in a new administrator, so it sorts after `identity-home` and `first-run`.
test('the report editor previews its unsaved draft through the views module, and says what the module refuses', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Preview Tester', `views-editor-preview-${Date.now()}@example.com`)
  await editorPreviewStory(page)
})
