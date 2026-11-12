import { test } from '@playwright/test'
import { signInNewAdmin } from './people'
import { actionsStory } from './views-actions'

// Plan 32, DBR53: actions from a visual, on the fixture source's sample (the console only, no port). The story is in views-actions.ts.
test('a click on a status icon opens the shell\'s popup in the declaration\'s words and reports what the Store answered', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Actions Tester', `views-actions-${Date.now()}@example.com`)
  await actionsStory(page)
})
