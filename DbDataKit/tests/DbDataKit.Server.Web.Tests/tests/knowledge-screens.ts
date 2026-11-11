import { expect, type Page, type Route } from '@playwright/test'

// Plan 27a KN28, KN36, KN37, KN38: the console's knowledge screens against a fake knowledge host, mocked in the page. The host side (the strict reader, the policy folder's rule, the orphans, the previews) is `DbDataKit.Server.Tests` ConsoleAgentsTests and
// `DbDataKit.Knowledge.Tests` InsightTests on the real code; what these stories show is the screens: the index that says it is not built, an item typed and split, the worklist of a dropped model, and the install project's guardrail.

const json = (route: Route, body: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })
const project = (name: string, exists: boolean, files: { id: string; title: string; role: string; owner: string | null }[], orphans: string[] = []) =>
  ({ name, exists, items: files.length, chunks: files.length, instructions: files.filter((f) => f.role === 'instruction').length, guardrails: files.filter((f) => f.role === 'guardrail').length, references: files.filter((f) => f.role === 'reference').length, failed: [], orphans, files })

export interface KnowledgeMock { saved: { project: string; id: string; text: string }[]; repointed: string[]; retired: string[]; installMade: () => boolean }

export async function mockKnowledge(page: Page): Promise<KnowledgeMock> {
  const saved: KnowledgeMock['saved'] = []
  const repointed: string[] = []
  const retired: string[] = []
  let installMade = false
  let dropped = false
  const items = [{ id: 'revenue.md', title: 'Revenue is net', role: 'instruction', owner: 'finance' }, { id: 'margin.md', title: 'Margin', role: 'instruction', owner: 'finance' }]
  const overview = () => {
    const live = items.filter((i) => !retired.includes(i.id))
    const orphans = dropped ? live.filter((i) => !repointed.includes(i.id)) : []
    return {
      projects: [project('install', installMade, installMade ? [{ id: 'shared/readme-first.md', title: 'Read first', role: 'guardrail', owner: 'administrators' }, ...saved.filter((s) => s.project === 'install').map((s) => ({ id: s.id, title: s.id, role: 'guardrail', owner: 'administrators' }))] : []),
        project('project', true, live, orphans.map((o) => o.id))],
      hasSubjectCatalog: true, subjects: [{ type: 'semantic-model', name: 'Sales', narrowingKind: '', narrowingName: '', status: dropped ? 'gone' : 'exists' }],
      index: { built: false, message: 'No index is built: the knowledge module and its embedding model are not part of this install yet. What is shown is what the files say and what they would be split into.' },
      worklists: orphans.length === 0 ? [] : [{ owner: 'finance', work: orphans.map((o) => ({ kind: 'orphaned', itemId: o.id, title: o.title, message: 'Everything it is about is gone (semantic-model:Sales); it reaches nobody. Point it at another object, or retire it.', subject: 'semantic-model:Sales' })) }],
    }
  }
  await page.route('**/api/agents/definitions', (route) => json(route, [{ name: 'helper', title: 'Helper', description: null, audience: 'back-end', modelSlot: 'default', problems: 0, modelBound: true }]))
  await page.route('**/api/knowledge/overview', (route) => json(route, overview()))
  await page.route('**/api/knowledge/install', (route) => { installMade = true; return json(route, overview()) })
  await page.route('**/api/knowledge/subjects', (route) => { dropped = (route.request().postDataJSON() as { status: string }).status === 'gone'; return json(route, overview()) })
  await page.route('**/api/knowledge/repoint/**', (route) => { repointed.push(decodeURIComponent(new URL(route.request().url()).pathname.split('/').pop()!)); return json(route, overview()) })
  await page.route('**/api/knowledge/items/**', (route) => {
    const request = route.request()
    const [, , , , projectName, ...rest] = new URL(request.url()).pathname.split('/')
    const id = rest.join('/')
    if (request.method() === 'DELETE') { retired.push(id); return route.fulfill({ status: 204 }) }
    if (request.method() === 'PUT') {
      const text = (request.postDataJSON() as { text: string }).text
      if (/^prompting:/m.test(text)) return json(route, { saved: false, diagnostics: [{ code: 'DBD-KNW-003', severity: 'error', file: id, line: 6, column: 1, message: '`prompting` is a setting, not knowledge: it belongs in the install policy (agent-policy/1).', fix: null }] }, 422)
      saved.push({ project: projectName, id, text })
      return json(route, { saved: true, diagnostics: [] })
    }
    return json(route, { project: projectName, id, text: '---\nknowledge: 1\nrole: reference\nowner: finance\nabout:\n  - install: true\n---\n# Guide\n\n## One\n\nFirst section text that is long enough to be its own chunk.\n\n## Two\n\nSecond section text that is long enough to be its own chunk.\n' })
  })
  await page.route('**/api/knowledge/preview/chunks', (route) => {
    const body = route.request().postDataJSON() as { text: string }
    const edited = body.text.includes('CHANGED')
    return json(route, {
      preview: { title: 'Guide', role: 'reference', owner: 'finance', diagnostics: [], changedChunks: edited ? 1 : 0,
        chunks: [{ id: 'g#one', headingPath: ['Guide', 'One'], tokens: 22, startLine: 9, changed: edited, text: 'one' }, { id: 'g#two', headingPath: ['Guide', 'Two'], tokens: 21, startLine: 13, changed: false, text: 'two' }] },
      reaches: [{ agent: 'helper', adds: 40, used: 190, budget: 200, pushesOver: true }],
    })
  })
  await page.route('**/api/knowledge/preview/scope', (route) => json(route, {
    scopes: [],
    preview: {
      instructions: [{ id: 'k1.md', title: 'K1', role: 'instruction', rank: 2, scope: 'a project', owner: 'data' }, { id: 'k3.md', title: 'K3', role: 'instruction', rank: 5, scope: 'a member', owner: 'data' }],
      guardrails: [{ id: 'k2.md', title: 'K2', role: 'guardrail', rank: 3, scope: 'a folder', owner: 'data' }], skipped: [{ id: 'k6.md', title: 'K6', role: 'instruction', reason: 'Not for this audience, agent or channel.' }],
    },
  }))
  await page.route('**/api/knowledge/context', (route) => json(route, {
    agent: 'helper', parts: [], totalTokens: 180, window: 1000, budgetTokens: 200, overBudget: false, basis: 'an estimate', inPlay: ['semantic-model:Sales'],
    shares: [{ layer: 'platform', id: 'platform-rules', version: 'platform-rules/1', tokens: 60, shareOfBudget: 0.3 }, { layer: 'install-policy', id: 'policy/production.md', version: 'abc', tokens: 20, shareOfBudget: 0.1 }],
  }))
  return { saved, repointed, retired, installMade: () => installMade }
}

