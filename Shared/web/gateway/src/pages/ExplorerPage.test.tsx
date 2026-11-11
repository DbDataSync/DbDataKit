import { renderToStaticMarkup } from 'react-dom/server'
import { MemoryRouter } from 'react-router-dom'
import { describe, expect, it } from 'vitest'
import { ExplorerView, type ExplorerViewProps } from './ExplorerPage'
import { RunsView, type RunsViewProps } from './RunsPanel'
import { SavedQueriesView, SavedQueryManager, type ManagerProps } from './SavedQueriesPage'
import { view } from '../modelBrowserFixture'
import type { RunResult, RunRow, SavedQueryRow } from '../runTypes'

const noop = () => {}
const result: RunResult = {
  status: 'ok', warnings: ['one warning'], request_hash: 'abc123', model_version: 3,
  columns: [{ name: 'customer.region', type: 'string' }, { name: 'revenue', type: 'number' }],
  rows: [['East', 10], ['West', 5]],
}
const base: ExplorerViewProps = {
  model: 'shop_sales', catalog: view, mode: 'sql', text: 'SELECT 1', picks: { attributes: [], measures: [], limit: 100 }, running: false, error: null, shape: 'table', encoding: { rows: ['customer.region'], columns: [], value: 'revenue' },
  save: { name: '', description: '', saved: null, error: null, busy: false },
  onMode: noop, onText: noop, onToggle: noop, onLimit: noop, onRun: noop, onExplain: noop, onShape: noop, onDraw: noop, onEncoding: noop, onSaveName: noop, onSaveDescription: noop, onSave: noop,
}
const html = (p: Partial<ExplorerViewProps> = {}) => renderToStaticMarkup(<MemoryRouter><ExplorerView {...base} {...p} /></MemoryRouter>)

