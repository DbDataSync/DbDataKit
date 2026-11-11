import { test } from '@playwright/test'
import { signInNewAdmin } from './people'
import { chatStory, definitionStory, inboxStory, mockAgentHost, policyStory } from './agents-screens'

// Plan 27a AG15, AG17, AG29 to AG32: the console's agent screens with the agent host mocked in the page (the host itself is DbDataKit.Server.Tests' ConsoleAgentsTests, on the real host with a scripted model), so the story needs the signed-in console only and takes
// no port. It invites a console administrator (`user invite`) as `identity-admin.spec.ts` does, so the file sorts after `first-run` and `identity-home` (the name is `screens-agents`, not `agents-screens`: that sorted before them and used the first-run invitation up).
test('a step that asks waits for the person and is answered, the inbox and the record show outside agents, a definition says what each tool asks, and a loosening of the policy is confirmed', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Agent Tester', `agents-${Date.now()}@example.com`)
  const mock = await mockAgentHost(page)
  await chatStory(page, mock)
  await inboxStory(page, mock)
  await definitionStory(page)
  await policyStory(page, mock)
})
