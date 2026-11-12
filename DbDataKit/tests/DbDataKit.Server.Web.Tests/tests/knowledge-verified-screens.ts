import { expect, type Page, type Route } from '@playwright/test'

// Plan 27a KN21: the console's verified-query screens against a fake knowledge host, mocked in the page. The host side (the strict reader of the kind, the request being the truth, the preview) is
// `DbDataKit.Knowledge.Tests` VerifiedQueryTests and InsightTests on the real code; what this story shows is the screen: a person starts a verified query from a question, a model and a request, and the item
// page shows who verified it, what it runs and the questions that are embedded.

const json = (route: Route, body: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })

export interface VerifiedMock { saved: { project: string; id: string; text: string }[] }

export async function mockVerified(page: Page): Promise<VerifiedMock> {
  const saved: VerifiedMock['saved'] = []
  const overview = () => ({
    projects: [
      { name: 'install', exists: false, items: 0, chunks: 0, instructions: 0, guardrails: 0, references: 0, failed: [], orphans: [], files: [] },
      { name: 'project', exists: true, items: saved.length, chunks: saved.length, instructions: 0, guardrails: 0, references: saved.length, failed: [], orphans: [], files: saved.map((s) => ({ id: s.id, title: 'A verified query', role: 'reference', owner: null })) },
    ],
    hasSubjectCatalog: true, subjects: [{ type: 'semantic-model', name: 'Sales', narrowingKind: '', narrowingName: '', status: 'exists' }],
    index: { built: false, message: 'No index is built.' }, worklists: [],
  })
  await page.route('**/api/agents/definitions', (route) => json(route, []))
  await page.route('**/api/knowledge/overview', (route) => json(route, overview()))
  await page.route('**/api/knowledge/items/**', (route) => {
    const request = route.request()
    const [, , , , projectName, ...rest] = new URL(request.url()).pathname.split('/')
    const id = rest.join('/')
    if (request.method() === 'PUT') {
      saved.push({ project: projectName, id, text: (request.postDataJSON() as { text: string }).text })
      return json(route, { saved: true, diagnostics: [] })
    }
    return json(route, { project: projectName, id, text: saved.find((s) => s.id === id)?.text ?? '' })
  })
  await page.route('**/api/knowledge/preview/chunks', (route) => json(route, {
    preview: {
      title: 'What was revenue by region last quarter?', role: 'reference', owner: null, diagnostics: [], changedChunks: 0,
      chunks: [{ id: 'q#0', headingPath: [], tokens: 12, startLine: 12, changed: false, text: '- What was revenue by region last quarter?' }],
      verified: { model: 'Sales', questions: ['What was revenue by region last quarter?'], request: '{\n  "kind": "query"\n}', semanticSql: 'SELECT region, revenue FROM Sales', savedQuery: null, savedQueryVersion: null, verifiedBy: 'Verified Tester', verifiedAt: '2026-11-12', modelVersion: '1' },
    },
    reaches: [],
  }))
  return { saved }
}

export async function verifiedStory(page: Page, mock: VerifiedMock) {
  await page.goto('/knowledge?tab=items')
  const form = page.getByTestId('new-verified-query')
  await expect(form).toBeVisible()
  await expect(page.getByTestId('vq-make')).toBeDisabled()
  await form.getByTestId('vq-question').fill('What was revenue by region last quarter?')
  await form.getByTestId('vq-model').fill('Sales')
  await form.getByTestId('vq-request').fill('{"kind":"query","query":{"measures":["revenue"],"limit":10}}')
  await page.getByTestId('vq-make').click()

  await expect(page.getByTestId('item-editor')).toHaveAttribute('data-item', 'project/queries/what-was-revenue-by-region-last-quarter.md')
  expect(mock.saved).toHaveLength(1)
  expect(mock.saved[0].text).toContain('kind: verified-query')
  expect(mock.saved[0].text).toContain('model: Sales')
  await expect(page.getByTestId('verified-line')).toContainText('Verified by Verified Tester on 2026-11-12 against version 1 of Sales.')
  await expect(page.getByTestId('verified-questions')).toContainText('What was revenue by region last quarter?')
  await expect(page.getByTestId('verified-request')).toContainText('"kind": "query"')
  await expect(page.getByTestId('verified-sql')).toContainText('SELECT region, revenue FROM Sales')
}