describe('the explorer', () => {
  it('offers the model\'s members as a palette and both ways of writing the statement', () => {
    const text = html()
    expect(text).toContain('data-testid="palette-attribute-customer.region"')
    expect(text).toContain('data-testid="palette-measure-revenue"')
    expect(text).toContain('data-testid="mode-json"')
    expect(text).toContain('data-testid="run"')
  })
  it('shows the rows, the status, the request hash, the version and the warnings of a result', () => {
    const text = html({ result })
    expect(text).toContain('data-testid="qis-status">complete')
    expect(text).toContain('abc123')
    expect(text).toContain('model version 3')
    expect(text).toContain('one warning')
    expect((text.match(/data-testid="result-row"/g) ?? []).length).toBe(2)
  })
  it('says a result came from the replay of a record and whether its request hash is the record\'s (D38 (3))', () => {
    expect(html({ result: { ...result, replay_of: 7, same_hash: true } })).toContain('data-testid="replay-note">replay of run 7, the same request hash')
    expect(html({ result: { ...result, replay_of: 7, same_hash: false } })).toContain('a different request hash')
    expect(html({ result })).not.toContain('replay-note')
  })
  it('shows what the engine left when it would not run everything, with the reason', () => {
    const text = html({ result: { ...result, status: 'unsupported', reason: 'The engine left steps this host does not run.', remaining: [{ step: 'x' }] } })
    expect(text).toContain('data-testid="run-unsupported"')
    expect(text).toContain('data-testid="run-remaining"')
  })
  it('shows a refusal with its code and no result', () => {
    const text = html({ error: 'unknown_measure: The model has no measure x.' })
    expect(text).toContain('data-testid="run-error"')
    expect(text).toContain('unknown_measure: The model has no measure x.')
    expect(text).not.toContain('run-result')
  })
  it('shows an explain as one card per scan and says nothing was sent', () => {
    const text = html({ result: { status: 'explained', warnings: [], request_hash: null, columns: [{ name: 'source', type: 'string' }, { name: 'engine', type: 'string' }, { name: 'text', type: 'string' }], rows: [['shop', 'postgres', 'SELECT 1 FROM t']] } })
    expect(text).toContain('data-testid="scan-source-0">shop')
    expect(text).toContain('data-testid="scan-text-0">SELECT 1 FROM t')
    expect(text).toContain('Nothing was sent to a source')
  })
  it('draws what the Gateway\'s renderer made of the matrix chosen, and says nothing of its own about the cells', () => {
    const rendered = { kind: 'rendered_view' as const, status: 'complete' as const, views: [{ id: 'result', type: 'matrix' as const, notice: null, columns: [{ labels: ['Revenue'] }], rows: [{ labels: ['East'], cells: [{ v: 10, text: '10' }] }, { labels: ['Total'], total: true, cells: [{ v: 15, text: '15' }] }] }] }
    const matrix = html({ result, shape: 'matrix', rendered })
    expect(matrix).toContain('data-testid="view-drawn"')
    expect(matrix).toContain('data-testid="vz-matrix-result"')
    expect(matrix).toContain('Total')
    expect(html({ result, shape: 'matrix', rendering: true })).toContain('data-testid="view-rendering"')
    expect(html({ result, shape: 'table', rendered })).not.toContain('view-drawn')
  })
  it('shows the renderer\'s refusal of a view with its code, and a problem reaching it apart', () => {
    const refused = html({ result, shape: 'bar', rendered: { kind: 'rendered_view', status: 'failed', errors: [{ code: 'stack_not_additive', path: '$.views[0]', message: 'orders.order_count does not add up, so it cannot be stacked.' }] } })
    expect(refused).toContain('data-testid="view-refused"')
    expect(refused).toContain('stack_not_additive')
    expect(refused).toContain('$.views[0]')
    expect(refused).toContain('orders.order_count does not add up')
    expect(refused).not.toContain('data-testid="view-drawn"')
    const broken = html({ result, shape: 'bar', renderError: 'The Gateway could not be reached.' })
    expect(broken).toContain('data-testid="view-error"')
    expect(broken).toContain('could not be reached')
  })
  it('turns the Draw button off and says which required slot is empty, and on when each is filled (D38)', () => {
    const empty = html({ result, shape: 'matrix', encoding: { rows: ['customer.region'], columns: [], value: '' } })
    expect(empty).toMatch(/<button[^>]*data-testid="view-draw"[^>]*disabled/)
    expect(empty).toContain('required slot is empty: Value')
    const bar = html({ result, shape: 'bar', encoding: { rows: [], columns: [], value: '' } })
    expect(bar).toContain('required slot is empty: Along, Value')
    expect(bar).not.toContain('data-testid="view-drawn"')
    const filled = html({ result, shape: 'matrix' })
    expect(filled).toContain('data-testid="view-draw"')
    expect(filled).not.toMatch(/<button[^>]*data-testid="view-draw"[^>]*disabled/)
    expect(filled).not.toContain('view-missing')
    expect(html({ result, shape: 'table' })).not.toContain('view-draw')
  })
  it('shows the table and the Gateway\'s note, and no matrix or chart, when a column type cannot be drawn (D38)', () => {
    const notice = { code: 'view_unavailable', column: 'logo', type: 'binary', message: 'matrix and chart are unavailable: column logo has type binary, which a view cannot read.' }
    const text = html({ result: { ...result, notices: [notice] }, shape: 'matrix' })
    expect(text).toContain('data-testid="run-notice-view_unavailable"')
    expect(text).toContain('matrix and chart are unavailable: column logo has type binary')
    expect(text).toContain('data-testid="result-table"')
    expect((text.match(/data-testid="result-row"/g) ?? []).length).toBe(2)
    expect(text).not.toContain('data-testid="view-choice"')
    expect(text).not.toContain('data-testid="shape-matrix"')
    expect(html({ result })).toContain('data-testid="view-choice"')
    expect(html({ result })).not.toContain('run-notices')
  })
  it('offers to pin what it ran to a dashboard of the person', () => {
    expect(html({ result, text: '{"query": {"measures": ["revenue"], "attributes": ["customer.region"], "limit": 5}}' })).toContain('href="/views?pin_model=shop_sales')
    expect(html({ result, text: 'SELECT 1' })).not.toContain('pin-result')
  })
})

const runs: RunRow[] = [
  { id: 7, caller: 'ada@example.org', model: 'shop_sales', model_version: 3, kind: 'query', text: 'SELECT customer.region FROM shop_sales GROUP BY ALL LIMIT 5', status: 'complete', error_code: null, request_hash: 'abcdef123456', started_at: '2026-11-11T10:00:00Z', duration_ms: 12, rows: 2, replay_of: null },
  { id: 8, caller: 'ada@example.org', model: 'shop_sales', model_version: 3, kind: 'query', text: 'SELECT nope', status: 'refused', error_code: 'unknown_attribute', request_hash: null, started_at: '2026-11-11T10:01:00Z', duration_ms: 1, rows: 0, replay_of: null },
]
const runsHtml = (p: Partial<RunsViewProps> = {}) => renderToStaticMarkup(<MemoryRouter><RunsView runs={runs} loading={false} error={null} all={false} onAll={noop} replaying={null} replayed={undefined} replayError={null} onReplay={noop} {...p} /></MemoryRouter>)

