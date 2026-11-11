import { test } from '@playwright/test'
import { signInNewAdmin } from './people'
import { secretsStory } from './gateway-secrets'

// Plan 33, UE16: the Gateway's Secrets screen (set, replace, remove; a value is written and never shown) and the references the data source editor shows, with the Gateway's relay mocked in the page (the Gateway side is
// DbDataGateway.Tests' ManagementTests), so the story needs the signed-in console only and takes no port. It invites a console administrator (`user invite`) as `identity-admin.spec.ts` does.
test('a secret is set and never shown again, its reference is written in a data source and tested, replaced and removed', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Secrets Tester', `gateway-secrets-${Date.now()}@example.com`)
  await secretsStory(page)
})
