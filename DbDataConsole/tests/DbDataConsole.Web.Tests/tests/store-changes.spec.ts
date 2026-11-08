import { test } from '@playwright/test'
import { signInNewAdmin } from './people'
import { changesStory } from './store-changes'

// Plan 33, UE14: the review of the Store's change requests: a write that waits, read against the row, approved (applied) or rejected with a reason, an agent's request refused on approval, and the role that owner approval needs.
// The Store's side is mocked in the page (`store-fake.ts`; the real routes are DbDataStore.Tests'), so the story needs the signed-in console only and takes no port. It signs in a new administrator, so it sorts after `identity-home` and `first-run`.
test('the Store screens list change requests, show the proposed rows, approve and reject them, and say who may decide', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Store Changes Tester', `store-changes-${Date.now()}@example.com`)
  await changesStory(page)
})
