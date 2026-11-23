// @vitest-environment happy-dom
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { MemoryRouter, useLocation } from 'react-router-dom'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { parseYaml } from '@dbdatakit/shell'
import { DraftProvider, WorkspaceModule, registerDesigner, useDraft } from '@dbdatakit/workspace'
import { syncDesigner } from './designer'
import { unsavedNote } from './replication/PublishCard'
import { useTables } from './metadata'
import { CATALOG, DRAFT } from './fixtures'

;(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true

/** A Workspace server in memory with one sync project and its draft; it answers the frame's file calls and records what is written. */
function fakeServer(files: Record<string, string>) {
  const writes: string[] = []
  const deletes: string[] = []
  const calls: string[] = []
  const reply = (body: unknown, status = 200) => Promise.resolve(new Response(status === 204 ? null : JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } }))
  const fetcher = vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input), 'http://localhost')
    const path = decodeURIComponent(url.pathname)
    calls.push(`${init?.method ?? 'GET'} ${path}`)
    if (path === '/api/auth/status') return reply({ authenticated: true, role: 'Admin', name: 'ana' })
    if (path === '/api/v1/projects') return reply({ projects: [{ id: 'plant', kind: 'sync', title: null, created: '', workspaces: 1 }] })
    const m = /^\/api\/v1\/projects\/([^/]+)\/workspaces(?:\/([^/]+)(?:\/(.*))?)?$/.exec(path)
    if (!m) return reply({ error: 'no' }, 404)
    const [, project, id, rest] = m
    if (!id) return reply({ workspaces: [{ id: 'ws-p', project, name: 'edit', branch: 'workspace/edit', base: 'abc', created: '', owner: 'ana', mine: true }] })
    if (rest === 'files') return reply({ files: Object.keys(files).sort().map((p) => ({ path: p, size: files[p].length })), truncated: false, max_file_bytes: 1 })
    if (rest?.startsWith('files/')) {
      const p = rest.slice('files/'.length)
      if (init?.method === 'PUT') { files[p] = (JSON.parse(String(init.body)) as { content: string }).content; writes.push(p); return reply({ path: p, created: false, size: 1 }) }
      if (init?.method === 'DELETE') { delete files[p]; deletes.push(p); return reply(null, 204) }
      return p in files ? reply({ path: p, content: files[p] }) : reply({ error: 'none' }, 404)
    }
    if (rest === 'check') return reply({ kind: 'sync', ok: true, diagnostics: [] })
    if (rest === 'changes') return reply({ changes: [], uncommitted: false })
    if (rest === 'history') return reply({ commits: [] })
    if (rest === 'events') return reply({ events: [] })
    return reply({ error: 'no' }, 404)
  })
  return { files, writes, deletes, calls, fetcher }
}

let root: Root
let host: HTMLDivElement
let where = ''
let query = ''
function Where() { const at = useLocation(); where = at.pathname; query = at.search; return null }
const settle = async () => { for (let i = 0; i < 12; i++) await act(async () => { await new Promise((r) => setTimeout(r, 5)) }) }
const q = (sel: string) => host.querySelector(sel) as HTMLElement | null
const all = (sel: string) => [...host.querySelectorAll(sel)] as HTMLElement[]
const click = async (sel: string) => { const el = q(sel); if (!el) throw new Error(`no ${sel} in ${host.innerHTML.slice(0, 3000)}`); await act(async () => { el.click() }); await settle() }

async function render(node: React.ReactNode, at: string) {
  host = document.createElement('div')
  document.body.appendChild(host)
  root = createRoot(host)
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  await act(async () => { root.render(<QueryClientProvider client={client}><MemoryRouter initialEntries={[at]}><Where />{node}</MemoryRouter></QueryClientProvider>) })
  await settle()
}

/** What the draft now says of a file: the unsaved edit, else the server's text (read through the frame, as Files would). */
const yamlOf = (text: string | undefined) => parseYaml(text ?? '').value as Record<string, any>

