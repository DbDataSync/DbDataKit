import { test } from '@playwright/test'
import { signInNewAdmin } from './people'
import { mockSuggestions, suggestionsStory } from './knowledge-suggestions-screens'

// Plan 27a KN19 and KN23: the suggestions screens with the knowledge host mocked in the page (the host side is DbDataKit.Knowledge.Cli.Tests' SuggestionTests and DbDataKit.Server.Tests' ConsoleKnowledgeSuggestionsTests).
// The name sorts after first-run and identity-home: it invites a console administrator.
test('a change typed on an item page is suggested with a reason; the list says whose it is, which an agent made and which item moved on; one\'s own is withdrawn', async ({ page, context, baseURL }) => {
  const email = `suggest-${Date.now()}@example.com`
  await signInNewAdmin(page, context, baseURL!, 'Suggest Tester', email)
  const status = await (await page.request.get('/api/auth/status')).json() as { userId: string }
  const mock = await mockSuggestions(page, status.userId)
  await suggestionsStory(page, mock)
})
