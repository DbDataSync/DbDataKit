// @vitest-environment happy-dom
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ModuleProvider } from '@dbdatakit/shell'
import { WorkspaceModule, readCatalogCache, registerDesigner } from '@dbdatakit/workspace'
import { syncDesigner } from './designer'
import { DRAFT } from './fixtures'

;(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true

const READ_AT = '2026-11-23T09:00:00Z'
const head = (connection: string, engine: string) => [
  'schema: catalog-cache/2', `generated_at: '${READ_AT}'`, 'connections:', `  '${connection}':`, `    engine: '${engine}'`,
  `    read: {server: 'runners-eu', product: 'runner', channel: 'dev', at: '${READ_AT}'}`,
]
const table = (database: string, schema: string, name: string, columns: string[]) => [
  `      - database: '${database}'`, `        schema: '${schema}'`, `        name: '${name}'`, '        kind: table', '        columns:',
  ...columns.map((c) => `          - ${c}`),
]
const ID = "{name: 'id', type: 'int', nullable: false, key: true}"

/** What the console's read answers, by connection and table: `crm`, never read before; `erp` with `dbo.invoices` added and `segment` on `dbo.orders`. */
function answer(connection: string, tableName: string | undefined): string {
  if (connection === 'lite') return [...head('lite', 'sqlite.generic'), '    databases:', "      - {name: 'main', schemas: ['main']}", '    tables:', ...table('main', 'main', 'items', [ID]), ''].join('\n')
  if (connection === 'crm') return [...head('crm', 'sqlserver'), '    databases:', "      - {name: 'crm_db', schemas: ['dbo']}", '    tables:', ...table('crm_db', 'dbo', 'accounts', [ID]), ''].join('\n')
  const orders = table('sales', 'dbo', 'orders', [ID, "{name: 'customer_id', type: 'int', nullable: true}", "{name: 'total', type: 'decimal(18,2)', nullable: true}", "{name: 'placed_at', type: 'datetime2', nullable: true}", "{name: 'segment', type: 'nvarchar(20)', nullable: true}"])
  const customers = table('sales', 'dbo', 'customers', [ID, "{name: 'name', type: 'nvarchar(100)', nullable: true}"])
  const invoices = table('sales', 'dbo', 'invoices', [ID])
  const tables = tableName === 'orders' ? orders : [...customers, ...invoices, ...table('sales', 'dbo', 'orders', [ID, "{name: 'customer_id', type: 'int', nullable: true}", "{name: 'total', type: 'decimal(18,2)', nullable: true}", "{name: 'placed_at', type: 'datetime2', nullable: true}"])]
  return [...head('erp', 'sqlserver'), '    databases:', "      - {name: 'sales', schemas: ['dbo']}", '    tables:', ...tables, ''].join('\n')
}

/**
 * The console in memory: the Workspace server through its relay (`/relay/<id>`), which answers the draft's files and records what is written, and the
 * console's own catalog read (`/api/workspace-catalog/...`), which answers at once.
 */
function fakeConsole(workspace: string, files: Record<string, string>) {
  const writes: string[] = []
  const reads: unknown[] = []
  const calls: string[] = []
  const deploys: unknown[] = []
  /** Whether the Runner holds a build of the set in dev: the set's own package reads a connection only then. */
  let staged = false
  const reply = (body: unknown, status = 200) => Promise.resolve(new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } }))
  const fetcher = vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input), 'http://localhost')
    const full = decodeURIComponent(url.pathname)
    calls.push(`${init?.method ?? 'GET'} ${full}`)
    if (full === '/api/instances') return reply([{ id: 'r-1', name: 'runners-eu', product: 'dbdatarunner' }, { id: workspace, name: 'Workspace', product: 'dbdataworkspace' }])
    const catalog = `/api/workspace-catalog/${workspace}/plant`
    if (full === `${catalog}/channels`) {
      return reply({ package: 'plant', connection: url.searchParams.get('connection'), slot: url.searchParams.get('connection'), runner: 'r-1', runner_name: 'runners-eu', default: 'dev',
        channels: [{ channel: 'dev', production: false, bound: true, bound_to: 'dev_login' }, { channel: 'prod', production: true, bound: true, bound_to: 'prod_login' }] })
    }
    if (full === '/api/deploys' && init?.method === 'POST') {
      deploys.push(JSON.parse(String(init.body)))
      staged = true
      return reply({ steps: ['Staged plant as version 1 in dev of runners-eu.'], deployed: {}, deploy: { id: 'd-1' } })
    }
    if (full === `${catalog}/read`) {
      const body = JSON.parse(String(init!.body)) as { connection: string; table?: string; channel: string; reader?: string }
      reads.push(body)
      if (body.reader === 'package' && !staged) {
        return reply({ error: 'runners-eu holds no build of plant in \'dev\'.', needs_build: true, package: 'plant', task: 'discover', runner: 'r-1', runner_name: 'runners-eu', channel: 'dev', production: false, connection: body.connection }, 409)
      }
      return reply({ state: 'done', run_id: `run-${reads.length}`, link: '/servers/r-1/runs', runner: 'r-1', runner_name: 'runners-eu', channel: body.channel, production: false, connection: body.connection, cache: answer(body.connection, body.table) })
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
    if (rest === 'builds' && init?.method === 'POST') return reply({ id: 'b-1', workspace: 'ws-p', status: 'succeeded', commit: null, requested_by: 'ana', message: null, requested: '', started: null, finished: null })
    if (rest === 'check') return reply({ kind: 'sync', ok: true, diagnostics: [] })
    if (rest === 'changes') return reply({ changes: [], uncommitted: false })
    if (rest === 'history') return reply({ commits: [] })
    if (rest === 'events') return reply({ events: [] })
    return reply({ error: 'no' }, 404)
  })
  return { files, writes, reads, calls, deploys, fetcher }
}