/** KN28: the index says it is not built; the counts per project; an agent's context lists each part with its share of the budget. */
export async function overviewStory(page: Page) {
  await page.goto('/knowledge')
  await expect(page.getByTestId('knowledge-index-state')).toContainText('No index is built')
  await expect(page.getByTestId('knowledge-project').filter({ hasText: 'Knowledge' }).last()).toHaveAttribute('data-exists', 'true')
  await page.getByTestId('context-agent').selectOption('helper')
  await page.getByTestId('context-in-play').fill('semantic-model:Sales')
  await page.getByTestId('context-show').click()
  await expect(page.getByTestId('context-part')).toHaveCount(2)
  await expect(page.getByTestId('context-part').nth(1)).toContainText('policy/production.md (20 tokens, 10% of the budget)')
}

/** KN37: drop a model, see its items in the owner's worklist, re-point one and retire the other. */
export async function worklistStory(page: Page) {
  await page.goto('/knowledge?tab=worklist')
  await expect(page.getByTestId('worklist-empty')).toBeVisible()
  await page.getByTestId('subject-name').fill('Sales')
  await page.getByTestId('subject-gone').click()
  const list = page.getByTestId('worklist').filter({ hasText: 'finance' })
  await expect(list.getByTestId('work-item')).toHaveCount(2)
  await expect(list.getByTestId('work-item').first()).toContainText('Orphaned')
  const revenue = list.getByTestId('work-item').filter({ hasText: 'revenue.md' })
  await revenue.getByTestId('work-repoint').click()
  await revenue.getByTestId('repoint-target').fill('Revenue')
  await revenue.getByTestId('repoint-save').click()
  await expect(list.getByTestId('work-item')).toHaveCount(1)
  await list.getByTestId('work-retire').click()
  await expect(page.getByTestId('worklist-empty')).toBeVisible()
}

