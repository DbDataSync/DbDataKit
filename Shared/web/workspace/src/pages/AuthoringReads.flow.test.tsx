// @vitest-environment happy-dom
import { act, useState } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { MemoryRouter } from 'react-router-dom'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { ModuleProvider } from '@dbdatakit/shell'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { DraftProvider } from '../draft'
import type { DescribedColumn } from '../authoringReads'
import { CatalogPanel } from './CatalogPanel'
import { NewestChecks, QueryDescribe } from './AuthoringReads'
import { ChannelPicker } from './CatalogRefresh'

;(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true

const PASSWORD = 'pw-7731'

/** The Workspace server through the relay (`/relay/ws-1`) and the console's own routes, in memory. `bound` is whether dev binds the slot of `crm`. */
function fakeConsole() {
  const asked: { path: string; body: unknown }[] = []
  const state = { bound: false, describeLooks: 0, noPackage: false }
  const reply = (body: unknown, status = 200) => Promise.resolve(new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } }))
  const fetcher = vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input), 'http://localhost')
    const path = decodeURIComponent(url.pathname)
    asked.push({ path: `${init?.method ?? 'GET'} ${path}${url.search}`, body: init?.body ? JSON.parse(String(init.body)) : null })
    const draft = '/relay/ws-1/api/v1/projects/crm-to-dw/workspaces/ws'
    if (path === `${draft}/files`) return reply({ files: [{ path: 'config/connections/crm.yaml', size: 1 }], truncated: false, max_file_bytes: 1 })
    if (path === `${draft}/files/catalog-cache.yaml`) return reply({ error: 'no such file' }, 404)
    if (path === '/api/instances') return reply([{ id: 'r-1', name: 'runners-eu', product: 'dbdatarunner' }, { id: 'ws-1', name: 'Workspace', product: 'dbdataworkspace' }])
    const project = '/api/workspace-catalog/ws-1/crm-to-dw'
    if (path === `${project}/channels`) {
      // a Runner that holds no version of the package lists every channel it serves, with the bindings it holds for the package
      if (state.noPackage) {
        return reply({
          package: 'crm-to-dw', connection: 'crm', slot: 'crm', runner: 'r-1', runner_name: 'runners-eu', default: state.bound ? 'dev' : null,
          channels: [{ channel: 'dev', production: false, bound: state.bound, bound_to: state.bound ? 'crm-to-dw-crm-dev' : null }, { channel: 'prod', production: true, bound: false, bound_to: null }],
          note: 'runners-eu holds no version of crm-to-dw yet.',
        })
      }
      return reply({
        package: 'crm-to-dw', connection: 'crm', slot: 'crm', runner: 'r-1', runner_name: 'runners-eu', default: state.bound ? 'dev' : null,
        channels: [{ channel: 'dev', production: false, bound: state.bound, bound_to: state.bound ? 'crm_dev' : null }, { channel: 'prod', production: true, bound: true, bound_to: 'crm' }],
        ...(state.bound ? {} : {}),
      })
    }
    if (path === '/api/workspace-dev-logins/ws-1/crm-to-dw') {
      // the console binds the slot in the channel: the picker, asked again, lists dev as bound
      const body = JSON.parse(String(init!.body)) as { channel: string; user?: string }
      state.bound = true
      return reply({
        package: 'crm-to-dw', connection: 'crm', slot: 'crm', channel: body.channel, production: false, runner: 'r-1', runner_name: 'runners-eu',
        ...(body.user ? { user: body.user } : {}), secrets: { password: `dbdatakit:runner:dev-login:crm-to-dw.${body.channel}.crm:password` },
        bound: true, copy: `crm-to-dw-crm-${body.channel}`, steps: [`Bound the slot 'crm' of crm-to-dw in ${body.channel} to 'crm-to-dw-crm-${body.channel}'.`],
      })
    }
    if (path === `${project}/binding-test`) {
      const body = JSON.parse(String(init!.body)) as { channel: string }
      return reply({
        package: 'crm-to-dw', connection: 'crm', slot: 'crm', channel: body.channel, production: body.channel === 'prod', role: 'default', runner: 'r-1', runner_name: 'runners-eu',
        ok: false, step: 'login', connected: false, bound_to: 'crm_dev', message: "The secret 'dbdatakit:runner:dev-login:crm-to-dw.dev.crm:password' is not on this runner.",
        steps: [{ step: 'binding', state: 'passed' }, { step: 'connection', state: 'passed' }, { step: 'login', state: 'failed' }, { step: 'connect', state: 'skipped' }],
      })
    }
    const base = { run_id: 'run-9', link: '/servers/r-1/runs?run=run-9', runner: 'r-1', runner_name: 'runners-eu', connection: 'crm', channel: 'dev', production: false }
    const columns = [{ name: 'id', type: 'int', nullable: false }, { name: 'segment', type: 'nvarchar(20)', nullable: true }]
    if (path === `${project}/describe`) return reply({ ...base, state: 'running' }, 202)
    if (path === `${project}/describe/run-9`) return reply(++state.describeLooks < 2 ? { ...base, state: 'running' } : { ...base, state: 'done', engine: 'sqlserver', strategy: 'SqlServerDescribeFirstResultSet', columns }, state.describeLooks < 2 ? 202 : 200)
    if (path === '/api/deploys') {
      const deploy = (id: string, channel: string) => ({
        id, workspace_server: 'ws-1', project: 'crm-to-dw', workspace: 'ws', build: 'b1', item: 'crm-to-dw', kind: 'sync', target: 'r-1', target_name: 'runners-eu', product: 'runner', route: 'x',
        name: 'crm-to-dw', channel, version: 3, mode: 'serve', signature: null, deployed_by: 'ana', deployed_at: '2026-11-22T10:00:00Z', outcome: 'ok', error: null, tasks: ['load', 'preflight'],
      })
      return reply({ deploys: [deploy('d-prod', 'prod'), deploy('d-dev', 'dev'), deploy('d-dev-old', 'dev')] })
    }
    if (path === '/api/deploys/d-prod/runs') {
      return reply({
        package: 'crm-to-dw', channel: 'prod', task: 'preflight', runner: 'r-1', runner_name: 'runners-eu', link: '/servers/r-1/runs?run=pf-2',
        run: {
          id: 'pf-2', task: 'preflight', outcome: 'succeeded', started: '2026-11-23T08:00:00Z', finished: '2026-11-23T08:00:05Z', version: '3', failure: null,
          steps: [{ key: 'orders', started: '2026-11-23T08:00:01Z', completed: '2026-11-23T08:00:04Z', result: { part: 'orders', findings: [{ severity: 'error', message: 'Change tracking is off on dbo.orders.', about: 'crm' }] } }],
        },
      })
    }
    if (path === '/api/deploys/d-dev/runs') return reply({ package: 'crm-to-dw', channel: 'dev', task: 'preflight', runner: 'r-1', runner_name: 'runners-eu', link: null, run: null })
    return reply({ error: `no ${path}` }, 404)
  })
  return { asked, state, fetcher }
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

