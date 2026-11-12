import { test } from '@playwright/test'
import { signInNewAdmin } from './people'
import { trustStory } from './gateway-trust'

// Plan 30 PB03: the Gateway's Publishers screen (keys per kind, add, revoke, the signature requirement, the test group), with the Gateway's relay mocked in the page (the Gateway side is State.Api.Tests'
// DefinitionsApiTests), so the story needs the signed-in console only and takes no port. It invites a console administrator (`user invite`) as `gateway-secrets.spec.ts` does.
test('publisher keys are added, rotated and revoked per kind, and signatures are required or optional per kind and for a test group', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Trust Tester', `gateway-trust-${Date.now()}@example.com`)
  await trustStory(page)
})