let root: Root
let host: HTMLDivElement
let n = 0
const settle = async () => { for (let i = 0; i < 15; i++) await act(async () => { await new Promise((r) => setTimeout(r, 5)) }) }
const q = (sel: string) => host.querySelector(sel) as HTMLElement | null
const all = (sel: string) => [...host.querySelectorAll(sel)] as HTMLElement[]
const click = async (sel: string) => { const el = q(sel); if (!el) throw new Error(`no ${sel} in ${host.innerHTML.slice(0, 3000)}`); await act(async () => { el.click() }); await settle() }
const choose = async (sel: string, value: string) => {
  const el = q(sel) as HTMLSelectElement
  await act(async () => { el.value = value; el.dispatchEvent(new Event('change', { bubbles: true })) })
  await settle()
}

describe('the Designer\'s Refresh buttons, through the console\'s catalog read', () => {
  let server: ReturnType<typeof fakeConsole>
  let workspace: string
  /** The draft held by the frame for a file: the unsaved edit's text when there is one (what Files shows). */
  let unsaved: () => string | undefined

  beforeEach(() => {
    // a Workspace server of its own per test: the module's query cache is kept per API base
    workspace = `ws-${++n}`
    server = fakeConsole(workspace, { ...DRAFT, 'config/connections/crm.yaml': 'schema: connection/1\nname: crm\nengine: sqlserver\naddress:\n  host: crm.example.com\n  database: crm_db\nauth:\n  mode: sql\n  user: sync\n  password_secret: secret:dbdatakit:sync:connection:crm\n' })
    vi.stubGlobal('fetch', server.fetcher)
    registerDesigner(syncDesigner)
    unsaved = () => (q('[data-testid="wsp-file"][data-path="catalog-cache.yaml"]')?.dataset.dirty === 'true' ? 'dirty' : undefined)
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

  /** The read in the open panel: the channel the picker offers, the difference, then Accept. */
  async function readAndAccept(panel: string, summary: string) {
    expect(q(`[data-testid="${panel}"] [data-testid="wsp-channel-picker"]`)?.dataset.channel).toBe('dev')
    await click(`[data-testid="${panel}"] [data-testid="wsp-refresh-go"]`)
    expect(q(`[data-testid="${panel}"] [data-testid="wsp-refresh-summary"]`)?.textContent).toBe(summary)
    expect(server.writes).toEqual([])
    await click(`[data-testid="${panel}"] [data-testid="wsp-refresh-accept"]`)
  }

  it('offers Refresh catalog on an endpoint card whose connection has no cache; accepted, the database picker is filled and the cache is an unsaved edit', async () => {
    await render('/plant/ws-p/designer/replications/orders')
    expect(q('[data-testid="task-source-refresh-catalog"]')).toBeNull()
    await choose('[data-testid="task-source-connection-select"]', 'crm')
    expect(q('[data-testid="task-source-no-cache"]')?.textContent).toContain('nothing for the connection crm')
    await click('[data-testid="task-source-refresh-catalog"]')
    await readAndAccept('task-source-refresh', '1 table added')
    expect(server.reads).toEqual([{ runner: 'r-1', channel: 'dev', connection: 'crm' }])
    expect(all('[data-testid="task-source-database-select"] option').map((o) => o.textContent)).toEqual(['Select…', 'crm_db'])
    expect(q('[data-testid="task-source-refresh"]')).toBeNull()
    expect(q('[data-testid="task-source-read"]')?.textContent).toContain(`read from runners-eu (runner) in dev at ${READ_AT}`)
    expect(unsaved()).toBe('dirty')
    expect(server.writes).toEqual([])
    // the read was the console's: nothing of it went to the Workspace server
    expect(server.calls.filter((c) => c.includes(`/relay/${workspace}/`) && !/\/api\/(auth\/status|notifications|v1\/projects)\b/.test(c.slice(c.indexOf('/relay/') + `/relay/${workspace}`.length)))).toEqual([])
  })

  it('reads a connection whose driver is Sync\'s own with the set\'s own package, and offers Build and stage where the channel holds no build', async () => {
    server.files['config/connections/lite.yaml'] = 'schema: connection/1\nname: lite\nengine: sqlite.generic\nconnection_string: Mode=ReadOnly\nauth:\n  mode: none\n'
    await render('/plant/ws-p/designer/replications/orders')
    await choose('[data-testid="task-source-connection-select"]', 'lite')
    await click('[data-testid="task-source-refresh-catalog"]')
    expect(q('[data-testid="task-source-refresh-own-package"]')?.textContent).toContain("lite is a sqlite.generic connection, whose driver is Sync's own")
    await click('[data-testid="task-source-refresh"] [data-testid="wsp-refresh-go"]')
    expect(server.reads).toEqual([{ runner: 'r-1', channel: 'dev', connection: 'lite', reader: 'package' }])
    expect(q('[data-testid="wsp-build-stage"]')?.dataset.channel).toBe('dev')

    // Build and stage: the draft is built on its Workspace server, the build staged in dev through the console's deploy, and the read asked again
    await click('[data-testid="wsp-build-stage-go"]')
    expect(server.calls).toContain(`POST /relay/${workspace}/api/v1/projects/plant/workspaces/ws-p/builds`)
    expect(server.deploys).toEqual([{ workspaceServer: workspace, project: 'plant', workspace: 'ws-p', build: 'b-1', target: 'r-1', channel: 'dev' }])
    expect(server.reads).toHaveLength(2)
    expect(q('[data-testid="task-source-refresh"] [data-testid="wsp-refresh-summary"]')?.textContent).toBe('1 table added')
    await click('[data-testid="task-source-refresh"] [data-testid="wsp-refresh-accept"]')
    expect(all('[data-testid="task-source-database-select"] option').map((o) => o.textContent)).toEqual(['Select…', 'main'])
  })

  it('lists a table the overview\'s Refresh found once it is accepted, before anything is saved', async () => {
    await render('/plant/ws-p/designer/replications/orders/mappings')
    expect(all('[data-testid^="select-table-"]').map((e) => e.dataset.testid)).toEqual(['select-table-dbo.customers', 'select-table-dbo.orders'])
    await click('[data-testid="mappings-refresh-button"]')
    await readAndAccept('mappings-refresh', '1 table added')
    expect(server.reads).toEqual([{ runner: 'r-1', channel: 'dev', connection: 'erp' }])
    expect(all('[data-testid^="select-table-"]').map((e) => e.dataset.testid)).toEqual(['select-table-dbo.customers', 'select-table-dbo.invoices', 'select-table-dbo.orders'])
    expect(unsaved()).toBe('dirty')
    expect(server.writes).toEqual([])
  })

  it('reads one table from the cached-metadata card, shows "1 column added", and leaves the mapping as it was', async () => {
    await render('/plant/ws-p/designer/replications/orders/mappings/dbo.orders/columns')
    await click('[data-testid="cached-metadata-source-refresh-button"]')
    await readAndAccept('cached-metadata-source-refresh', '1 column added')
    expect(server.reads).toEqual([{ runner: 'r-1', channel: 'dev', connection: 'erp', schema: 'dbo', table: 'orders' }])
    expect(unsaved()).toBe('dirty')
    expect(q('[data-testid="mapping-dirty"]')).toBeNull()
    // saved: the table read has the new column, the connection's other tables and the other connection stay as they were
    await click('[data-testid="wsp-save-all"]')
    expect(server.writes).toEqual(['catalog-cache.yaml'])
    const cache = readCatalogCache(server.files['catalog-cache.yaml']).cache!
    expect(cache.connections.erp.tables.find((t) => t.name === 'orders')?.columns.map((c) => c.name)).toContain('segment')
    expect(cache.connections.erp.tables.map((t) => t.name).sort()).toEqual(['customers', 'orders'])
    expect(Object.keys(cache.connections).sort()).toEqual(['dw', 'erp'])
  })
})

describe('the answers of the fake read', () => {
  it('are catalog caches the strict reader accepts', () => {
    for (const [c, t] of [['crm', undefined], ['erp', undefined], ['erp', 'orders']] as const) expect(readCatalogCache(answer(c, t)).cache?.connections[c]).toBeDefined()
  })
})