/** KN38: an item as typed is split with its heading paths, an edit marks one chunk changed and the agent it pushes over budget is named; a scope preview lists what would be given, in order, and what is left out and why. */
export async function previewStory(page: Page) {
  await page.goto('/knowledge?tab=items&item=project/guide.md')
  await expect(page.getByTestId('chunk-preview')).toBeVisible()
  await expect(page.getByTestId('chunk')).toHaveCount(2)
  await expect(page.getByTestId('chunk').first()).toContainText('Guide > One')
  await expect(page.getByTestId('chunk-changed')).toHaveCount(0)
  await expect(page.getByTestId('reach')).toHaveAttribute('data-over', 'true')

  await page.getByTestId('scope-in-play').fill('build-project:sales\nsemantic-model:Sales/measure:revenue')
  await page.getByTestId('scope-show').click()
  await expect(page.getByTestId('scope-instruction')).toHaveText([/k1\.md/, /k3\.md/])
  await expect(page.getByTestId('scope-guardrail')).toContainText('k2.md')
  await expect(page.getByTestId('scope-skipped')).toContainText('Not for this audience')
}

/** KN36: the install project is made from the template, a guardrail is added as the administrators' own and reaches the agent. */
export async function installStory(page: Page, mock: KnowledgeMock) {
  await page.goto('/knowledge?tab=install')
  await expect(page.getByTestId('install-missing')).toBeVisible()
  await page.getByTestId('install-create').click()
  await expect(page.getByTestId('install-files')).toBeVisible()
  await page.getByTestId('policy-item-title').fill('No deletes in production')
  await page.getByTestId('policy-item-body').fill('Never delete a production table.')
  await page.getByTestId('policy-item-save').click()
  await expect(page.getByTestId('policy-item-reach')).toContainText('Reaches helper')
  expect(mock.saved[0].id).toBe('policy/no-deletes-in-production.md')
  expect(mock.saved[0].text).toContain('owner: administrators')
  expect(mock.saved[0].text).toContain('role: guardrail')
  await expect(page.getByTestId('install-policy-count')).toContainText('1 guardrail')

  // the item opens in the editor (the refusal of a settings key is the host's, tested on the real reader in ConsoleAgentsTests; typing into the code editor is left to a hand check)
  await page.goto('/knowledge?tab=items&item=install/policy/no-deletes-in-production.md')
  await expect(page.getByTestId('item-editor')).toBeVisible()
}

// ---- KN28 with KN05, KN07, KN08: the index, the embedding model and the search, against a fake index host ----

interface FakeIndex { opened: boolean; model: string | null; building: string | null; polls: number; failing: boolean }

const embedder = (name: string, ids: string[]) => ({ name, file: `connections/${name}.yml`, yaml: '', kind: 'ollama', endpoint: null, cloud: false, purpose: 'embeddings', keyReference: null, keyAvailable: true, keyProblem: null, models: ids.map((id) => ({ id, window: 8192, maxOutput: 0, priceInput: null, priceOutput: null })), diagnostics: [] })

/** The index host mocked in the page: nothing is built until the button is pressed, the vectors fill over a few polls, a second model builds beside the first, and a model that refuses a chunk lists the failure. The host side is `ConsoleKnowledgeIndexTests` and `KnowledgeVectorTests`, on the real engines. */
export async function mockIndex(page: Page) {
  const fake: FakeIndex = { opened: false, model: null, building: null, polls: 0, failing: false }
  const view = () => {
    // the slot names the newest model at once; the index that answers is the old one until the new one is complete (the third poll)
    const bound = fake.building ?? fake.model
    const switched = fake.building !== null && fake.polls >= 2
    const ready = switched ? fake.building : fake.model
    const building = fake.building !== null && !switched ? fake.building : null
    return {
      channel: 'main', opened: fake.opened, running: fake.opened && fake.polls < 2, items: fake.opened ? 2 : 0, chunks: fake.opened ? 4 : 0, wordsMode: 'tsvector', vectorsAvailable: true, vectorsProblem: null,
      embedding: bound ? { provider: 'ollama-local', model: bound.replace('ollama-local/', ''), modelId: bound, usable: true, problem: null } : { provider: null, model: null, modelId: null, usable: false, problem: null },
      vectors: fake.opened && bound ? { readyModel: ready, readyDimension: 768, buildingModel: building, buildingDimension: building ? 1024 : null, chunks: 4, embedded: 4, buildingEmbedded: building ? [1, 3][fake.polls] ?? 3 : 0, failed: fake.failing && switched ? 1 : 0 } : null,
      failures: fake.failing && switched ? [{ itemId: 'project:bad.md', seq: 0, model: bound, attempts: 3, message: 'ollama-local refused the request (400). It said: the input is too long for this model', failedAt: '2026-11-11T10:00:00Z' }] : [],
      lastRun: null, message: fake.opened ? '2 items in 4 chunks.' : 'The index is not built. Build it to search the knowledge by its words, and by its meaning where the engine can hold vectors and an embedding model is bound.',
    }
  }
  await page.route('**/api/agents/models', (route) => json(route, { providers: [embedder('ollama-local', ['nomic-embed-text', 'bge-m3'])], slotsYaml: '', slotDiagnostics: [], bindings: [], agentSlots: [], secretPrefix: '' }))
  await page.route('**/api/agents/models/providers/ollama-local/test', (route) => json(route, { ok: true, message: `ollama-local embedded a sample with ${(route.request().postDataJSON() as { model: string }).model}: 768 dimensions.`, answer: '768 dimensions', inputTokens: 9, outputTokens: null, milliseconds: 12, temporary: false, dimensions: 768 }))
  await page.route('**/api/knowledge/index', (route) => { if (fake.opened && fake.polls < 2 && route.request().method() === 'GET') fake.polls++; return json(route, view()) })
  await page.route('**/api/knowledge/index/embedding', (route) => {
    const body = route.request().postDataJSON() as { provider: string | null; model: string | null }
    if (body.provider === null) { fake.model = null; fake.building = null } else {
      const id = `${body.provider}/${body.model}`
      if (fake.model === null) fake.model = id; else { fake.building = id; fake.polls = 0; fake.failing = id.endsWith('bge-m3') }
    }
    return json(route, view())
  })
  await page.route('**/api/knowledge/index/build', (route) => { fake.opened = true; fake.polls = 0; return json(route, { sync: { added: 2, unchanged: 0, removed: 0 }, view: view() }) })
  await page.route('**/api/knowledge/index/retry', (route) => json(route, view()))
  await page.route('**/api/knowledge/search', (route) => {
    const body = route.request().postDataJSON() as { question: string; mode: string }
    const hit = { itemId: 'project:revenue.md', title: 'Revenue', headingPath: ['Revenue'], excerpt: 'Revenue means net revenue: gross sales minus returns minus discounts.', source: 'project', version: 'abc123abc123', score: 0.0328, chunks: 1 }
    return json(route, { hits: body.mode === 'words' && body.question.includes('turnover') ? [] : [hit], used: body.mode, note: null })
  })
  return fake
}

