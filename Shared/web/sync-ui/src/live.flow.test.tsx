// @vitest-environment happy-dom
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ModuleProvider, parseYaml } from '@dbdatakit/shell'
import { WorkspaceModule, registerDesigner } from '@dbdatakit/workspace'
import { syncDesigner } from './designer'
import { DRAFT } from './fixtures'
import { describedColumns, queryColumnsLine } from './mappings/QueryColumnsCard'
import { partLabel, partsOf } from './replication/NewestPreflight'

;(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true

/** The mapping `dbo.orders` with a query for its source in place of a table. */
const QUERY_MAPPING = 'schemaVersion: 1\nname: dbo.orders\nsources:\n- query: SELECT id, total FROM dbo.orders\ntargets:\n- schema: dbo\n  table: orders\ncolumnMappings: []\nprimaryKey: []\n'

/** What the set's preflight found in dev: a part of each mapping of `orders`, the state of `orders`, and a part of another replication. */
const PREFLIGHT_STEPS = [
  { key: 'orders/dbo.orders', result: { part: 'orders/dbo.orders', findings: [{ severity: 'error', message: "The run login of 'dw' may not UPDATE dw.dbo.orders.", about: 'dw' }] } },
  { key: 'orders tracking', result: { part: 'orders tracking', findings: [] } },
  { key: 'invoices/dbo.invoices', result: { part: 'invoices/dbo.invoices', findings: [{ severity: 'warning', message: 'not about orders', about: 'erp' }] } },
]

/**
 * The console in memory: the Workspace server through its relay, and the console's own routes the Designer's live pieces ask: the channels of a
 * connection (`erp` is bound in dev; `dw` is not, so dev offers Set up for development), the binding test, the description of a query, the draft's deploys
 * and the newest preflight of the channel deployed to.
 */
function fakeConsole(workspace: string, files: Record<string, string>) {
  const writes: string[] = []
  const asked: { path: string; body: unknown }[] = []
  const reply = (body: unknown, status = 200) => Promise.resolve(new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } }))
  const fetcher = vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input), 'http://localhost')
    const full = decodeURIComponent(url.pathname)
    asked.push({ path: `${init?.method ?? 'GET'} ${full}${url.search}`, body: init?.body ? JSON.parse(String(init.body)) : null })
    if (full === '/api/instances') return reply([{ id: 'r-1', name: 'runners-eu', product: 'dbdatarunner' }, { id: workspace, name: 'Workspace', product: 'dbdataworkspace' }])
    const catalog = `/api/workspace-catalog/${workspace}/plant`
    const base = { package: 'plant', runner: 'r-1', runner_name: 'runners-eu' }
    if (full === `${catalog}/channels`) {
      const connection = url.searchParams.get('connection')!
      const devBound = connection === 'erp'
      return reply({ ...base, connection, slot: connection, default: devBound ? 'dev' : null,
        channels: [{ channel: 'dev', production: false, bound: devBound, bound_to: devBound ? `${connection}_dev` : null }, { channel: 'prod', production: true, bound: true, bound_to: connection }] })
    }
    if (full === `${catalog}/binding-test`) {
      const body = JSON.parse(String(init!.body)) as { connection: string; channel: string }
      return reply({ ...base, connection: body.connection, slot: body.connection, channel: body.channel, production: false, role: 'default', ok: true, step: 'connect', connected: true,
        bound_to: `${body.connection}_dev`, message: `The slot '${body.connection}' opened a connection through '${body.connection}_dev'.`,
        steps: ['binding', 'connection', 'login', 'connect'].map((step) => ({ step, state: 'passed' })) })
    }
    if (full === `${catalog}/describe`) {
      const body = JSON.parse(String(init!.body)) as { connection: string; channel: string }
      return reply({ ...base, state: 'done', run_id: 'run-d', link: '/servers/r-1/runs?run=run-d', channel: body.channel, production: false, connection: body.connection, engine: 'sqlserver',
        strategy: 'SqlServerDescribeFirstResultSet', columns: [{ name: 'id', type: 'int', nullable: false }, { name: 'total', type: 'decimal(18,2)', nullable: true }] })
    }
    if (full === '/api/deploys') {
      return reply({ deploys: [{
        id: 'd-1', workspace_server: workspace, project: 'plant', workspace: 'ws-p', build: 'b1', item: 'package', kind: 'sync', target: 'r-1', target_name: 'runners-eu', product: 'runner', route: 'package',
        name: 'plant', channel: 'dev', version: 3, mode: 'stage', signature: null, deployed_by: 'ana', deployed_at: '2026-11-24T10:00:00Z', outcome: 'ok', error: null, tasks: ['orders', 'provision', 'preflight'],
      }] })
    }
    if (full === '/api/deploys/d-1/runs' && (init?.method ?? 'GET') === 'GET') {
      return reply({ ...base, channel: 'dev', task: 'preflight', link: '/servers/r-1/runs?run=run-p',
        run: { id: 'run-p', task: 'preflight', outcome: 'succeeded', started: '2026-11-24T11:00:00Z', finished: '2026-11-24T11:00:04Z', version: '3', failure: null, steps: PREFLIGHT_STEPS.map((s) => ({ ...s, started: '', completed: '' })) } })
    }
    const path = full.replace(new RegExp(`^/relay/${workspace}`), '')
    if (path === '/api/auth/status') return reply({ authenticated: true, role: 'Admin', name: 'ana' })
    if (path === '/api/v1/projects') return reply({ projects: [{ id: 'plant', kind: 'sync', title: null, created: '', workspaces: 1 }] })
    const m = /^\/api\/v1\/projects\/plant\/workspaces(?:\/([^/]+)(?:\/(.*))?)?$/.exec(path)
    if (!m) return reply({ error: 'no' }, 404)
    const [, id, rest] = m
    if (!id) return reply({ workspaces: [{ id: 'ws-p', project: 'plant', name: 'edit', branch: 'workspace/edit', base: 'abc', created: '', owner: 'ana', mine: true }] })
    if (rest === 'files') return reply({ files: Object.keys(files).sort().map((p) => ({ path: p, size: files[p].length })), truncated: false, max_file_bytes: 1 })
    if (rest?.startsWith('files/')) {
      const p = rest.slice('files/'.length)
      if (init?.method === 'PUT') { files[p] = (JSON.parse(String(init.body)) as { content: string }).content; writes.push(p); return reply({ path: p, created: false, size: 1 }) }
      return p in files ? reply({ path: p, content: files[p] }) : reply({ error: 'none' }, 404)
    }
    if (rest === 'check') return reply({ kind: 'sync', ok: true, diagnostics: [] })
    if (rest === 'changes') return reply({ changes: [], uncommitted: false })
    if (rest === 'history') return reply({ commits: [] })
    if (rest === 'events') return reply({ events: [] })
    return reply({ error: 'no' }, 404)
  })
  return { files, writes, asked, fetcher }
}

