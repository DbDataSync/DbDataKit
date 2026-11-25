// @vitest-environment happy-dom
import { act, useEffect } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { MemoryRouter } from 'react-router-dom'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ModuleProvider } from '@dbdatakit/shell'
import { DraftProvider, useDraft } from '@dbdatakit/workspace'
import { mappingPath } from './model'
import { MappingRowsPreview } from './mappings/MappingRowsPreview'
import { SegmentingStrategiesView } from './replication/SegmentingStrategiesCard'
import type { SegmentingStrategyConfig } from './types'

;(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true

/** A value of a row: the screen shows it, and no call but the answer's carries it. */
const ROW_VALUE = 'row-value-4410'

/** The console in memory: the draft's newest deploy to a Runner, a preview that takes longer than the console waits, and a trial answered at once. */
function fakeConsole(channel = 'dev') {
  const asked: { path: string; body: unknown }[] = []
  let looks = 0
  const reply = (body: unknown, status = 200) => Promise.resolve(new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } }))
  const fetcher = vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input), 'http://localhost')
    const path = decodeURIComponent(url.pathname)
    asked.push({ path: `${init?.method ?? 'GET'} ${path}`, body: init?.body ? JSON.parse(String(init.body)) : null })
    if (path === '/relay/ws-1/api/v1/projects/plant/workspaces/ws/files') return reply({ files: [], truncated: false, max_file_bytes: 1 })
    if (path === '/api/deploys') {
      return reply({ deploys: [{
        id: 'd-1', workspace_server: 'ws-1', project: 'plant', workspace: 'ws', build: 'b1', item: 'plant', kind: 'sync', target: 'r-1', target_name: 'runners-eu', product: 'runner', route: 'x',
        name: 'plant', channel, version: 4, mode: 'serve', signature: null, deployed_by: 'ana', deployed_at: '2026-11-24T10:00:00Z', outcome: 'ok', error: null, tasks: ['orders', 'preview', 'try-strategy'],
      }] })
    }
    const base = { runner: 'r-1', runner_name: 'runners-eu', channel, production: channel === 'prod', link: '/servers/r-1/runs?run=run-7' }
    if (path === '/api/deploys/d-1/runs' && init?.method === 'POST') {
      const body = JSON.parse(String(init.body)) as { task: string }
      if (body.task === 'preview') return reply({ ...base, run_id: 'run-7', state: 'running' }, 202)
      return reply({
        ...base, run_id: 'run-8', state: 'done',
        answer: { replication: 'orders', mapping: 'dbo.orders', strategy: 'by-month', kind: 'SourceSql', column: 'placed_at', readsADatabase: true,
          candidates: [{ label: '2026-10', column: 'placed_at', start: '2026-10-01', end: '2026-11-01', segment: '2026-10', selected: false }, { label: '2026-11', column: 'placed_at', start: '2026-11-01', end: '2026-12-01', segment: '2026-11', selected: true }] },
      })
    }
    if (path === '/api/deploys/d-1/runs/run-7/answer') {
      return ++looks < 2 ? reply({ ...base, run_id: 'run-7', state: 'running' }, 202) : reply({
        ...base, run_id: 'run-7', state: 'done',
        answer: { replication: 'orders', mapping: 'dbo.orders', connection: 'erp', source: 'sales.dbo.orders', columns: ['id', 'note'], rows: [[1, ROW_VALUE], [2, null]], more: true, limit: 20 },
      })
    }
    return reply({ error: `no ${path}` }, 404)
  })
  return { asked, fetcher }
}

let root: Root
let host: HTMLDivElement
const settle = async (n = 15) => { for (let i = 0; i < n; i++) await act(async () => { await new Promise((r) => setTimeout(r, 5)) }) }
const q = (sel: string) => host.querySelector(sel) as HTMLElement | null
const all = (sel: string) => [...host.querySelectorAll(sel)] as HTMLElement[]
const click = async (sel: string) => { const el = q(sel); if (!el) throw new Error(`no ${sel} in ${host.innerHTML.slice(0, 3000)}`); await act(async () => { el.click() }); await settle() }
const type = async (sel: string, value: string) => {
  const el = q(sel) as HTMLInputElement
  const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!
  await act(async () => { setter.call(el, value); el.dispatchEvent(new Event('input', { bubbles: true })) })
}

async function mount(node: React.ReactNode) {
  host = document.createElement('div')
  document.body.appendChild(host)
  root = createRoot(host)
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  await act(async () => {
    root.render(
      <MemoryRouter>
        <ModuleProvider info={{ basePath: '/servers/ws-1', apiBase: '/relay/ws-1' }}>
          <QueryClientProvider client={client}>
            <DraftProvider project="plant" id="ws" kind="sync" canEdit>{node}</DraftProvider>
          </QueryClientProvider>
        </ModuleProvider>
      </MemoryRouter>,
    )
  })
  await settle()
}

