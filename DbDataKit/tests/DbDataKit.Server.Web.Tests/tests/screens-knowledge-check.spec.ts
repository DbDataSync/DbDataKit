import { test } from '@playwright/test'
import { signInNewAdmin } from './people'
import { checkStory, mockCheck } from './knowledge-check-screens'

// Plan 27a KN10 and KN17: the Knowledge page's tab Check with the host mocked in the page (the host side is DbDataKit.Server.Tests' ConsoleKnowledgeCheckTests and DbDataKit.Knowledge.Cli.Tests), so the story needs the signed-in console only and takes no port. Its file name sorts after
// first-run and identity-home: it invites a console administrator.
test('a project that would push an agent over its budget is refused with file, line and the agent, and passes once the instruction is fixed', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Check Tester', `check-${Date.now()}@example.com`)
  const mock = await mockCheck(page)
  await checkStory(page, mock)
})