/** KN28 with KN05, KN07, KN08: nothing is built and no search is possible; an embedding model is tested (the dimensions are reported), bound and the index built; the vectors fill and the modes open; a search by words finds nothing for a word the text does not have, a search by both finds it by meaning; a second model builds beside the first and a chunk it refuses is listed. */
export async function searchStory(page: Page) {
  await page.goto('/knowledge?tab=search')
  await expect(page.getByTestId('index-message')).toContainText('not built')
  await expect(page.getByTestId('search-run')).toBeDisabled()
  await expect(page.getByTestId('embedding-current')).toContainText('No embedding model is bound')

  await page.getByTestId('embedding-provider').selectOption('ollama-local')
  await page.getByTestId('embedding-model').selectOption('nomic-embed-text')
  await page.getByTestId('embedding-test').click()
  await expect(page.getByTestId('embedding-test-result')).toHaveAttribute('data-dimensions', '768')
  await page.getByTestId('embedding-bind').click()
  await expect(page.getByTestId('embedding-current')).toContainText('ollama-local/nomic-embed-text')

  await page.getByTestId('index-build').click()
  await expect(page.getByTestId('index-counts')).toContainText('2 items in 4 chunks')
  await expect(page.getByTestId('index-modes')).toContainText('Words, vectors, or both together', { timeout: 20000 })
  await expect(page.getByTestId('index-progress')).toContainText('4 of 4 chunks')

  await page.getByTestId('search-question').fill('what is our turnover')
  await page.getByTestId('search-mode-words').check()
  await page.getByTestId('search-run').click()
  await expect(page.getByTestId('search-empty')).toBeVisible()
  await page.getByTestId('search-mode-both').check()
  await page.getByTestId('search-run').click()
  await expect(page.getByTestId('search-hit')).toHaveAttribute('data-item', 'project:revenue.md')
  await expect(page.getByTestId('hit-excerpt')).toContainText('net revenue')
  await expect(page.getByTestId('search-used')).toContainText('Searched by both')

  await page.getByTestId('embedding-model').selectOption('bge-m3')
  await page.getByTestId('embedding-bind').click()
  await expect(page.getByTestId('index-progress')).toHaveAttribute('data-building', 'ollama-local/bge-m3')
  await expect(page.getByTestId('index-progress')).toContainText('keeps answering until the new one is complete')
  await expect(page.getByTestId('index-failure')).toHaveAttribute('data-item', 'project:bad.md', { timeout: 20000 })
  await expect(page.getByTestId('index-failures')).toContainText('too long')
  await page.getByTestId('index-retry').click()
}
