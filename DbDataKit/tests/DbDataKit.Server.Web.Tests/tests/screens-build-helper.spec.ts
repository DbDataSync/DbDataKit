import { test } from '@playwright/test'
import { signInNewAdmin } from './people'
import { buildHelperStory } from './build-helper-screens'

// Plan 27a AG14, AG15: the build-helper agent's screens with the host mocked in the page (the host is DbDataKit.Server.Tests' ConsoleBuildAgentTests). The name sorts after `first-run` and `identity-home` (see
// `screens-agents.spec.ts`).
test('the build helper is made from a server and a conversation starts on a project in a workspace', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Build Helper Tester', `build-helper-${Date.now()}@example.com`)
  await buildHelperStory(page)
})
