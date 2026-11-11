import { test } from '@playwright/test'
import { signInNewAdmin } from './people'
import { versionsStory } from './views-versions'

// Plan 33, UE04: the versions of a published dashboard on the Views page (list, serve an earlier one, roll back, remove from the group), with the Gateway's relay mocked in the page (the Gateway side is DbDataGateway.Tests'
// ViewsModuleTests), so the story needs the signed-in console only and takes no port. It signs in a new administrator, so it sorts after `identity-home` and `first-run` (and after `views-published`, whose mock it follows).
test('the versions of a published dashboard are listed, an earlier one is served and drawn, a stale change is refused in the Gateway\'s words, and the document is removed from the group', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Versions Tester', `views-versions-${Date.now()}@example.com`)
  await versionsStory(page)
})