describe('the sync Designer in the Workspace frame', () => {
  let server: ReturnType<typeof fakeServer>
  beforeEach(() => {
    server = fakeServer({ ...DRAFT })
    vi.stubGlobal('fetch', server.fetcher)
    registerDesigner(syncDesigner)
  })
  afterEach(() => { if (root) act(() => root.unmount()); host?.remove(); vi.unstubAllGlobals() })

  it('opens a sync draft on its first replication\'s Overview, with the endpoints and the schedule from the files and the databases from the catalog cache', async () => {
    await render(<WorkspaceModule />, '/plant/ws-p')
    expect(where).toBe('/plant/ws-p/designer/replications/orders')
    expect(q('[data-testid="sd-replication"]')?.dataset.name).toBe('orders')
    expect((q('[data-testid="task-source-connection-select"]') as HTMLSelectElement).value).toBe('erp')
    expect((q('[data-testid="task-source-database-select"]') as HTMLSelectElement).value).toBe('sales')
    expect(all('[data-testid="task-target-database-select"] option').map((o) => o.textContent)).toEqual(['Select…', 'warehouse'])
    expect(q('[data-testid="task-source-read"]')?.textContent).toContain('read from runners-eu (runner) in dev')
    expect((q('[data-testid="schedule-frequency-input"]') as HTMLInputElement).value).toBe('60')
    expect(q('[data-testid="enabled-toggle"]')?.getAttribute('aria-pressed')).toBe('true')
  })

  it('turns Enabled off as an unsaved edit of task.yaml that keeps the rest of the file, and Save settings writes it', async () => {
    await render(<WorkspaceModule />, '/plant/ws-p/designer/replications/orders')
    await click('[data-testid="enabled-toggle"]')
    expect(q('[data-testid="wsp-unsaved"]')?.dataset.count).toBe('1')
    expect(q('[data-testid="sd-task-dirty"]')).not.toBeNull()
    expect(server.writes).toEqual([])
    await click('[data-testid="save-settings-button"]')
    expect(server.writes).toEqual(['config/replications/orders/task.yaml'])
    const text = server.files['config/replications/orders/task.yaml']
    expect(text).toContain('# the nightly orders copy')
    expect(text).toContain('owner: data-team')
    expect(yamlOf(text).enabled).toBe(false)
  })

  it('makes two mappings from the ticked tables of the source, as unsaved files with their cached columns', async () => {
    await render(<WorkspaceModule />, '/plant/ws-p/designer/replications/orders/mappings')
    expect(all('[data-testid^="select-table-"]').map((e) => e.dataset.testid)).toEqual(['select-table-dbo.customers', 'select-table-dbo.orders'])
    expect(q('[data-testid="table-mapping-count-dbo.orders"]')?.textContent).toBe('1')
    await click('[data-testid="select-table-dbo.customers"]')
    await click('[data-testid="create-mappings-button"]')
    const made = 'config/replications/orders/table-mappings/dbo.customers.yaml'
    expect(q(`[data-testid="mapping-item-dbo.customers"]`)).not.toBeNull()
    expect(q('[data-testid="bulk-create-notes"]')?.textContent).toContain('dbo.customers')
    expect(server.writes).toEqual([])
    expect(q('[data-testid="wsp-unsaved"]')?.dataset.count).toBe('1')
    expect(q(`[data-testid="wsp-file"][data-path="${made}"]`)?.dataset.dirty).toBe('true')
  })

  it('overrides the target side of a mapping Create mappings made, leaving its source as it was', async () => {
    await render(<WorkspaceModule />, '/plant/ws-p/designer/replications/orders/mappings')
    await click('[data-testid="select-table-dbo.customers"]')
    await click('[data-testid="create-mappings-button"]')
    await click('[data-testid="mapping-item-dbo.customers"]')
    await click('[data-testid="target-override-toggle"]')
    expect(q('[data-testid="target-override-toggle"]')?.getAttribute('aria-pressed')).toBe('true')
    expect((q('[data-testid="target-connection-select"]') as HTMLSelectElement).value).toBe('dw')
    await click('[data-testid="save-mapping-button"]')
    const saved = yamlOf(server.files['config/replications/orders/table-mappings/dbo.customers.yaml'])
    expect(saved.targets).toEqual([{ schema: 'dbo', table: 'customers', connectionName: 'dw', database: 'warehouse' }])
    expect(saved.sources).toEqual([{ schema: 'dbo', table: 'customers' }])
  })

  it('edits a mapping\'s columns with Auto-map by name and the key ticks, as unsaved edits Save mapping writes', async () => {
    await render(<WorkspaceModule />, '/plant/ws-p/designer/replications/orders/mappings/dbo.orders/columns')
    expect(q('[data-testid="mapping-editor"]')?.dataset.name).toBe('dbo.orders')
    expect(all('[data-testid="column-mapping-row"]').map((r) => r.dataset.target)).toEqual(['id'])
    await click('[data-testid="auto-map-button"]')
    expect(all('[data-testid="column-mapping-row"]').map((r) => r.dataset.target)).toEqual(['id', 'customer_id', 'total'])
    await click('[data-testid="primary-key-column-id"]')
    expect(q('[data-testid="mapping-dirty"]')).not.toBeNull()
    await click('[data-testid="save-mapping-button"]')
    const saved = yamlOf(server.files['config/replications/orders/table-mappings/dbo.orders.yaml'])
    expect(saved.columnMappings.map((c: { targetColumn: string }) => c.targetColumn)).toEqual(['id', 'customer_id', 'total'])
    expect(saved.primaryKey).toEqual(['id'])
    expect(saved.sources).toEqual([{ schema: 'dbo', table: 'orders' }])
  })

  it('shows a mapping of a newer format as a line that opens it in Files', async () => {
    server.files['config/replications/orders/table-mappings/dbo.orders.yaml'] = 'schemaVersion: 9\nname: dbo.orders\n'
    await render(<WorkspaceModule />, '/plant/ws-p/designer/replications/orders')
    expect(q('[data-testid="sd-problem"]')?.dataset.path).toBe('config/replications/orders/table-mappings/dbo.orders.yaml')
    expect(q('[data-testid="sd-problem"]')?.textContent).toContain('is in format 9, which is newer than the Designer reads (1)')
  })

  it('deletes a replication only after asking twice', async () => {
    await render(<WorkspaceModule />, '/plant/ws-p/designer/replications/orders')
    await click('[data-testid="delete-replication-button"]')
    expect(q('[data-testid="delete-replication-ask"]')?.dataset.step).toBe('1')
    await click('[data-testid="delete-replication-confirm"]')
    expect(q('[data-testid="delete-replication-ask"]')?.dataset.step).toBe('2')
    expect(server.deletes).toEqual([])
    await click('[data-testid="delete-replication-confirm"]')
    expect(server.deletes.sort()).toEqual(['config/replications/orders/table-mappings/dbo.orders.yaml', 'config/replications/orders/task.yaml'])
  })

  it('makes a new replication from the navigation\'s dialog and opens its Overview', async () => {
    await render(<WorkspaceModule />, '/plant/ws-p/designer/replications/orders')
    await click('[data-testid="sd-new-replication"]')
    const input = q('[data-testid="replication-name-input"]') as HTMLInputElement
    await act(async () => {
      Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!.call(input, 'nightly')
      input.dispatchEvent(new Event('input', { bubbles: true }))
    })
    await click('[data-testid="create-replication-button"]')
    expect(where).toBe('/plant/ws-p/designer/replications/nightly')
    expect(q('[data-testid="sd-replication"]')?.dataset.name).toBe('nightly')
    expect(q('[data-testid="enabled-toggle"]')?.getAttribute('aria-pressed')).toBe('false')
    expect(q('[data-testid="wsp-file"][data-path="config/replications/nightly/task.yaml"]')?.dataset.dirty).toBe('true')
  })

  it('opens the Builds tab of the same draft from Build and publish… in the rail, keeping an unsaved edit and saying a build leaves it out', async () => {
    await render(<WorkspaceModule />, '/plant/ws-p/designer/replications/orders')
    expect(q('[data-testid="sd-rail"] [data-testid="sd-build-and-publish"]')?.textContent).toBe('Build and publish…')
    expect(q('[data-testid="sd-publish-unsaved"]')).toBeNull()
    await click('[data-testid="enabled-toggle"]')
    expect(q('[data-testid="sd-publish-unsaved"]')?.dataset.count).toBe('1')
    expect(q('[data-testid="sd-publish-unsaved"]')?.textContent).toBe('One edit of this draft is not saved, so a build leaves it out. Save first to build it.')
    await click('[data-testid="sd-build-and-publish"]')
    expect(where).toBe('/plant/ws-p/files')
    expect(query).toBe('?tab=builds')
    expect(q('[data-testid="wsp-builds"]')).not.toBeNull()
    expect(q('[data-testid="wsp-unsaved"]')?.dataset.count).toBe('1')
    expect(server.writes).toEqual([])
  })

  it('says how many unsaved edits a build leaves out', () => {
    expect(unsavedNote(0)).toBeNull()
    expect(unsavedNote(3)).toBe('3 edits of this draft are not saved, so a build leaves them out. Save first to build them.')
  })

  it('asks the Workspace for files only: no Runner, Gateway or run route is called', async () => {
    await render(<WorkspaceModule />, '/plant/ws-p/designer/replications/orders/mappings/dbo.orders/columns')
    expect(server.calls.filter((c) => !/^(GET|PUT|DELETE) \/api\/(auth\/status|notifications|v1\/projects)\b/.test(c))).toEqual([])
  })
})

