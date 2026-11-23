// @vitest-environment happy-dom
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { MemoryRouter, useLocation } from 'react-router-dom'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { WorkspaceModule } from './WorkspaceModule'
import { registerDesigner, unregisterDesigner, designerPath, type DesignerProps } from './designers'
import { useDraft, useDraftFile } from './draft'
import { placeOf } from './pages/WorkspacePage'
import { Link } from '@dbdatakit/shell'

;(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true

/** A Workspace server in memory: two projects (a kind with a Designer, an app), one draft each, files read and written. */
function fakeServer() {
  const files: Record<string, Record<string, string>> = {
    'ws-t': { 'things/a.txt': 'alpha\n', 'notes.txt': 'n\n' },
    'ws-a': { 'app.yaml': 'name: a\n' },
  }
  const writes: string[] = []
  const reply = (body: unknown, status = 200) => Promise.resolve(new Response(status === 204 ? null : JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } }))
  const fetcher = vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input), 'http://localhost')
    const path = decodeURIComponent(url.pathname)
    if (path === '/api/auth/status') return reply({ authenticated: true, role: 'Admin', name: 'ana' })
    if (path === '/api/v1/projects') return reply({ projects: [{ id: 'things', kind: 'testkind', title: null, created: '', workspaces: 1 }, { id: 'site', kind: 'app', title: null, created: '', workspaces: 1 }] })
    const m = /^\/api\/v1\/projects\/([^/]+)\/workspaces(?:\/([^/]+)(?:\/(.*))?)?$/.exec(path)
    if (!m) return reply({ error: 'no' }, 404)
    const [, project, id, rest] = m
    if (!id) return reply({ workspaces: [{ id: project === 'things' ? 'ws-t' : 'ws-a', project, name: 'edit', branch: 'workspace/edit', base: 'abc', created: '', owner: 'ana', mine: true }] })
    const mine = files[id]
    if (rest === 'files') return reply({ files: Object.keys(mine).sort().map((p) => ({ path: p, size: mine[p].length })), truncated: false, max_file_bytes: 1 })
    if (rest?.startsWith('files/')) {
      const p = rest.slice('files/'.length)
      if (init?.method === 'PUT') { mine[p] = (JSON.parse(String(init.body)) as { content: string }).content; writes.push(p); return reply({ path: p, created: false, size: 1 }) }
      return p in mine ? reply({ path: p, content: mine[p] }) : reply({ error: 'none' }, 404)
    }
    if (rest === 'check') return reply({ kind: 'x', ok: true, diagnostics: [] })
    if (rest === 'changes') return reply({ changes: [], uncommitted: false })
    if (rest === 'history') return reply({ commits: [] })
    if (rest === 'events') return reply({ events: [] })
    return reply({ error: 'no' }, 404)
  })
  return { files, writes, fetcher }
}

/** A test kind's Designer: an entry per file under `things/`, and a page that shows the file and changes it without saving. */
function TestNav({ project, id }: DesignerProps) {
  const d = useDraft()
  return <>{d.paths.filter((p) => p.startsWith('things/')).map((p) => <Link key={p} to={designerPath(project, id, p)} data-testid="t-entry" data-path={p}>{p}</Link>)}</>
}
function TestPage({ rest }: DesignerProps) {
  const d = useDraft()
  const f = useDraftFile(rest || 'things/a.txt')
  return (
    <div data-testid="t-page" data-text={f.text ?? ''} data-dirty={f.dirty}>
      <button type="button" data-testid="t-change" onClick={() => d.edit(f.path, 'changed in the Designer\n')}>change</button>
    </div>
  )
}

let root: Root
let host: HTMLDivElement
let where = ''
function Where() { where = useLocation().pathname; return null }

async function mount(at: string) {
  host = document.createElement('div')
  document.body.appendChild(host)
  root = createRoot(host)
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  await act(async () => {
    root.render(<QueryClientProvider client={client}><MemoryRouter initialEntries={[at]}><Where /><WorkspaceModule /></MemoryRouter></QueryClientProvider>)
  })
  await settle()
}
const settle = async () => { for (let i = 0; i < 10; i++) await act(async () => { await new Promise((r) => setTimeout(r, 5)) }) }
const q = (sel: string) => host.querySelector(sel) as HTMLElement | null
const click = async (sel: string) => { const el = q(sel); if (!el) throw new Error(`no ${sel} in ${host.innerHTML.slice(0, 2000)}`); await act(async () => { el.click() }); await settle() }

