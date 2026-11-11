import { test } from '@playwright/test'
import { signInNewAdmin } from './people'
import { embedStory } from './views-embed'

// Plan 32, DBR19: the embed widget in the editor. The story is in views-embed.ts. Same shape as `views-editor-query`: a new administrator, the console only, no port. Written without a browser: not run.
test('the editor adds an embedded report, shows it as a link and refuses an address that is not https', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Embed Tester', `views-embed-${Date.now()}@example.com`)
  await embedStory(page)
})
