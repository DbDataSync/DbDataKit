import { test } from '@playwright/test'
import { signInNewAdmin } from './people'
import { publishFilesStory } from './runner-editors'

// Plan 33, UE10: a package is published to a runner from a folder chosen in the browser, with the runner's relay routes mocked in the page, so the story needs the signed-in console only and takes no port (the runner side is
// DbDataRunner.Tests' PublishApiTests, the console side EmbeddedRunnerTests and ConsoleRunnerTests). It signs in a new administrator, so the file sorts after `identity-home` and `first-run`.
test('a package is published from a folder: the files are read and hashed in the browser, only the missing ones are sent, a change sends one file, and the runner\'s refusals are shown in its words', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Publish Files Tester', `runner-publish-files-${Date.now()}@example.com`)
  await publishFilesStory(page)
})