/** A component that shows the tables the metadata hook answers, and edits the cache without saving it. */
function TablesOf({ connection, database, next }: { connection: string; database: string; next: string }) {
  const draft = useDraft()
  const tables = useTables(connection, database)
  return (
    <div data-testid="tables" data-names={(tables.data ?? []).map((t) => `${t.schema}.${t.table}`).join(',')}>
      <button type="button" data-testid="edit-cache" onClick={() => draft.edit('catalog-cache.yaml', next)}>edit</button>
    </div>
  )
}

describe('the metadata hooks', () => {
  let server: ReturnType<typeof fakeServer>
  beforeEach(() => { server = fakeServer({ ...DRAFT }); vi.stubGlobal('fetch', server.fetcher) })
  afterEach(() => { if (root) act(() => root.unmount()); host?.remove(); vi.unstubAllGlobals() })

  it('answer a table of an unsaved edit of the catalog cache', async () => {
    const next = CATALOG.replace(`      - database: sales
        schema: dbo
        name: orders`, `      - database: sales
        schema: dbo
        name: invoices
        kind: table
        columns:
          - {name: id, type: int, nullable: false, key: true}
      - database: sales
        schema: dbo
        name: orders`)
    await render(<DraftProvider project="plant" id="ws-p" kind="sync" canEdit><TablesOf connection="erp" database="sales" next={next} /></DraftProvider>, '/')
    expect(q('[data-testid="tables"]')?.dataset.names).toBe('dbo.customers,dbo.orders')
    await click('[data-testid="edit-cache"]')
    expect(q('[data-testid="tables"]')?.dataset.names).toBe('dbo.customers,dbo.invoices,dbo.orders')
    expect(server.writes).toEqual([])
  })
})
