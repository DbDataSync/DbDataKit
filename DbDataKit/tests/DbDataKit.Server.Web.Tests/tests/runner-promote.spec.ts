import { test } from '@playwright/test'
import { signInNewAdmin } from './people'
import { promoteStory } from './runner-promote'

// Plan 30, PB06: a package is promoted to another runner from the Packages screen of a runner the console manages, with the runner's relay routes and the console's promotion mocked in the page, so the story needs the signed-in console only and takes no
// port. It signs in a new administrator, so the file sorts after `identity-home` and `first-run`.
test('a version of a package is promoted to another runner from the Packages screen: the choices, the connections of the target channel, what was sent and the target\'s refusal in its words', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Promote Tester', `runner-promote-${Date.now()}@example.com`)
  await promoteStory(page)
})