let root: Root
let host: HTMLDivElement
let n = 0
const settle = async () => { for (let i = 0; i < 15; i++) await act(async () => { await new Promise((r) => setTimeout(r, 5)) }) }
const q = (sel: string) => host.querySelector(sel) as HTMLElement | null
const all = (sel: string) => [...host.querySelectorAll(sel)] as HTMLElement[]
const click = async (sel: string) => { const el = q(sel); if (!el) throw new Error(`no ${sel} in ${host.innerHTML.slice(0, 3000)}`); await act(async () => { el.click() }); await settle() }

describe('the Designer\'s live pieces of a replication, through the console', () => {
  let server: ReturnType<typeof fakeConsole>
  let workspace: string

  beforeEach(() => {
    workspace = `wl-${++n}`
    server = fakeConsole(workspace, { ...DRAFT })
    vi.stubGlobal('fetch', server.fetcher)
    registerDesigner(syncDesigner)
  })
  afterEach(() => { if (root) act(() => root.unmount()); host?.remove(); vi.unstubAllGlobals() })

  async function render(at: string) {
    host = document.createElement('div')
    document.body.appendChild(host)
    root = createRoot(host)
    await act(async () => {
      root.render(<MemoryRouter initialEntries={[at]}><ModuleProvider info={{ basePath: '', apiBase: `/relay/${workspace}` }}><WorkspaceModule /></ModuleProvider></MemoryRouter>)
    })
    await settle()
  }

  it('tests the source endpoint in dev with each step of the binding test, and writes nothing', async () => {
    await render('/plant/ws-p/designer/replications/orders')
    await click('[data-testid="task-source-test-button"]')
    const panel = '[data-testid="task-source-test"]'
    expect(q(`${panel} [data-testid="wsp-channel-picker"]`)?.dataset.channel).toBe('dev')
    await click(`${panel} [data-testid="wsp-test-go"]`)
    expect(q(`${panel} [data-testid="wsp-test-result"]`)?.dataset.ok).toBe('true')
    expect(all(`${panel} [data-testid="wsp-test-step"]`).map((s) => `${s.dataset.step} ${s.dataset.state}`)).toEqual(['binding passed', 'connection passed', 'login passed', 'connect passed'])
    expect(server.asked.find((a) => a.path.endsWith('/binding-test'))?.body).toEqual({ runner: 'r-1', channel: 'dev', connection: 'erp', role: 'default' })
    expect(server.writes).toEqual([])
  })

  it('offers Set up for development on the target endpoint, whose slot dev does not bind, and no test until a channel binds it', async () => {
    await render('/plant/ws-p/designer/replications/orders')
    await click('[data-testid="task-target-test-button"]')
    const panel = '[data-testid="task-target-test"]'
    expect(q(`${panel} [data-testid="wsp-setup-open"]`)?.textContent).toBe('Set up dw for development')
    expect(q(`${panel} [data-testid="wsp-test-go"]`)).toBeNull()
    expect(q(`${panel} [data-testid="task-target-test-pick"]`)?.textContent).toContain('binds the slot of dw')
  })

  it('describes a query source and writes its columns into the mapping, pairing the target\'s columns of the same name, as an unsaved edit', async () => {
    server.files['config/replications/orders/table-mappings/dbo.orders.yaml'] = QUERY_MAPPING
    await render('/plant/ws-p/designer/replications/orders/mappings/dbo.orders/columns')
    expect(q('[data-testid="query-columns-card"]')?.dataset.count).toBe('0')
    await click('[data-testid="query-columns-describe-button"]')
    await click('[data-testid="query-columns-describe"] [data-testid="wsp-describe-go"]')
    expect(server.asked.find((a) => a.path.endsWith('/describe'))?.body).toEqual({ runner: 'r-1', channel: 'dev', connection: 'erp', sql: 'SELECT id, total FROM dbo.orders' })
    expect(q('[data-testid="wsp-describe-summary"]')?.textContent).toBe('2 columns: id int, total decimal(18,2) null')
    await click('[data-testid="wsp-describe-use"]')
    expect(q('[data-testid="query-columns-card"]')?.dataset.count).toBe('2')
    expect(q('[data-testid="mapping-dirty"]')).not.toBeNull()
    expect(server.writes).toEqual([])
    await click('[data-testid="save-mapping-button"]')
    const saved = parseYaml(server.files['config/replications/orders/table-mappings/dbo.orders.yaml']).value as Record<string, unknown>
    expect(saved.sourceColumns).toEqual([
      { name: 'id', nativeType: 'int', isNullable: false, isPrimaryKey: false, isIdentity: false },
      { name: 'total', nativeType: 'decimal(18,2)', isNullable: true, isPrimaryKey: false, isIdentity: false },
    ])
    expect(saved.columnMappings).toEqual([{ sourceColumn: 'id', targetColumn: 'id' }, { sourceColumn: 'total', targetColumn: 'total' }])
  })

  it('shows the newest preflight of dev on the replication\'s Provisioning sub-tab, by mapping, with the run\'s link', async () => {
    await render('/plant/ws-p/designer/replications/orders/overview/provisioning')
    const card = q('[data-testid="newest-preflight"]')!
    expect(card.dataset.channel).toBe('dev')
    expect(card.dataset.errors).toBe('1')
    expect(q('[data-testid="newest-preflight-when"]')?.textContent).toContain('preflight of version 3, succeeded, finished 2026-11-24T11:00:04Z')
    expect(q('[data-testid="newest-preflight-link"]')?.getAttribute('href')).toBe('/servers/r-1/runs?run=run-p')
    expect(all('[data-testid="newest-preflight-part"]').map((p) => p.dataset.part)).toEqual(['orders/dbo.orders', 'orders tracking'])
    expect(server.asked.some((a) => a.path === 'GET /api/deploys/d-1/runs?task=preflight&step=preflight-findings')).toBe(true)
  })

  it('shows only the mapping\'s own part of the newest preflight on its Provisioning tab', async () => {
    await render('/plant/ws-p/designer/replications/orders/mappings/dbo.orders/provisioning')
    expect(all('[data-testid="newest-preflight-part"]').map((p) => p.dataset.part)).toEqual(['orders/dbo.orders'])
    expect(q('[data-testid="newest-preflight-summary"]')?.textContent).toBe('The preflight found 1 error and 0 warnings.')
  })
})

