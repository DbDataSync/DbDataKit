// @vitest-environment happy-dom
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { MemoryRouter } from 'react-router-dom'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { ModuleProvider } from '@dbdatakit/shell'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { DraftProvider, useDraft } from '../draft'
import { SyncProvisioningPanel } from './SyncProvisioningPanel'

;(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true

const TASK = 'config/replications/orders/task.yaml'
const MAPPING = 'config/replications/orders/table-mappings/orders.yaml'

/** The Workspace server through the relay, in memory: the draft's files and the plan it answers; every request is kept. */
function fakeWorkspace() {
  const asked: { path: string; body: unknown }[] = []
  const reply = (body: unknown, status = 200) => Promise.resolve(new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } }))
  const fetcher = vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input), 'http://localhost')
    const path = decodeURIComponent(url.pathname)
    asked.push({ path: `${init?.method ?? 'GET'} ${path}`, body: init?.body ? JSON.parse(String(init.body)) : null })
    const draft = '/relay/ws-1/api/v1/projects/plant/workspaces/ws'
    if (path === `${draft}/files`) return reply({ files: [{ path: TASK, size: 1 }, { path: MAPPING, size: 1 }, { path: 'README.md', size: 1 }], truncated: false, max_file_bytes: 1 })
    if (path === `${draft}/files/${MAPPING}`) return reply({ path: MAPPING, content: 'name: orders\n' })
    if (path === `${draft}/provisioning-plan`) {
      return reply({ ok: true, diagnostics: [], data: { replication: 'orders', cache_generated_at: '2026-11-12T10:00:00Z', cache_server: 'runners-eu', mappings: [] } })
    }
    return reply({ error: `no ${path}` }, 404)
  })
  return { asked, fetcher }
}

let root: Root
let host: HTMLDivElement

const settle = async (n = 15) => { for (let i = 0; i < n; i++) await act(async () => { await new Promise((r) => setTimeout(r, 5)) }) }
const q = (sel: string) => host.querySelector(sel) as HTMLElement | null
const click = async (sel: string) => { const el = q(sel); if (!el) throw new Error(`no ${sel} in ${host.innerHTML.slice(0, 3000)}`); await act(async () => { el.click() }); await settle() }

/** An edit made in the other area of the project: the frame holds it, not saved. */
function EditMapping() {
  const draft = useDraft()
  return (
    <>
      <button type="button" data-testid="edit-mapping" onClick={() => draft.edit(MAPPING, 'name: orders\nprovisioning:\n  createTargetTableIfMissing: false\n')}>edit</button>
      <button type="button" data-testid="edit-readme" onClick={() => draft.edit('README.md', 'notes')}>readme</button>
    </>
  )
}

async function mount() {
  host = document.createElement('div')
  document.body.appendChild(host)
  root = createRoot(host)
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  await act(async () => {
    root.render(
      <MemoryRouter>
        <ModuleProvider info={{ basePath: '/servers/ws-1', apiBase: '/relay/ws-1' }}>
          <QueryClientProvider client={client}>
            <DraftProvider project="plant" id="ws" kind="sync" canEdit>
              <EditMapping />
              <SyncProvisioningPanel project="plant" id="ws" paths={[TASK, MAPPING]} />
            </DraftProvider>
          </QueryClientProvider>
        </ModuleProvider>
      </MemoryRouter>,
    )
  })
  await settle()
}

describe('the Files area\'s Provisioning tab', () => {
  let server: ReturnType<typeof fakeWorkspace>
  beforeEach(() => { server = fakeWorkspace(); vi.stubGlobal('fetch', server.fetcher) })
  afterEach(() => { act(() => root.unmount()); host.remove(); vi.unstubAllGlobals() })

  const plans = () => server.asked.filter((a) => a.path.endsWith('/provisioning-plan'))

  it('plans the saved files when nothing is edited, and the unsaved edits under config/ once there are some', async () => {
    await mount()
    await click('[data-testid="wsp-prov-run"]')
    expect(plans().at(-1)?.body).toEqual({ replication: 'orders' })
    expect(q('[data-testid="wsp-prov-basis"]')?.textContent).toBe('Planned from the saved files.')

    await click('[data-testid="edit-mapping"]')
    await click('[data-testid="edit-readme"]')
    await click('[data-testid="wsp-prov-run"]')
    expect(plans().at(-1)?.body).toEqual({ replication: 'orders', unsaved: { [MAPPING]: 'name: orders\nprovisioning:\n  createTargetTableIfMissing: false\n' } })
    expect(q('[data-testid="wsp-prov-basis"]')?.dataset.unsaved).toBe('1')
    expect(q('[data-testid="wsp-prov-basis"]')?.textContent).toContain('its one unsaved edit included')
  })
})
