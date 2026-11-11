import { test } from '@playwright/test'
import { signInNewAdmin } from './people'
import { settingsStory } from './gateway-settings'

// Plan 33, UE17: the Gateway's server settings screen (server.yaml as a form and as text, checked as it changes, saved as a commit, the restart said) with the Gateway's relay mocked in the page (the Gateway side is
// DbDataGateway.Tests' ManagementTests), so the story needs the signed-in console only and takes no port. It invites a console administrator (`user invite`) as `identity-admin.spec.ts` does.
test('server.yaml is edited in a form and in text, checked by the Gateway, saved with the restart said, and a file changed by someone else is refused', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Settings Tester', `gateway-settings-${Date.now()}@example.com`)
  await settingsStory(page)
})