describe('the pieces of the live reads', () => {
  it('records a described column as a mapping records a source column, with no key', () => {
    expect(describedColumns([{ name: 'id', type: 'int', nullable: false }])).toEqual([{ name: 'id', nativeType: 'int', isNullable: false, isPrimaryKey: false, isIdentity: false }])
    expect(queryColumnsLine(undefined)).toContain('No column of the query is recorded yet')
    expect(queryColumnsLine(describedColumns([{ name: 'id', type: 'int', nullable: false }]))).toBe('1 column recorded: id')
  })

  it('takes a replication\'s parts of the set\'s preflight, or one mapping\'s', () => {
    const parts = [{ part: 'orders/a', findings: [] }, { part: 'orders tracking', findings: [] }, { part: 'orders', findings: [] }, { part: 'orders-eu/a', findings: [] }, { part: 'other/a', findings: [] }]
    expect(partsOf(parts, 'orders').map((p) => p.part)).toEqual(['orders/a', 'orders tracking', 'orders'])
    expect(partsOf(parts, 'orders', 'a').map((p) => p.part)).toEqual(['orders/a'])
    expect(partLabel('orders/a', 'orders')).toBe('a')
    expect(partLabel('orders tracking', 'orders')).toBe('the replication\'s state on its target')
  })
})
