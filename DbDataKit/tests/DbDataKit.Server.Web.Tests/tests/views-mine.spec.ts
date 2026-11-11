import { test } from '@playwright/test'
import { signInNewAdmin } from './people'
import { mineStory } from './views-mine'

// Plan 32, DBR12: a person's own dashboards on the Views page: pin from a published dashboard or a query, my dashboards, edit, share, history and restore, promote, and what another person sees.
// The module's routes are a fake of its rules in the page (the real ones are DbDataKit.Views.Hosting.Tests' ViewsUserDocumentsTests, through the real host), so the story needs the signed-in console only and takes no port.
// It signs in a new administrator, so it sorts after `identity-home` and `first-run`; it sorts before `views-published`, with which it shares the mocked Gateway (`mockViews`).
test('the Views page keeps a person\'s own dashboards: pin, edit, share, history and restore, promote, and another person sees only what is shared and their own rows', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Mine Tester', `views-mine-${Date.now()}@example.com`)
  await mineStory(page)
})
