import { test } from '@playwright/test'
import { signInNewAdmin } from './people'
import { mockVerified, verifiedStory } from './knowledge-verified-screens'

// Plan 27a KN21: the verified-query screens with the knowledge host mocked in the page (the host side is DbDataKit.Knowledge.Tests' VerifiedQueryTests and InsightTests). The name sorts after first-run and identity-home:
// it invites a console administrator.
test('a verified query is started from a question, a model and a request, and its page says who verified it, what it runs and which questions are embedded', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Verified Tester', `verified-${Date.now()}@example.com`)
  const mock = await mockVerified(page)
  await verifiedStory(page, mock)
})
