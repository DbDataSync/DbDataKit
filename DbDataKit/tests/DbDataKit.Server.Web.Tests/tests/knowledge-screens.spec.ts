import { test } from '@playwright/test'
import { signInNewAdmin } from './people'
import { installStory, mockKnowledge, overviewStory, previewStory, worklistStory } from './knowledge-screens'

// Plan 27a KN28, KN36, KN37, KN38: the console's knowledge screens with the knowledge host mocked in the page (the host side is DbDataKit.Server.Tests' ConsoleAgentsTests and DbDataKit.Knowledge.Tests' InsightTests, on the real reader), so the story
// needs the signed-in console only and takes no port. It invites a console administrator (`user invite`) as `identity-admin.spec.ts` does.
test('the index says it is not built, a dropped model puts its items in the owner\'s worklist, an item is previewed chunk by chunk, and the install project takes a guardrail', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Knowledge Tester', `knowledge-${Date.now()}@example.com`)
  const mock = await mockKnowledge(page)
  await overviewStory(page)
  await worklistStory(page)
  await previewStory(page)
  await installStory(page, mock)
})
