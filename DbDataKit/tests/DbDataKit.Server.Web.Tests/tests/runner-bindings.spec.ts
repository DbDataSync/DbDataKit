import { test } from '@playwright/test'
import { signInNewAdmin } from './people'
import { bindingsStory } from './runner-editors'

// Plan 33, UE08: the connections of a runner's package per channel and the require-signatures switch, with the runner's relay routes mocked in the page, so the story needs the signed-in console only and takes no port (the runner side is
// DbDataRunner.Tests' ScreenRoutesTests and PublishApiTests, the console side ConsoleRunnerTests and EmbeddedRunnerTests). It signs in a new administrator, so the file sorts after `identity-home` and `first-run`.
test('the slots of a package are bound per channel, a change made meanwhile is refused in the runner\'s words, and require signatures is switched with its warning', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Bindings Tester', `runner-bindings-${Date.now()}@example.com`)
  await bindingsStory(page)
})