describe('the recent runs', () => {
  it('lists each run with its statement, status, hash, model version and who, and the two actions', () => {
    const text = runsHtml()
    expect(text).toContain('data-testid="run-text-7"')
    expect(text).toContain('abcdef1234')
    expect(text).toContain('unknown_attribute')
    expect(text).toContain('href="/models/shop_sales/explore?run=7"')
    expect(text).toContain('data-testid="run-replay-7"')
    expect(text).toContain('ada@example.org')
  })
  it('says when nothing was run, and what a replay found', () => {
    expect(runsHtml({ runs: [] })).toContain('runs-none')
    expect(runsHtml({ replayed: { ...result, replay_of: 7, run_id: 9, same_hash: true } as never })).toContain('the same request hash')
    expect(runsHtml({ replayError: 'Version 2 of the model shop_sales is gone' })).toContain('is gone')
  })
})

const saved: SavedQueryRow[] = [
  { model: 'shop_sales', name: 'regions', owner: 'ada@example.org', owner_name: 'Ada', own: true, version: 2, description: 'Revenue by region', sql: 'SELECT 1', request: null, parameters: {} },
  { model: 'shop_sales', name: 'theirs', owner: 'cy@example.org', owner_name: 'Cy', own: false, description: null, sql: 'SELECT 2', request: null, parameters: {} },
]

describe('the saved queries screen', () => {
  const list = (p: Partial<Parameters<typeof SavedQueriesView>[0]> = {}) => renderToStaticMarkup(<MemoryRouter><SavedQueriesView rows={saved} loading={false} error={null} model="" onModel={noop} selected={null} onManage={noop} {...p} /></MemoryRouter>)
  it('lists the own and the shared with the owner\'s email and name', () => {
    const text = list()
    expect(text).toContain('data-testid="sq-owner-shop_sales-regions-own">you')
    expect(text).toContain('cy@example.org (Cy)')
    expect(text).toContain('href="/models/shop_sales/explore?saved=theirs&amp;owner=cy%40example.org"')
  })
  it('says there is none yet', () => { expect(list({ rows: [] })).toContain('sq-none') })
  const manager = (row: SavedQueryRow, over: Partial<ManagerProps> = {}): ManagerProps => ({
    row, text: 'SELECT 1', description: '', history: [{ version: 2, saved_by: 'ada@example.org', saved_at: '2026-11-11T10:00:00Z', description: null, sql: 'SELECT 1', request: null, parameters: {} }, { version: 1, saved_by: 'ada@example.org', saved_at: '2026-11-10T10:00:00Z', description: null, sql: 'SELECT 0', request: null, parameters: {} }],
    shares: 'cy@example.org\ngroup:sales', notice: null, problem: null, confirmDelete: false, onText: noop, onDescription: noop, onSave: noop, onRestore: noop, onShares: noop, onShare: noop, onDelete: noop, onClose: noop, ...over,
  })
  it('lets the owner edit, restore an earlier version, share and delete', () => {
    const text = renderToStaticMarkup(<SavedQueryManager {...manager(saved[0]!)} />)
    expect(text).toContain('data-testid="sq-save"')
    expect(text).toContain('data-testid="sq-restore-1"')
    expect(text).not.toContain('data-testid="sq-restore-2"')
    expect(text).toContain('Shared with cy@example.org, the group sales.')
    expect(text).toContain('data-testid="sq-delete"')
    expect(renderToStaticMarkup(<SavedQueryManager {...manager(saved[0]!, { confirmDelete: true })} />)).toContain('sq-delete-confirm')
  })
  it('names a sharing it cannot read and leaves a shared query read-only', () => {
    expect(renderToStaticMarkup(<SavedQueryManager {...manager(saved[0]!, { shares: 'everyone' })} />)).toContain('data-testid="sq-share-problem"')
    const theirs = renderToStaticMarkup(<SavedQueryManager {...manager(saved[1]!)} />)
    expect(theirs).toContain('data-testid="sq-readonly"')
    expect(theirs).not.toContain('data-testid="sq-save"')
    expect(theirs).toContain('cy@example.org (Cy)')
  })
})