/** The mapping's file as the person is editing it: an unsaved edit of the draft, whose filter the version on the Runner does not have. */
const UNSAVED = 'schemaVersion: 1\nname: dbo.orders\nsources:\n- schema: dbo\n  table: orders\n  filter: "region = 2"\ntargets:\n- schema: dbo\n  table: orders\ncolumnMappings: []\n'

/** Puts the unsaved edit in the draft before the screen is drawn, as the mapping's form does when the person types. */
function WithUnsavedMapping({ children }: { children: React.ReactNode }) {
  const draft = useDraft()
  const path = mappingPath('orders', 'dbo.orders')
  useEffect(() => { draft.edit(path, UNSAVED) }, [])   // eslint-disable-line react-hooks/exhaustive-deps
  return draft.textOf(path) === UNSAVED ? <>{children}</> : null
}

const strategy: SegmentingStrategyConfig = { name: 'by-month', kind: 'SourceSql', sql: 'SELECT ... FROM dbo.orders', scriptName: null, parameters: {} }

describe('what a person waits for on a Runner: the rows of a mapping and the trial of a strategy', () => {
  let server: ReturnType<typeof fakeConsole>
  beforeEach(() => { server = fakeConsole(); vi.stubGlobal('fetch', server.fetcher) })
  afterEach(() => { act(() => root.unmount()); host.remove(); vi.unstubAllGlobals() })

  it('Preview rows starts the set\'s preview through the console, follows a long run, and shows the rows it answered', async () => {
    await mount(<WithUnsavedMapping><MappingRowsPreview replication="orders" mapping="dbo.orders" /></WithUnsavedMapping>)
    expect(q('[data-testid="mapping-rows-preview-version"]')?.textContent).toContain('as this mapping is in the draft with its unsaved edits, read on dev of runners-eu (version 4)')
    await click('[data-testid="mapping-rows-preview-go"]')
    // the mapping goes as the draft has it, the unsaved filter included
    expect(server.asked.find((a) => a.path === 'POST /api/deploys/d-1/runs')?.body).toEqual({
      task: 'preview', inputs: { replication: 'orders', mapping: 'dbo.orders', rows: '20', repo_dir: 'repo', mapping_text: UNSAVED }, answer: true,
    })
    expect(q('[data-testid="mapping-rows-preview-running"]')?.textContent).toContain('takes longer than 10 seconds')
    await act(async () => { await new Promise((r) => setTimeout(r, 2100)) })
    await settle()
    await act(async () => { await new Promise((r) => setTimeout(r, 2100)) })
    await settle()
    expect(q('[data-testid="mapping-rows-preview-summary"]')?.textContent).toBe('2 rows of sales.dbo.orders on erp, more follow.')
    expect(all('[data-testid="mapping-rows-preview-row"]').map((r) => r.textContent)).toEqual([`1${ROW_VALUE}`, '2NULL'])
    // the rows came in the answer only: nothing the screen sent holds them
    expect(server.asked.filter((a) => JSON.stringify(a.body ?? '').includes(ROW_VALUE))).toEqual([])
    expect(q('[data-testid="mapping-rows-preview-production"]')).toBeNull()
  }, 15_000)

  it('Try runs the strategy as it is being edited over a mapping and a column, and shows every candidate', async () => {
    await mount(<WithUnsavedMapping><SegmentingStrategiesView strategies={[strategy]} scripts={[]} onChange={() => {}} trial={{ project: 'plant', id: 'ws', replication: 'orders', mappings: ['dbo.orders', 'dbo.customers'] }} /></WithUnsavedMapping>)
    await click('[data-testid="edit-strategy-by-month"]')
    expect((q('[data-testid="try-strategy-button"]') as HTMLButtonElement).disabled).toBe(true)   // no column yet
    await type('[data-testid="try-strategy-column"]', 'placed_at')
    await settle()
    await click('[data-testid="try-strategy-button"]')
    const sent = server.asked.find((a) => a.path === 'POST /api/deploys/d-1/runs')?.body as { task: string; inputs: Record<string, string>; answer: boolean }
    expect(sent.task).toBe('try-strategy')
    expect(sent.answer).toBe(true)
    expect({ ...sent.inputs, strategy: JSON.parse(sent.inputs.strategy!) }).toEqual({
      replication: 'orders', mapping: 'dbo.orders', name: '', column: 'placed_at', repo_dir: 'repo', mapping_text: UNSAVED,
      strategy: { name: 'by-month', kind: 'SourceSql', sql: 'SELECT ... FROM dbo.orders', scriptName: null, parameters: {} },
    })
    expect(q('[data-testid="try-strategy-summary"]')?.textContent).toBe('2 candidates of by-month over placed_at, 1 selected.')
    expect(all('[data-testid="try-strategy-candidate"]').map((c) => c.dataset.selected)).toEqual(['false', 'true'])
  })

  it('warns that a read in production shows production\'s rows', async () => {
    server = fakeConsole('prod')
    vi.stubGlobal('fetch', server.fetcher)
    await mount(<MappingRowsPreview replication="orders" mapping="dbo.orders" />)
    expect(q('[data-testid="mapping-rows-preview-production"]')?.textContent).toContain('reads rows of production')
  })
})
