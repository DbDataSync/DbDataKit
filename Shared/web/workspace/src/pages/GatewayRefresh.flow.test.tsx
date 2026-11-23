// @vitest-environment happy-dom
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { MemoryRouter } from 'react-router-dom'
import { ModuleProvider } from '@dbdatakit/shell'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { DraftProvider, useDraft } from '../draft'
import { readCatalogCache } from '../catalogCache'
import { CatalogPanel } from './CatalogPanel'

;(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true

/** What the console answers for a read of the data source `dwh` for the connection `warehouse`: one table, read from the Gateway `edge`. */
const ANSWER = [
  'schema: catalog-cache/2', "generated_at: '2026-11-23T10:00:00Z'", 'connections:',
  "  'warehouse':", "    engine: 'postgres'", "    read: {server: 'edge', product: 'gateway', at: '2026-11-23T10:00:00Z'}", '    databases: []', '    tables:',
  "      - schema: 'public'", "        name: 'orders'", '        kind: table', '        columns:',
  "          - {name: 'id', type: 'int64', nullable: false, key: true}", "          - {name: 'placed_at', type: 'timestamp', nullable: true}", '',
].join('\n')

/** The Workspace server through the relay (`/relay/ws-1`) and the console's own routes, in memory. The data source `legacy` was made by hand: it is read only with `adopt`. */
function fakeConsole() {
  const asked: { path: string; body: unknown }[] = []
  const reply = (body: unknown, status = 200) => Promise.resolve(new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } }))
  const fetcher = vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input), 'http://localhost')
    const path = decodeURIComponent(url.pathname)
    const body = init?.body ? JSON.parse(String(init.body)) as Record<string, unknown> : null
    asked.push({ path: `${init?.method ?? 'GET'} ${path}`, body })
    const draft = '/relay/ws-1/api/v1/projects/dw-build/workspaces/ws'
    if (path === `${draft}/files`) return reply({ files: [{ path: 'connections/warehouse.yaml', size: 1 }], truncated: false, max_file_bytes: 1 })
    if (path === '/api/instances') return reply([{ id: 'gw-1', name: 'edge', product: 'dbdatagateway' }, { id: 'r-1', name: 'runners-eu', product: 'dbdatarunner' }, { id: 'ws-1', name: 'Workspace', product: 'dbdataworkspace' }])
    if (path === '/api/workspace-catalog/ws-1/dw-build/channels') return reply({ package: 'dw-build', connection: 'warehouse', slot: 'warehouse', runner: 'r-1', runner_name: 'runners-eu', default: null, channels: [] })
    if (path === '/api/workspace-catalog/ws-1/dw-build/gateway-read') {
      if (body?.data_source === 'legacy' && body.adopt !== true) {
        return reply({ error: "The Gateway refused to give the catalog of 'legacy' (409): made by hand.", adopt: { data_source: 'legacy', made_by: 'hand' } }, 409)
      }
      return reply({ state: 'done', gateway: 'gw-1', gateway_name: 'edge', connection: 'warehouse', data_source: body?.data_source ?? 'warehouse', cache: ANSWER, tables: 1, columns: 2 })
    }
    return reply({ error: `no ${path}` }, 404)
  })
  return { asked, fetcher }
}

let root: Root
let host: HTMLDivElement
let draftText: string | undefined

/** Reads what the frame holds, as another area of the draft would. */
function Frame() {
  const d = useDraft()
  draftText = d.dirtyPaths.includes('catalog-cache.yaml') ? d.textOf('catalog-cache.yaml') : undefined
  return null
}

const settle = async () => { for (let i = 0; i < 15; i++) await act(async () => { await new Promise((r) => setTimeout(r, 5)) }) }
const q = (sel: string) => host.querySelector(sel) as HTMLElement | null
const within = (sel: string) => `[data-testid="wsp-gateway-refresh"] ${sel}`
const click = async (sel: string) => { const el = q(sel); if (!el) throw new Error(`no ${sel} in ${host.innerHTML.slice(0, 3000)}`); await act(async () => { el.click() }); await settle() }
const type = async (sel: string, value: string) => {
  const el = q(sel) as HTMLInputElement
  const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!
  await act(async () => { setter.call(el, value); el.dispatchEvent(new Event('input', { bubbles: true })) })
  await settle()
}