describe('the two areas of a draft', () => {
  let server: ReturnType<typeof fakeServer>
  beforeEach(() => {
    server = fakeServer()
    vi.stubGlobal('fetch', server.fetcher)
    registerDesigner({ kind: 'testkind', title: 'Designer', Navigation: TestNav, Page: TestPage })
  })
  afterEach(() => { if (root) act(() => root.unmount()); host?.remove(); unregisterDesigner('testkind'); vi.unstubAllGlobals() })

  it('reads the area and the place from the address', () => {
    expect(placeOf('files/config/a b.yaml')).toEqual({ area: 'files', file: 'config/a b.yaml', rest: '' })
    expect(placeOf('files')).toEqual({ area: 'files', file: null, rest: '' })
    expect(placeOf('designer/replications/orders')).toEqual({ area: 'designer', file: null, rest: 'replications/orders' })
    expect(placeOf('')).toEqual({ area: null, file: null, rest: '' })
  })

  it('opens a draft of a kind with a Designer on it, with the kind\'s entries under Designer', async () => {
    await mount('/things/ws-t')
    expect(where).toBe('/things/ws-t/designer')
    expect(q('[data-testid="wsp-page"]')?.dataset.area).toBe('designer')
    expect(q('[data-testid="wsp-nav-designer"]')).not.toBeNull()
    expect(host.querySelectorAll('[data-testid="t-entry"]').length).toBe(1)
    expect(q('[data-testid="t-page"]')?.dataset.text).toBe('alpha\n')
    // Files' entries are there too: the tree
    expect(q('[data-testid="wsp-file"][data-path="notes.txt"]')).not.toBeNull()
  })

  it('shows only Files for a kind without a Designer, and no Designer entries', async () => {
    await mount('/site/ws-a')
    expect(where).toBe('/site/ws-a/files')
    expect(q('[data-testid="wsp-nav-designer"]')).toBeNull()
    expect(q('[data-testid="wsp-area-files"]')).not.toBeNull()
    await act(async () => { root.unmount() })
    host.remove()
    await mount('/site/ws-a/designer')
    expect(where).toBe('/site/ws-a/files')
  })

  it('keeps an unsaved edit across a click from one area to the other, and saves it only on Save', async () => {
    await mount('/things/ws-t/designer/things/a.txt')
    await click('[data-testid="t-change"]')
    expect(q('[data-testid="t-page"]')?.dataset.dirty).toBe('true')
    expect(q('[data-testid="wsp-unsaved"]')?.dataset.count).toBe('1')
    // into Files: the tree marks the file, its editor says not saved, nothing was written
    await click('[data-testid="wsp-file"][data-path="things/a.txt"]')
    expect(where).toBe('/things/ws-t/files/things/a.txt')
    expect(q('[data-testid="wsp-file"][data-path="things/a.txt"]')?.dataset.dirty).toBe('true')
    expect(q('[data-testid="wsp-dirty"]')).not.toBeNull()
    expect(server.writes).toEqual([])
    // and back by the Designer's entry: still the edit, still not saved
    await click('[data-testid="t-entry"]')
    expect(where).toBe('/things/ws-t/designer/things/a.txt')
    expect(q('[data-testid="t-page"]')?.dataset.text).toBe('changed in the Designer\n')
    expect(server.writes).toEqual([])
    // the chrome's Save writes it
    await click('[data-testid="wsp-save-all"]')
    expect(server.writes).toEqual(['things/a.txt'])
    expect(server.files['ws-t']['things/a.txt']).toBe('changed in the Designer\n')
    expect(q('[data-testid="wsp-unsaved"]')).toBeNull()
  })

  it('opens a tab of Files by its address, and an old `?tab=` address lands in Files', async () => {
    await mount('/things/ws-t?tab=history')
    expect(where).toBe('/things/ws-t/files')
    expect(q('[data-testid="wsp-tab-history"]')?.getAttribute('aria-selected')).toBe('true')
  })
})
