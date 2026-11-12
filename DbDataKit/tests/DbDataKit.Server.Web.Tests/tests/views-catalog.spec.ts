import { test } from '@playwright/test'
import { signInNewAdmin } from './people'
import { catalogStory } from './views-catalog'

// Plan 32, RC05: the report browser over the sample catalogs. The story is in views-catalog.ts. A new administrator, the console only, no port. Written without a console host to run it against: not run.
test('the Catalog tab browses two catalogs, opens an item with parameters in a frame or as a link, and exports in a format it offers', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Catalog Tester', `views-catalog-${Date.now()}@example.com`)
  await catalogStory(page)
})
