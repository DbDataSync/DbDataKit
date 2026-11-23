// @vitest-environment happy-dom
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { MemoryRouter, useLocation } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ModuleProvider } from '@dbdatakit/shell'
import { BuildsPanel } from './BuildsPanel'
import { useRunnerVersion } from '../deploys'

;(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true

interface Call { method: string; path: string; body: unknown }

/**
 * The console with a Workspace server `ws-1` (its relay is `/relay/ws-1`), a Runner `jobs` and a Gateway `edge`, and a Sync project `orders` with one draft and one
 * build that succeeded. `/api/deploys` deploys and lists what was deployed, as the console does; every call is kept, so a test can say which routes the panel used.
 */
function fakeConsole() {
  const calls: Call[] = []
  const deploys: Record<string, unknown>[] = []
  const reply = (body: unknown, status = 200) => Promise.resolve(new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } }))
  const build = { id: 'b1', workspace: 'd1', status: 'succeeded', commit: null, requested_by: 'ana', message: null, requested: '2026-11-23T10:00:00Z', started: null, finished: null }
  const fetcher = vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input), 'http://localhost')
    const method = init?.method ?? 'GET'
    const body = init?.body ? JSON.parse(String(init.body)) : null
    calls.push({ method, path: url.pathname + url.search, body })
    const ws = '/relay/ws-1/api/v1/projects/orders/workspaces/d1/builds'
    if (url.pathname === '/api/instances') return reply([
      { id: 'ws-1', name: 'Workspace', product: 'dbdataworkspace', reachable: true },
      { id: 'r-1', name: 'jobs', product: 'dbdatarunner', reachable: true },
      { id: 'g-1', name: 'edge', product: 'dbdatagateway', reachable: true },
    ])
    if (url.pathname === '/api/deploys' && method === 'GET') return reply({ deploys })
    if (url.pathname === '/api/deploys' && method === 'POST') {
      const record = { id: `d${deploys.length + 1}`, workspace_server: 'ws-1', project: 'orders', workspace: 'd1', build: 'b1', item: 'package', kind: 'sync', target: 'r-1', target_name: 'jobs', product: 'runner', route: 'package',
        name: 'orders', channel: body.channel, version: 1, mode: 'stage', signature: 'verified', deployed_by: 'ana', deployed_at: '2026-11-23T10:05:00Z', outcome: 'ok', error: null }
      deploys.unshift(record)
      return reply({ steps: ['Read build b1 of orders/d1 from Workspace.'], deployed: { package: 'orders', channel: body.channel, mode: 'stage', version: 1, changed: true, target: 'jobs', runner: 'https://jobs:8495', signature: 'verified' }, deploy: record })
    }
    if (url.pathname === '/relay/ws-1/api/v1/projects') return reply({ projects: [{ id: 'orders', kind: 'sync', title: null, created: '', workspaces: 1 }] })
    if (url.pathname === '/relay/ws-1/api/v1/project-kinds') return reply({ kinds: [{ name: 'sync', title: 'Sync', description: '', buildable: false, produces: true, publishes: [{ product: 'runner', what: 'package', description: '' }] }] })
    if (url.pathname === ws) return reply({ builds: [build] })
    if (url.pathname === `${ws}/b1`) return reply({ build, result: { status: 'succeeded', message: 'Made.', diagnostics: [], uncommitted_changes: false, commit: null, seconds: 1, kind: 'sync', info: { package: 'orders' } }, publishes: [] })
    return reply({ error: 'There is no such call.' }, 404)
  })
  return { calls, deploys, fetcher }
}

let root: Root
let host: HTMLDivElement
let where = ''
function Where() { const l = useLocation(); where = l.pathname + l.search; return null }
/** What another screen of the draft (a replication's rail) reads of the version on a Runner, mounted beside the tab. */
function RunnerVersion() { const v = useRunnerVersion('orders', 'd1'); return <span data-testid="runner-version" data-version={v.data?.version ?? 'none'} /> }

