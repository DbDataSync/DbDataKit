import { test } from '@playwright/test'
import { signInNewAdmin } from './people'
import { customStory } from './views-custom'

// Plan 32, DBR30: the custom widget in the editor. The story is in views-custom.ts. Same shape as `views-embed`: a new administrator, the console only, no port.
test('the editor names no placeholder for a custom widget and shows the component a document names', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Custom Tester', `views-custom-${Date.now()}@example.com`)
  await customStory(page)
})