async function mount() {
  host = document.createElement('div')
  document.body.appendChild(host)
  root = createRoot(host)
  await act(async () => {
    root.render(
      <MemoryRouter>
        <ModuleProvider info={{ basePath: '/servers/ws-1', apiBase: '/relay/ws-1' }}>
          <DraftProvider project="dw-build" id="ws" kind="build" canEdit>
            <Frame />
            <CatalogPanel project="dw-build" id="ws" onOpen={() => {}} kind="build" />
          </DraftProvider>
        </ModuleProvider>
      </MemoryRouter>,
    )
  })
  await settle()
}

describe('Refresh from a Gateway on the Catalog tab', () => {
  let server: ReturnType<typeof fakeConsole>
  beforeEach(() => { server = fakeConsole(); vi.stubGlobal('fetch', server.fetcher); draftText = undefined })
  afterEach(() => { act(() => root.unmount()); host.remove(); vi.unstubAllGlobals() })

  it('asks the console, shows what the read adds, and accepted the cache is an unsaved edit read from the Gateway; the Workspace server is not asked', async () => {
    await mount()
    expect((q(within('[data-testid="wsp-catalog-refresh-gateway"]')) as HTMLSelectElement).value).toBe('gw-1')
    await type(within('[data-testid="wsp-catalog-refresh-datasource"]'), 'dwh')
    await click(within('[data-testid="wsp-catalog-refresh-go"]'))

    const read = server.asked.find((a) => a.path === 'POST /api/workspace-catalog/ws-1/dw-build/gateway-read')
    expect(read?.body).toEqual({ gateway: 'gw-1', connection: 'warehouse', data_source: 'dwh' })
    expect(q(within('[data-testid="wsp-catalog-refreshed"]'))?.textContent).toBe('Read 1 table and 2 columns of warehouse from the data source dwh on edge. No row was read.')
    expect(q(within('[data-testid="wsp-refresh-summary"]'))?.textContent).toBe('1 table added')
    expect(draftText).toBeUndefined()

    await click(within('[data-testid="wsp-refresh-accept"]'))
    expect(q('[data-testid="wsp-catalog-unsaved"]')).not.toBeNull()
    const accepted = readCatalogCache(draftText!)
    expect(accepted.cache!.connections.warehouse!.read).toEqual({ server: 'edge', product: 'gateway', channel: null, at: '2026-11-23T10:00:00Z' })
    expect(accepted.cache!.connections.warehouse!.tables[0]!.columns.map((c) => c.name)).toEqual(['id', 'placed_at'])
    expect(q(within('[data-testid="wsp-refresh-difference"]'))).toBeNull()

    // nothing was written on the server, and the Workspace server was asked for neither a read nor its paired targets
    expect(server.asked.filter((a) => a.path.startsWith('PUT '))).toEqual([])
    expect(server.asked.filter((a) => a.path.includes('/catalog/') || a.path.includes('publish-targets'))).toEqual([])
  })

  it('asks before it reads a data source made by hand, then reads it with adopt', async () => {
    await mount()
    await type(within('[data-testid="wsp-catalog-refresh-datasource"]'), 'legacy')
    await click(within('[data-testid="wsp-catalog-refresh-go"]'))
    expect(q(within('[data-testid="wsp-catalog-adopt"]'))?.dataset.dataSource).toBe('legacy')
    expect(q(within('[data-testid="wsp-refresh-difference"]'))).toBeNull()

    await click(within('[data-testid="wsp-catalog-adopt-confirm"]'))
    const reads = server.asked.filter((a) => a.path === 'POST /api/workspace-catalog/ws-1/dw-build/gateway-read').map((a) => a.body)
    expect(reads).toEqual([{ gateway: 'gw-1', connection: 'warehouse', data_source: 'legacy' }, { gateway: 'gw-1', connection: 'warehouse', data_source: 'legacy', adopt: true }])
    expect(q(within('[data-testid="wsp-catalog-adopt"]'))).toBeNull()
    expect(q(within('[data-testid="wsp-refresh-summary"]'))?.textContent).toBe('1 table added')
  })
})
