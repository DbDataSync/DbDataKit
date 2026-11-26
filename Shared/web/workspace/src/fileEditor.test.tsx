// @vitest-environment happy-dom
import { act, useEffect, useRef } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { MemoryRouter, useLocation } from 'react-router-dom'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { CodeEditorProps } from '@dbdatakit/shell'
import { WorkspaceModule } from './WorkspaceModule'

;(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true

// Monaco does not run here. The stand-in keeps what Monaco does that matters to the frame: a value set from outside (`setValue`) is reported through
// `onChange` as a change of the content, just as a keystroke is.
vi.mock('@dbdatakit/shell', async (importOriginal) => {
  const shell = await importOriginal<typeof import('@dbdatakit/shell')>()
  function CodeEditor({ value, onChange, testId }: CodeEditorProps) {
    const held = useRef(value)
    const report = useRef(onChange)
    report.current = onChange
    useEffect(() => { if (held.current !== value) { held.current = value; report.current(value) } }, [value])
    return <textarea data-testid={testId} value={value} onChange={(e) => { held.current = e.target.value; onChange(e.target.value) }} />
  }
  return { ...shell, CodeEditor }
})

/**
 * A Workspace server in memory with one app project and one draft: files read, written, moved and deleted. Each answer takes a moment, as over a
 * network, so the page draws the new file list before the editor is closed, as it does in a browser.
 */
function fakeServer() {
  // the check of the draft answers when the test lets it (`hold`), as a check that is slow on a loaded machine
  let held: Promise<void> | null = null
  let release = () => {}
  const hold = () => { held = new Promise<void>((done) => { release = () => { held = null; done() } }) }
  const files: Record<string, string> = { 'views/orders.yaml': 'title: Orders\n', 'notes.txt': 'n\n' }
  const reply = (body: unknown, status = 200) => new Promise<Response>((done) => setTimeout(() => done(new Response(status === 204 ? null : JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })), 3))
  const fetcher = vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input), 'http://localhost')
    const path = decodeURIComponent(url.pathname)
    if (path === '/api/auth/status') return reply({ authenticated: true, role: 'Admin', name: 'ana' })
    if (path === '/api/v1/projects') return reply({ projects: [{ id: 'site', kind: 'app', title: null, created: '', workspaces: 1 }] })
    const m = /^\/api\/v1\/projects\/([^/]+)\/workspaces(?:\/([^/]+)(?:\/(.*))?)?$/.exec(path)
    if (!m) return reply({ error: 'no' }, 404)
    const [, project, id, rest] = m
    if (!id) return reply({ workspaces: [{ id: 'ws-a', project, name: 'edit', branch: 'workspace/edit', base: 'abc', created: '', owner: 'ana', mine: true }] })
    if (rest === 'files') return reply({ files: Object.keys(files).sort().map((p) => ({ path: p, size: files[p].length })), truncated: false, max_file_bytes: 1 })
    if (rest === 'move') {
      const { from, to } = JSON.parse(String(init?.body)) as { from: string; to: string }
      files[to] = files[from]
      delete files[from]
      return reply({ from, to })
    }
    if (rest?.startsWith('files/')) {
      const p = rest.slice('files/'.length)
      if (init?.method === 'PUT') { files[p] = (JSON.parse(String(init.body)) as { content: string }).content; return reply({ path: p, created: false, size: 1 }) }
      if (init?.method === 'DELETE') { delete files[p]; return reply(null, 204) }
      return p in files ? reply({ path: p, content: files[p] }) : reply({ error: 'none' }, 404)
    }
    if (rest === 'check') return (held ?? Promise.resolve()).then(() => reply({ kind: 'x', ok: true, diagnostics: [] }))
    if (rest === 'changes') return reply({ changes: [], uncommitted: false })
    if (rest === 'history') return reply({ commits: [] })
    if (rest === 'events') return reply({ events: [] })
    return reply({ error: 'no' }, 404)
  })
  return { files, fetcher, hold, release: () => release() }
}

let root: Root
let host: HTMLDivElement
let where = ''
let query = ''
function Where() { const at = useLocation(); where = at.pathname; query = at.search; return null }

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
const fill = async (sel: string, value: string) => {
  const el = q(sel) as HTMLInputElement | null
  if (!el) throw new Error(`no ${sel}`)
  await act(async () => {
    Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!.call(el, value)
    el.dispatchEvent(new Event('input', { bubbles: true }))
  })
  await settle()
}
const tree = () => [...host.querySelectorAll('[data-testid="wsp-file"]')].map((e) => (e as HTMLElement).dataset.path).sort()

describe('a file moved or deleted in the Files area', () => {
  let server: ReturnType<typeof fakeServer>
  beforeEach(() => { server = fakeServer(); vi.stubGlobal('fetch', server.fetcher) })
  afterEach(() => { if (root) act(() => root.unmount()); host?.remove(); vi.unstubAllGlobals() })

  it('leaves the tree with the new path only after Move', async () => {
    await mount('/site/ws-a/files/views/orders.yaml')
    expect((q('[data-testid="wsp-code"]') as HTMLTextAreaElement | null)?.value).toBe('title: Orders\n')
    await click('[data-testid="wsp-move-open"]')
    await fill('[data-testid="wsp-move-target"]', 'views/customers.yaml')
    await click('[data-testid="wsp-move-go"]')
    await vi.waitFor(() => expect(where).toBe('/site/ws-a/files/views/customers.yaml'))
    await settle()
    expect(server.files).toEqual({ 'views/customers.yaml': 'title: Orders\n', 'notes.txt': 'n\n' })
    expect(tree()).toEqual(['notes.txt', 'views/customers.yaml'])
    expect(q('[data-testid="wsp-unsaved"]')).toBeNull()
    expect((q('[data-testid="wsp-code"]') as HTMLTextAreaElement | null)?.value).toBe('title: Orders\n')
  })

  it('leaves no trace of the file in the tree after Delete', async () => {
    await mount('/site/ws-a/files/views/orders.yaml')
    await click('[data-testid="wsp-delete"]')
    await click('[data-testid="wsp-delete-confirm"]')
    await vi.waitFor(() => expect(where).toBe('/site/ws-a/files'))
    await settle()
    expect(server.files).toEqual({ 'notes.txt': 'n\n' })
    expect(tree()).toEqual(['notes.txt'])
    expect(q('[data-testid="wsp-unsaved"]')).toBeNull()
  })

  it('keeps the tab picked while a Delete was still refreshing', async () => {
    await mount('/site/ws-a/files/views/orders.yaml?tab=changes')
    server.hold()
    await click('[data-testid="wsp-delete"]')
    await click('[data-testid="wsp-delete-confirm"]')
    // the file has left the tree, the refresh of the check has not come back: the person goes to Builds
    await vi.waitFor(() => expect(tree()).toEqual(['notes.txt']))
    await click('[data-testid="wsp-tab-builds"]')
    expect(query).toBe('?tab=builds')
    server.release()
    await vi.waitFor(() => expect(where).toBe('/site/ws-a/files'))
    await settle()
    expect(query).toBe('?tab=builds')
    expect(q('[data-testid="wsp-builds"]')).not.toBeNull()
  })
})
