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
 * The console with a Workspace server `ws-1` (its relay is `/relay/ws-1`), a Runner `jobs` and a Gateway `edge`, and a Sync project `orders` (or, with `kind`, an app) with one draft and one
 * build that succeeded. `/api/deploys` deploys and lists what was deployed, as the console does; a preview of the instance `ghost` is refused in the Gateway's words. Every call is kept, so a test can say which routes the panel used.
 */
function fakeConsole(kind: 'sync' | 'app' = 'sync') {
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
    // serving in prod waits for the package's preflight: it found an error, so the version stays staged (409, the deploy kept) until the person confirms
    const preflight = { declared: true, run: '6f1c0f1e-0000-4000-8000-000000000001', link: '/servers/r-1/runs?run=6f1c0f1e-0000-4000-8000-000000000001', state: 'finished', outcome: 'succeeded', errors: 1, warnings: 1,
      findings: [{ severity: 'warning', message: 'marts.dim_store was changed outside dbdatabuild since it was deployed.', about: 'warehouse' }, { severity: 'error', message: 'marts.dim_region is not on the connection.', about: 'warehouse' }], activation: 'refused' }
    if (url.pathname === '/api/deploys' && method === 'POST' && body.channel === 'prod' && body.serve) {
      const record = { id: 'd-prod', workspace_server: 'ws-1', project: 'orders', workspace: 'd1', build: 'b1', item: 'package', kind: 'sync', target: 'r-1', target_name: 'jobs', product: 'runner', route: 'package',
        name: 'orders', channel: 'prod', version: 2, mode: 'stage', signature: 'verified', deployed_by: 'ana', deployed_at: '2026-11-23T10:05:00Z', outcome: 'ok', error: null, preflight }
      deploys.unshift(record)
      return reply({ error: 'The preflight of version 2 found 1 error, so it was not activated in prod: warehouse: marts.dim_region is not on the connection. It stays staged.', steps: ['Staged orders on prod: version 2.'],
        deployed: { package: 'orders', channel: 'prod', mode: 'stage', version: 2, changed: true, target: 'jobs', runner: 'https://jobs:8495', signature: 'verified', preflight }, deploy: record }, 409)
    }
    if (url.pathname === '/api/deploys/d-prod/activate' && method === 'POST') {
      if (!body.confirm) return reply({ error: 'The preflight of version 2 found 1 error, so it was not activated in prod.' }, 409)
      return reply({ activation: 'activated', message: 'Version 2 of orders is active in prod.', active: 2, preflight: { ...preflight, activation: 'activated', confirmed_by: 'ana' }, deploy: { ...deploys[0], mode: 'serve' } })
    }
    if (url.pathname === '/api/deploys' && method === 'POST' && body.previewInstance) {
      if (body.previewInstance === 'ghost') return reply({ error: "The Gateway refused the preview of the instance ghost (400): There is no instance 'ghost' in production to preview.", steps: ['Read build b1 of orders/d1 from Workspace.'] }, 409)
      const preview = { instance: body.previewInstance, address: `https://edge:8443/apps/${body.previewInstance}~preview/`, path: `/apps/${body.previewInstance}~preview/` }
      const record = { id: `d${deploys.length + 1}`, workspace_server: 'ws-1', project: 'orders', workspace: 'd1', build: 'b1', item: 'app', kind: 'app', target: 'g-1', target_name: 'edge', product: 'gateway', route: 'app',
        name: 'orders', channel: 'preview', version: 1, mode: 'preview', signature: null, deployed_by: 'ana', deployed_at: '2026-11-23T10:05:00Z', outcome: 'ok', error: null, preview }
      deploys.unshift(record)
      return reply({ steps: ['Read build b1 of orders/d1 from Workspace.', `Saved the preview of ${body.previewInstance} at ${preview.address}.`],
        deployed: { app: 'orders', channel: 'preview', mode: 'preview', version: 1, instance: body.previewInstance, preview_address: preview.address, preview_path: preview.path, target: 'edge', gateway: 'https://edge:8443' }, deploy: record })
    }
    if (url.pathname === '/api/deploys' && method === 'POST') {
      const record = { id: `d${deploys.length + 1}`, workspace_server: 'ws-1', project: 'orders', workspace: 'd1', build: 'b1', item: 'package', kind: 'sync', target: 'r-1', target_name: 'jobs', product: 'runner', route: 'package',
        name: 'orders', channel: body.channel, version: 1, mode: 'stage', signature: 'verified', deployed_by: 'ana', deployed_at: '2026-11-23T10:05:00Z', outcome: 'ok', error: null }
      deploys.unshift(record)
      return reply({ steps: ['Read build b1 of orders/d1 from Workspace.'], deployed: { package: 'orders', channel: body.channel, mode: 'stage', version: 1, changed: true, target: 'jobs', runner: 'https://jobs:8495', signature: 'verified' }, deploy: record })
    }
    if (url.pathname === '/relay/ws-1/api/v1/projects') return reply({ projects: [{ id: 'orders', kind, title: null, created: '', workspaces: 1 }] })
    if (url.pathname === '/relay/ws-1/api/v1/project-kinds') return reply({ kinds: [
      { name: 'sync', title: 'Sync', description: '', buildable: false, produces: true, publishes: [{ product: 'runner', what: 'package', description: '' }] },
      { name: 'app', title: 'App', description: '', buildable: true, produces: false, publishes: [{ product: 'gateway', what: 'app', description: '' }] },
    ] })
    if (url.pathname === ws) return reply({ builds: [build] })
    if (url.pathname === `${ws}/b1`) return reply({ build, result: kind === 'app'
      ? { status: 'succeeded', message: 'Built.', diagnostics: [], uncommitted_changes: false, commit: null, seconds: 1, app: 'orders', version: '1.0.0', files: 3, bundled: [] }
      : { status: 'succeeded', message: 'Made.', diagnostics: [], uncommitted_changes: false, commit: null, seconds: 1, kind: 'sync', info: { package: 'orders' } }, publishes: [] })
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

  it('shows the preflight of a version served in prod: an error keeps it staged with the findings and the run\'s link, and Activate anyway confirms it', async () => {
    await mount('/?build=b1')
    await fill('[data-testid="wsp-publish-channel"]', 'prod')
    await click('[data-testid="wsp-publish-serve"]')
    await click('[data-testid="wsp-publish"]')

    const card = q('[data-testid="wsp-preflight"]')
    expect(card?.dataset.activation).toBe('refused')
    expect(q('[data-testid="wsp-preflight-words"]')?.textContent).toContain('so the version was not activated')
    expect([...host.querySelectorAll('[data-testid="wsp-preflight-finding"]')].map((f) => (f as HTMLElement).dataset.severity)).toEqual(['error', 'warning'])
    expect(q('[data-testid="wsp-preflight-run-link"]')?.getAttribute('href')).toBe('/servers/r-1/runs?run=6f1c0f1e-0000-4000-8000-000000000001')
    expect(q('[data-testid="wsp-preflight-activate"]')?.textContent).toBe('Activate anyway')

    await click('[data-testid="wsp-preflight-activate"]')
    expect(console_.calls.filter((c) => c.path === '/api/deploys/d-prod/activate')).toEqual([{ method: 'POST', path: '/api/deploys/d-prod/activate', body: { confirm: true } }])
    expect(q('[data-testid="wsp-preflight"]')?.dataset.activation).toBe('activated')
    expect(q('[data-testid="wsp-preflight-words"]')?.textContent).toContain('the confirmation is kept in the Runner\'s record')
    expect(q('[data-testid="wsp-preflight-activated"]')?.textContent).toBe('Version 2 of orders is active in prod.')
    expect(q('[data-testid="wsp-preflight-activate"]')).toBeNull()
  })

  it('sends an app build as the preview of an instance through the console\'s deploy, shows the Gateway\'s refusal in its words, and calls no route of the Workspace server that sends', async () => {
    console_ = fakeConsole('app')
    vi.stubGlobal('fetch', console_.fetcher)
    await mount('/?build=b1')
    // the Gateways the console manages are offered; the one Gateway is chosen without asking, and a Runner is not offered
    const options = [...host.querySelectorAll('[data-testid="wsp-preview-data-target"] option')].map((o) => (o as HTMLOptionElement).value)
    expect(options).toEqual(['', 'g-1'])
    expect((q('[data-testid="wsp-preview-data"]') as HTMLButtonElement).disabled).toBe(true)

    await fill('[data-testid="wsp-preview-data-instance"]', 'ghost')
    await click('[data-testid="wsp-preview-data"]')
    expect(q('[data-testid="wsp-publish-refused"]')?.textContent).toContain("no instance 'ghost' in production")

    await fill('[data-testid="wsp-preview-data-instance"]', 'acme')
    await click('[data-testid="wsp-preview-data"]')
    const posted = console_.calls.filter((c) => c.method === 'POST')
    expect(posted.map((c) => [c.path, c.body])).toEqual([
      ['/api/deploys', { workspaceServer: 'ws-1', project: 'orders', workspace: 'd1', build: 'b1', target: 'g-1', previewInstance: 'ghost' }],
      ['/api/deploys', { workspaceServer: 'ws-1', project: 'orders', workspace: 'd1', build: 'b1', target: 'g-1', previewInstance: 'acme' }],
    ])
    expect(console_.calls.filter((c) => /\/builds\/[^/]+\/(publish|preview-data)$/.test(c.path) || c.path.includes('publish-targets'))).toEqual([])
    const done = q('[data-testid="wsp-preview-data-done"]')
    expect(done?.dataset.instance).toBe('acme')
    expect(q('[data-testid="wsp-preview-data-link"]')?.getAttribute('href')).toBe('https://edge:8443/apps/acme~preview/')
    // the console's record of the preview, which the tab reads back
    const record = q('[data-testid="wsp-deploy-record"]')
    expect(record?.textContent).toContain('Sent orders to edge as the preview of acme (version 1 of the preview channel).')
  })

  it('opens the build the address names, and puts the build chosen in the address', async () => {
    await mount('/?tab=builds&build=b1')
    expect(q('[data-testid="wsp-build-detail"]')?.dataset.build).toBe('b1')
    await click('[data-testid="wsp-build-row"]')
    expect(where).toBe('/?tab=builds&build=b1')
  })
})