async function mount(at: string) {
  host = document.createElement('div')
  document.body.appendChild(host)
  root = createRoot(host)
  await act(async () => {
    root.render(
      <ModuleProvider info={{ basePath: '', apiBase: '/relay/ws-1', host: { nav: [], serverId: 'ws-1', serverName: 'Workspace' } }}>
        <MemoryRouter initialEntries={[at]}><Where /><RunnerVersion /><BuildsPanel project="orders" id="d1" canWrite /></MemoryRouter>
      </ModuleProvider>,
    )
  })
  await settle()
}
const settle = async () => { for (let i = 0; i < 12; i++) await act(async () => { await new Promise((r) => setTimeout(r, 5)) }) }
const q = (sel: string) => host.querySelector(sel) as HTMLElement | null
const click = async (sel: string) => { const el = q(sel); if (!el) throw new Error(`no ${sel} in ${host.innerHTML.slice(0, 3000)}`); await act(async () => { el.click() }); await settle() }
const fill = async (sel: string, value: string) => {
  const el = q(sel) as HTMLInputElement | null
  if (!el) throw new Error(`no ${sel} in ${host.innerHTML.slice(0, 3000)}`)
  await act(async () => {
    Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!.call(el, value)
    el.dispatchEvent(new Event('input', { bubbles: true }))
  })
  await settle()
}

describe('publishing a build from the Builds tab', () => {
  let console_: ReturnType<typeof fakeConsole>
  beforeEach(() => { console_ = fakeConsole(); vi.stubGlobal('fetch', console_.fetcher) })
  afterEach(() => { act(() => root.unmount()); host.remove(); vi.unstubAllGlobals() })

  it('stages a Sync build on a Runner of the console with its slot bound, through the console\'s deploy, and calls no publish route of the Workspace server', async () => {
    await mount('/?build=b1')
    // the targets are the console's Runners and Gateways the kind goes to: a Sync set goes to a Runner, so the Gateway is not offered and the one Runner is chosen
    const options = [...host.querySelectorAll('[data-testid="wsp-publish-target"] option')].map((o) => (o as HTMLOptionElement).value)
    expect(options).toEqual(['', 'r-1'])
    expect(q('[data-testid="wsp-publish-form"]')?.dataset.product).toBe('runner')
    await fill('[data-testid="wsp-publish-channel"]', 'dev')
    await fill('[data-testid="wsp-publish-bind"]', 'source-db=erp')
    await click('[data-testid="wsp-publish"]')

    const posted = console_.calls.filter((c) => c.method === 'POST')
    expect(posted).toEqual([{ method: 'POST', path: '/api/deploys', body: { workspaceServer: 'ws-1', project: 'orders', workspace: 'd1', build: 'b1', target: 'r-1', channel: 'dev', bind: { 'source-db': 'erp' } } }])
    expect(console_.calls.filter((c) => /\/builds\/[^/]+\/(publish|preview-data)$/.test(c.path) || c.path.includes('publish-targets'))).toEqual([])
    expect(console_.calls.every((c) => c.path.startsWith('/api/') ? ['/api/instances', '/api/deploys'].some((p) => c.path.startsWith(p)) : c.path.startsWith('/relay/ws-1/'))).toBe(true)

    // what the Runner answered, and the console's record of the deploy, which the tab reads back
    expect(q('[data-testid="wsp-published"]')?.dataset.version).toBe('1')
    expect(q('[data-testid="wsp-published-line"]')?.textContent).toBe('Staged orders on dev of https://jobs:8495 as version 1.')
    const record = q('[data-testid="wsp-deploy-record"]')
    expect(record?.dataset.outcome).toBe('ok')
    expect(record?.textContent).toContain('ana to jobs')
    expect(record?.textContent).toContain('Staged orders on dev of jobs as version 1.')
    expect(console_.calls.some((c) => c.method === 'GET' && c.path === '/api/deploys?workspace_server=ws-1&project=orders&workspace=d1&build=b1')).toBe(true)
    // the draft's other reads of its deploys are asked again too: what the replication's rail says a Runner has is the version just staged
    expect(q('[data-testid="runner-version"]')?.dataset.version).toBe('1')
  })

  it('opens the build the address names, and puts the build chosen in the address', async () => {
    await mount('/?tab=builds&build=b1')
    expect(q('[data-testid="wsp-build-detail"]')?.dataset.build).toBe('b1')
    await click('[data-testid="wsp-build-row"]')
    expect(where).toBe('/?tab=builds&build=b1')
  })
})
