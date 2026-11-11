import { test } from '@playwright/test'
import { signInNewAdmin } from './people'
import { mockIndex, mockKnowledge, searchStory } from './knowledge-screens'

// Plan 27a KN28 with KN05, KN07 and KN08: the Knowledge page's tab Search and index with the index host mocked in the page (the host side is DbDataKit.Server.Tests' ConsoleKnowledgeIndexTests on SQLite, SQL Server 2025 and PostgreSQL with pgvector, and
// DbDataKit.State.Tests' KnowledgeVectorTests), so the story needs the signed-in console only and takes no port. Its file name sorts after first-run and identity-home: it invites a console administrator as screens-knowledge.spec.ts does.
test('an embedding model is tested and bound, the index is built and its vectors fill, a search by meaning finds what words do not, and a second model builds beside the first', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Index Tester', `index-${Date.now()}@example.com`)
  await mockKnowledge(page)
  await mockIndex(page)
  await searchStory(page)
})
