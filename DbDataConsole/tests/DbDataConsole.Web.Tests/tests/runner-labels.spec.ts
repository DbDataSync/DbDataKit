import { test } from '@playwright/test'
import { signInNewAdmin } from './people'
import { labelsStory } from './runner-editors'

// Plan 33, UE11: the labels of a runner's instances edited on the Tasks page, with the runner's relay routes mocked in the page, so the story needs the signed-in console only and takes no port (the runner side is DbDataRunner.Tests'
// ScreenRoutesTests, the console side EmbeddedRunnerTests and ConsoleRunnerTests). It signs in a new administrator, so the file sorts after `identity-home` and `first-run`.
test('the labels of an instance are edited as a list and saved as a whole, a task that required one leaves waiting, and the runner\'s refusals are shown in its words', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Labels Tester', `runner-labels-${Date.now()}@example.com`)
  await labelsStory(page)
})
