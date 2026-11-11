import { test } from '@playwright/test'
import { signInNewAdmin } from './people'
import { entityStory } from './store-entity'

// Plan 33, UE18: the Store entity editor in the console (Z06 left its browser test pending): rows created, refused by the Store's rules, edited with their history, refused when stale, deleted, and an instance picked.
// The Store's side is mocked in the page (`store-fake.ts`; the real routes are DbDataStore.Tests'), so the story needs the signed-in console only and takes no port. It signs in a new administrator, so it sorts after `identity-home` and `first-run`.
test('the Store entity editor creates, edits and deletes rows, shows the Store\'s refusals and history, and picks an instance', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Store Entity Tester', `store-entity-${Date.now()}@example.com`)
  await entityStory(page)
})
