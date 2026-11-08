import { test } from '@playwright/test'
import { signInNewAdmin } from './people'
import { modelsStory } from './store-models'

// Plan 33, UE13a: the Store's model editor, in the console. The Store's side is mocked in the page (`store-fake.ts`; the real routes are DbDataStore.Tests'
// StoreModelsTests and StoreDataTests), so the story needs the signed-in console only and takes no port. It signs in a new administrator, so it sorts after `identity-home` and `first-run`.
test('the Store screens make a model, show the Store\'s diagnostics, save, change and remove it', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Store Models Tester', `store-models-${Date.now()}@example.com`)
  await modelsStory(page)
})
