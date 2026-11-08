import { test } from '@playwright/test'
import { signInNewAdmin } from './people'
import { attributesStory } from './gateway-identity-attributes'

// Plan 33, UE15: the attribute mappings of the identity home (a directory group gives its members a value) on the Gateway's Identity home screen, with the Gateway's relay mocked in the page (the Gateway side is DbDataGateway.Tests'
// IdentityModuleTests), so the story needs the signed-in console only and takes no port. It invites a console administrator (`user invite`) as `identity-admin.spec.ts` does.
test('a group is mapped to an attribute value, the form says what is missing and what the home would refuse, a mapping is listed and removed', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Attributes Tester', `gateway-identity-attributes-${Date.now()}@example.com`)
  await attributesStory(page)
})
