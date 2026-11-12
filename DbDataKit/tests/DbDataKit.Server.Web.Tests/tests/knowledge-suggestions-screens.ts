import { expect, type Page, type Route } from '@playwright/test'

// Plan 27a KN19 and KN23: the console's suggestions against a fake knowledge host, mocked in the page. The host side (the strict reader, who sees which suggestion, the withdrawal, the moved base, the preview of a verified query)
// is `DbDataKit.Knowledge.Cli.Tests` SuggestionTests and `DbDataKit.Server.Tests` ConsoleKnowledgeSuggestionsTests on the real code; what this story shows is the screen: a change typed on an item page and suggested with a reason,
// the list with where each one stands (an agent's marked, a moved item said), the proposed text and the result a verified query showed, and the withdrawal of one's own.

const json = (route: Route, body: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })
const ITEM = '---\nknowledge: 1\ntitle: Revenue\nrole: reference\nowner: finance\nabout:\n  - install: true\n---\n# Revenue\n\nRevenue is net revenue.\n'

export interface SuggestionsMock { sent: { item: string; isNew: boolean; text: string; reason: string }[]; withdrawn: string[] }

export async function mockSuggestions(page: Page, me: string): Promise<SuggestionsMock> {
  const sent: SuggestionsMock['sent'] = []
  const withdrawn: string[] = []
  const row = (id: string, over: Record<string, unknown> = {}) => ({
    id, project: 'project', item: 'project:revenue.md', kind: 'change', title: 'Revenue', state: withdrawn.includes(id) ? 'withdrawn' : 'open', reason: 'returns too', suggestedBy: me, via: 'person', owner: 'finance', baseVersion: 'h', moved: false,
    createdAt: '2026-11-12T09:00:00Z', decidedBy: null, note: null, proposed: null, preview: null, ...over,
  })
  const all = () => [
    ...sent.map((s, i) => row(`mine${i}`)),
    row('agent1', { kind: 'verified-query', item: 'queries/revenue-by-region.md', title: 'What was revenue by region?', via: 'agent:sales-helper', suggestedBy: 'someone-else', moved: true,
      proposed: 'verified query text', preview: { model: 'Sales', model_version: '7', parameters: { quarter: 'FY27-Q1' }, columns: [{ name: 'region', type: 'string' }, { name: 'revenue', type: 'double' }], rows: [['EMEA', '120.5'], ['AMER', null]], row_count: 3, truncated: false } }),
  ]
  await page.route('**/api/agents/definitions', (route) => json(route, []))
  await page.route('**/api/knowledge/overview', (route) => json(route, {
    projects: [
      { name: 'install', exists: false, items: 0, chunks: 0, instructions: 0, guardrails: 0, references: 0, failed: [], orphans: [], files: [] },
      { name: 'project', exists: true, items: 1, chunks: 1, instructions: 0, guardrails: 0, references: 1, failed: [], orphans: [], files: [{ id: 'revenue.md', title: 'Revenue', role: 'reference', owner: 'finance' }] },
    ],
    hasSubjectCatalog: true, subjects: [], index: { built: true, message: 'The index is built.' }, worklists: [],
  }))
  await page.route('**/api/knowledge/items/**', (route) => json(route, { project: 'project', id: 'revenue.md', text: ITEM }))
  await page.route('**/api/knowledge/preview/chunks', (route) => json(route, { preview: { title: 'Revenue', role: 'reference', owner: 'finance', diagnostics: [], changedChunks: 0, chunks: [] }, reaches: [] }))
  await page.route('**/api/knowledge/suggestions/*/withdraw', (route) => { withdrawn.push(new URL(route.request().url()).pathname.split('/').at(-2)!.replace('mine', 'mine')); return json(route, { withdrawn: 'x' }) })
  await page.route('**/api/knowledge/suggestions/*', (route) => {
    const id = new URL(route.request().url()).pathname.split('/').pop()!
    return json(route, all().find((r) => r.id === id) ?? {}, all().some((r) => r.id === id) ? 200 : 404)
  })
  await page.route('**/api/knowledge/suggestions', (route) => {
    if (route.request().method() === 'POST') {
      const body = route.request().postDataJSON() as SuggestionsMock['sent'][number]
      sent.push(body)
      return json(route, row(`mine${sent.length - 1}`, { proposed: body.text }))
    }
    return json(route, { built: true, suggestions: all() })
  })
  return { sent, withdrawn }
}

export async function suggestionsStory(page: Page, mock: SuggestionsMock) {
  // an item page: the text is typed by anyone; the suggest button waits for a change and a reason
  await page.goto('/knowledge?tab=items&item=project/revenue.md')
  const form = page.getByTestId('suggest-change')
  await expect(form).toBeVisible()
  await expect(page.getByTestId('suggest-send')).toBeDisabled()
  await page.getByTestId('item-text').click()
  await page.keyboard.press('Control+End')
  await page.keyboard.insertText(' After returns.')   // inserted, not typed: typing loses keys while the editor redraws
  await page.getByTestId('suggest-reason').fill('Returns are netted out too')
  await expect(page.getByTestId('suggest-send')).toBeEnabled()
  await page.getByTestId('suggest-send').click()
  await expect(page.getByTestId('suggest-made')).toContainText('waiting for the owner')
  expect(mock.sent).toHaveLength(1)
  expect(mock.sent[0]).toMatchObject({ item: 'project:revenue.md', isNew: false, reason: 'Returns are netted out too' })
  expect(mock.sent[0].text).toContain('After returns.')

  // the list: mine, and one an agent made for an item I own whose item has since changed
  await page.getByTestId('knowledge-tab-suggestions').click()
  const rows = page.getByTestId('suggestion')
  await expect(rows).toHaveCount(2)
  await expect(rows.nth(1)).toHaveAttribute('data-moved', 'true')
  await expect(rows.nth(1)).toContainText('through the agent sales-helper')
  await expect(rows.nth(1).getByTestId('suggestion-withdraw')).toHaveCount(0)   // not mine to withdraw

  // an agent's verified query: what it returned when it was verified, and the text proposed
  await rows.nth(1).getByTestId('suggestion-open').click()
  await expect(page.getByTestId('suggestion-moved')).toBeVisible()
  await expect(page.getByTestId('suggestion-preview')).toContainText('3 rows from Sales at version 7, with quarter = FY27-Q1.')
  await expect(page.getByTestId('suggestion-preview')).toContainText('EMEA')
  await expect(page.getByTestId('suggestion-proposed')).toContainText('verified query text')

  // mine is withdrawn by me
  await rows.nth(0).getByTestId('suggestion-withdraw').click()
  await expect(rows.nth(0)).toHaveAttribute('data-state', 'withdrawn')
  expect(mock.withdrawn).toHaveLength(1)
}