async function mount(node: React.ReactNode, withDraft = true) {
  host = document.createElement('div')
  document.body.appendChild(host)
  root = createRoot(host)
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  await act(async () => {
    root.render(
      <MemoryRouter>
        <ModuleProvider info={{ basePath: '/servers/ws-1', apiBase: '/relay/ws-1' }}>
          <QueryClientProvider client={client}>
            {withDraft ? <DraftProvider project="crm-to-dw" id="ws" kind="sync" canEdit>{node}</DraftProvider> : node}
          </QueryClientProvider>
        </ModuleProvider>
      </MemoryRouter>,
    )
  })
  await settle()
}

describe('the Designer\'s shared reads', () => {
  let server: ReturnType<typeof fakeConsole>
  beforeEach(() => { server = fakeConsole(); vi.stubGlobal('fetch', server.fetcher) })
  afterEach(() => { act(() => root.unmount()); host.remove(); vi.unstubAllGlobals() })

  it('offers Set up for development where dev does not bind the slot, sends the login and the draft to the console only, and the picker then reads in dev', async () => {
    await mount(<CatalogPanel project="crm-to-dw" id="ws" onOpen={() => {}} kind="sync" />)
    expect((q('[data-testid="wsp-refresh-go"]') as HTMLButtonElement).disabled).toBe(true)
    expect(q('[data-testid="wsp-test-go"]')).toBeNull()
    await click('[data-testid="wsp-setup-open"]')
    expect(q('[data-testid="wsp-setup-warning"]')?.textContent).toContain('goes to runners-eu, which keeps it for the dev channel only')
    expect((q('[data-testid="wsp-setup-channel"]') as HTMLSelectElement).value).toBe('dev')
    await type('[data-testid="wsp-setup-user"]', 'ana_dev')
    await type('[data-testid="wsp-setup-password"]', PASSWORD)
    await click('[data-testid="wsp-setup-save"]')

    const sent = server.asked.filter((a) => JSON.stringify(a.body ?? '').includes(PASSWORD))
    expect(sent.map((a) => a.path)).toEqual(['POST /api/workspace-dev-logins/ws-1/crm-to-dw'])
    expect(sent[0]!.body).toEqual({ runner: 'r-1', channel: 'dev', connection: 'crm', user: 'ana_dev', password: PASSWORD, draft: 'ws' })
    expect(host.innerHTML).not.toContain(PASSWORD)
    // the picker asked the Runner again once the slot was bound: dev is picked, Refresh can read, and Set up is no longer offered
    expect(server.asked.filter((a) => a.path.startsWith('GET /api/workspace-catalog/ws-1/crm-to-dw/channels')).length).toBeGreaterThanOrEqual(2)
    expect(q('[data-testid="wsp-channel-picker"]')?.getAttribute('data-channel')).toBe('dev')
    expect((q('[data-testid="wsp-refresh-go"]') as HTMLButtonElement).disabled).toBe(false)
    expect(q('[data-testid="wsp-setup"]')).toBeNull()
  })

  it('on a Runner that holds no version of the package yet, the picker lists its channels, and after Set up for dev Refresh and Test read in dev', async () => {
    server.state.noPackage = true
    await mount(<CatalogPanel project="crm-to-dw" id="ws" onOpen={() => {}} kind="sync" />)
    expect(all('[data-testid="wsp-channel-channel"] option').map((o) => o.getAttribute('value'))).toEqual(['', 'dev', 'prod'])
    expect(q('[data-testid="wsp-channel-note"]')?.textContent).toContain('holds no version of crm-to-dw')
    expect((q('[data-testid="wsp-refresh-go"]') as HTMLButtonElement).disabled).toBe(true)
    await click('[data-testid="wsp-setup-open"]')
    expect(all('[data-testid="wsp-setup-channel"] option').map((o) => o.getAttribute('value'))).toEqual(['dev', 'prod'])
    await type('[data-testid="wsp-setup-password"]', PASSWORD)
    await click('[data-testid="wsp-setup-save"]')

    expect(server.asked.find((a) => a.path === 'POST /api/workspace-dev-logins/ws-1/crm-to-dw')?.body).toEqual({ runner: 'r-1', channel: 'dev', connection: 'crm', password: PASSWORD, draft: 'ws' })
    expect(host.innerHTML).not.toContain(PASSWORD)
    expect(q('[data-testid="wsp-channel-picker"]')?.getAttribute('data-channel')).toBe('dev')
    expect((q('[data-testid="wsp-refresh-go"]') as HTMLButtonElement).disabled).toBe(false)
    expect(q('[data-testid="wsp-test-go"]')).not.toBeNull()
  })

  it('on a Runner whose only channel prod does not bind erp, offers Set up erp for development with prod and the production warning', async () => {
    const list = { package: 'crm-to-dw', connection: 'erp', slot: 'erp', runner: 'r-1', runner_name: 'runners-eu', default: null, channels: [{ channel: 'prod', production: true, bound: false, bound_to: null }] }
    await mount(<ChannelPicker runners={[{ id: 'r-1', name: 'runners-eu' }]} runner="r-1" onRunner={() => {}} list={list} channel="" onChannel={() => {}} connection="erp" project="crm-to-dw" canEdit />)
    expect(q('[data-testid="wsp-channel-unbound"]')?.textContent).toContain('prod does not bind the slot erp. Set up for development binds it')
    expect(q('[data-testid="wsp-setup-open"]')?.textContent).toBe('Set up erp for development')
    await click('[data-testid="wsp-setup-open"]')
    expect(all('[data-testid="wsp-setup-channel"] option').map((o) => o.getAttribute('value'))).toEqual(['prod'])
    expect(q('[data-testid="wsp-setup-production"]')?.textContent).toContain('This reads production: the shape of erp as the prod channel of runners-eu binds it')
    await type('[data-testid="wsp-setup-password"]', PASSWORD)
    await click('[data-testid="wsp-setup-save"]')
    expect(server.asked.find((a) => a.path === 'POST /api/workspace-dev-logins/ws-1/crm-to-dw')?.body).toEqual({ runner: 'r-1', channel: 'prod', connection: 'erp', password: PASSWORD, draft: 'ws' })
  })

  it('once dev binds the slot, Test shows each step of the binding test and its reason, and no row', async () => {
    server.state.bound = true
    await mount(<CatalogPanel project="crm-to-dw" id="ws" onOpen={() => {}} kind="sync" />)
    expect(q('[data-testid="wsp-setup-open"]')).toBeNull()
    await click('[data-testid="wsp-test-go"]')
    expect(server.asked.find((a) => a.path === 'POST /api/workspace-catalog/ws-1/crm-to-dw/binding-test')?.body).toEqual({ runner: 'r-1', channel: 'dev', connection: 'crm', role: 'default' })
    expect(all('[data-testid="wsp-test-step"]').map((s) => `${s.dataset.step}:${s.dataset.state}`)).toEqual(['binding:passed', 'connection:passed', 'login:failed', 'connect:skipped'])
    expect(q('[data-testid="wsp-test-message"]')?.textContent).toContain('is not on this runner')
    expect(q('[data-testid="wsp-test-result"]')?.dataset.ok).toBe('false')
  })

  it('Describe follows a long description and hands the columns to the kind', async () => {
    let used: DescribedColumn[] | null = null
    function Host() {
      const [sql] = useState('SELECT id, segment FROM dbo.accounts')
      return <QueryDescribe project="crm-to-dw" connection="crm" runner="r-1" channel="dev" sql={sql} canEdit onColumns={(c) => { used = c }} />
    }
    await mount(<Host />, false)
    await click('[data-testid="wsp-describe-go"]')
    expect(q('[data-testid="wsp-describe-running"]')?.textContent).toContain('takes longer than 10 seconds')
    await settle(30)
    await act(async () => { await new Promise((r) => setTimeout(r, 2100)) })
    await settle()
    expect(q('[data-testid="wsp-describe-summary"]')?.textContent).toBe('2 columns: id int, segment nvarchar(20) null')
    await click('[data-testid="wsp-describe-use"]')
    expect(used).toEqual([{ name: 'id', type: 'int', nullable: false }, { name: 'segment', type: 'nvarchar(20)', nullable: true }])
    expect(q('[data-testid="wsp-describe-used"]')).not.toBeNull()
    expect(server.asked.find((a) => a.path === 'POST /api/workspace-catalog/ws-1/crm-to-dw/describe')?.body).toEqual({ runner: 'r-1', channel: 'dev', connection: 'crm', sql: 'SELECT id, segment FROM dbo.accounts' })
  }, 10_000)

  it('the newest-check card shows the newest preflight of each channel, with its time, link and findings', async () => {
    await mount(<NewestChecks project="crm-to-dw" id="ws" />, false)
    const cards = all('[data-testid="wsp-newest-check"]')
    expect(cards.map((c) => c.dataset.channel)).toEqual(['prod', 'dev'])
    const prod = cards[0]!
    expect(prod.dataset.errors).toBe('1')
    expect(prod.querySelector('[data-testid="wsp-newest-check-when"]')?.textContent).toContain('finished 2026-11-23T08:00:05Z')
    expect(prod.querySelector('[data-testid="wsp-newest-check-link"]')?.getAttribute('href')).toBe('/servers/r-1/runs?run=pf-2')
    expect(prod.querySelector('[data-testid="wsp-preflight-finding"]')?.textContent).toContain('Change tracking is off on dbo.orders.')
    expect(cards[1]!.querySelector('[data-testid="wsp-newest-check-none"]')?.textContent).toBe('No preflight has run in dev yet.')
    expect(server.asked.some((a) => a.path === 'GET /api/deploys/d-prod/runs?task=preflight&step=preflight-findings')).toBe(true)
    expect(server.asked.some((a) => a.path.includes('d-dev-old'))).toBe(false)
  })
})
