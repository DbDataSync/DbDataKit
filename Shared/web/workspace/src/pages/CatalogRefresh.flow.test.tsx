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

const CACHE = [
  'schema: catalog-cache/2', "generated_at: '2026-11-20T09:00:00Z'", 'connections:',
  "  'crm':", "    engine: 'sqlserver'", "    read: {server: 'runners-eu', product: 'runner', channel: 'dev', at: '2026-11-20T09:00:00Z'}",
  '    databases:', "      - {name: 'crm', schemas: ['dbo']}", '    tables:',
  "      - database: 'crm'", "        schema: 'dbo'", "        name: 'accounts'", '        kind: table', '        columns:',
  "          - {name: 'id', type: 'int', nullable: false, key: true}", '',
].join('\n')
/** What the console answers for a read of `dbo.accounts`: one column more, read later. */
const ANSWER = CACHE.replace("'2026-11-20T09:00:00Z'\nconnections", "'2026-11-22T10:00:04Z'\nconnections").replace("at: '2026-11-20T09:00:00Z'", "at: '2026-11-22T10:00:04Z'")
  + "          - {name: 'segment', type: 'nvarchar(20)', nullable: true}\n"

/** The Workspace server through the relay (`/relay/ws-1`) and the console's own routes, in memory. */
function fakeConsole() {
  const asked: { path: string; body: unknown }[] = []
  const writes: string[] = []
  const reply = (body: unknown, status = 200) => Promise.resolve(new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } }))
  const fetcher = vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input), 'http://localhost')
    const path = decodeURIComponent(url.pathname)
    asked.push({ path: `${init?.method ?? 'GET'} ${path}${url.search}`, body: init?.body ? JSON.parse(String(init.body)) : null })
    if (init?.method === 'PUT') writes.push(path)
    const draft = '/relay/ws-1/api/v1/projects/crm-to-dw/workspaces/ws'
    if (path === `${draft}/files`) return reply({ files: [{ path: 'catalog-cache.yaml', size: 1 }, { path: 'config/connections/crm.yaml', size: 1 }], truncated: false, max_file_bytes: 1 })
    if (path === `${draft}/files/catalog-cache.yaml`) return reply({ path: 'catalog-cache.yaml', content: CACHE })
    if (path === '/api/instances') return reply([{ id: 'r-1', name: 'runners-eu', product: 'dbdatarunner' }, { id: 'ws-1', name: 'Workspace', product: 'dbdataworkspace' }])
    if (path === '/api/workspace-catalog/ws-1/crm-to-dw/channels') {
      return reply({
        package: 'crm-to-dw', connection: 'crm', slot: 'crm', runner: 'r-1', runner_name: 'runners-eu', default: 'dev',
        channels: [{ channel: 'dev', production: false, bound: true, bound_to: 'crm_dev' }, { channel: 'prod', production: true, bound: true, bound_to: 'crm' }],
      })
    }
    const base = { run_id: 'run-1', link: '/servers/r-1/runs?run=run-1', runner: 'r-1', runner_name: 'runners-eu', connection: 'crm' }
    if (path === '/api/workspace-catalog/ws-1/crm-to-dw/read') {
      const body = JSON.parse(String(init!.body)) as { channel: string }
      return reply({ ...base, state: 'running', channel: body.channel, production: body.channel === 'prod' }, 202)
    }
    if (path === '/api/workspace-catalog/ws-1/crm-to-dw/read/run-1') {
      const channel = url.searchParams.get('channel')!
      return reply({ ...base, state: 'done', channel, production: channel === 'prod', cache: ANSWER.replace("channel: 'dev'", `channel: '${channel}'`), tables: 1, columns: 2 })
    }
    return reply({ error: `no ${path}` }, 404)
  })
  return { asked, writes, fetcher }
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
const click = async (sel: string) => { const el = q(sel); if (!el) throw new Error(`no ${sel} in ${host.innerHTML.slice(0, 3000)}`); await act(async () => { el.click() }); await settle() }
const choose = async (sel: string, value: string) => {
  const el = q(sel) as HTMLSelectElement
  await act(async () => { el.value = value; el.dispatchEvent(new Event('change', { bubbles: true })) })
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
          <DraftProvider project="crm-to-dw" id="ws" kind="sync" canEdit>
            <Frame />
            <CatalogPanel project="crm-to-dw" id="ws" onOpen={() => {}} kind="sync" />
          </DraftProvider>
        </ModuleProvider>
      </MemoryRouter>,
    )
  })
  await settle()
}

describe('Refresh of a table on the Catalog tab', () => {
  let server: ReturnType<typeof fakeConsole>
  beforeEach(() => { server = fakeConsole(); vi.stubGlobal('fetch', server.fetcher); draftText = undefined })
  afterEach(() => { act(() => root.unmount()); host.remove(); vi.unstubAllGlobals() })

  it('offers dev first, follows a read that takes long, shows "1 column added", and accepted the cache is an unsaved edit read from dev', async () => {
    await mount()
    expect(q('[data-testid="wsp-channel-picker"]')?.dataset.channel).toBe('dev')
    await choose('[data-testid="wsp-runner-refresh-table"]', 'dbo.accounts')
    await click('[data-testid="wsp-refresh-go"]')

    const read = server.asked.find((a) => a.path === 'POST /api/workspace-catalog/ws-1/crm-to-dw/read')
    expect(read?.body).toEqual({ runner: 'r-1', channel: 'dev', connection: 'crm', schema: 'dbo', table: 'accounts' })
    expect(server.asked.some((a) => a.path.startsWith('GET /api/workspace-catalog/ws-1/crm-to-dw/read/run-1?runner=r-1&channel=dev&connection=crm'))).toBe(true)
    expect(q('[data-testid="wsp-refresh-summary"]')?.textContent).toBe('1 column added')
    expect(draftText).toBeUndefined()

    await click('[data-testid="wsp-refresh-accept"]')
    expect(q('[data-testid="wsp-catalog-unsaved"]')).not.toBeNull()
    const accepted = readCatalogCache(draftText!)
    expect(accepted.cache!.connections.crm!.read).toEqual({ server: 'runners-eu', product: 'runner', channel: 'dev', at: '2026-11-22T10:00:04Z' })
    expect(accepted.cache!.connections.crm!.tables[0]!.columns.map((c) => c.name)).toEqual(['id', 'segment'])
    expect(q('[data-testid="wsp-catalog-read"]')?.textContent).toContain('in dev at 2026-11-22T10:00:04Z')
    // nothing was written to the draft on the server: the person saves it
    expect(server.writes).toEqual([])
    // and nothing was asked of the Workspace server for the read
    expect(server.asked.filter((a) => a.path.includes('/relay/ws-1/') && a.path.includes('/catalog/'))).toEqual([])
  })

  it('warns when production is picked and still reads', async () => {
    await mount()
    await choose('[data-testid="wsp-channel-channel"]', 'prod')
    expect(q('[data-testid="wsp-channel-production"]')?.textContent).toContain('This reads production')
    expect((q('[data-testid="wsp-refresh-go"]') as HTMLButtonElement).disabled).toBe(false)
    await click('[data-testid="wsp-refresh-go"]')
    expect(server.asked.find((a) => a.path === 'POST /api/workspace-catalog/ws-1/crm-to-dw/read')?.body).toMatchObject({ channel: 'prod' })
    expect(q('[data-testid="wsp-refresh-summary"]')?.textContent).toBe('1 column added')
  })
})
