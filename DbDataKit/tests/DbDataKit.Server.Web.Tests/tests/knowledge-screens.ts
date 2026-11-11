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
