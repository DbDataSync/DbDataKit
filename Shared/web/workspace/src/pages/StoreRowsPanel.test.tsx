// @vitest-environment happy-dom
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { renderToStaticMarkup } from 'react-dom/server'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { ModuleProvider } from '@dbdatakit/shell'
import type { AttributeSchema, ModelSchema } from '@dbdatakit/store-ui'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { ConsoleServer } from '../deploys'
import { StoreRowsPanel, gatewaysOf, servedModels, servingGateways, storeBaseOf } from './StoreRowsPanel'

;(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true

const attribute = (name: string, over: Partial<AttributeSchema> = {}): AttributeSchema => ({ name, title: null, type: 'VARCHAR(100)', kind: 'string', length: 100, precision: null, scale: null, required: false, unique: false, default: null, pattern: null, min: null, max: null, allowed: null, references: null, ...over })
/** The model `territories` as a Gateway's store describes it: one entity, keyed by `code`, with no history. */
const territories: ModelSchema = {
  name: 'territories', title: null, approval: 'none', instances: 'none',
  entities: [{ name: 'territory', title: null, key: ['code'], history: 'none', role: 'table', rules: [], attributes: [attribute('code', { required: true }), attribute('name', { required: true })] }],
}

const server = (id: string, product: string | null, reachable = true): ConsoleServer => ({ id, name: id, product, reachable })

/**
 * The console in memory: its list of servers, and the store of each Gateway through the relay. `gw-edge` serves `territories`, `gw-other` serves another
 * model, `gw-plain` has no store module (404), `gw-broken` fails, and `gw-away` is one the console cannot reach. The Workspace server's own target list
 * answers 404, as it will once it is removed.
 */
function fakeConsole() {
  const asked: { path: string; body: unknown }[] = []
  const rows = [{ values: { code: 'NE', name: 'North East' }, version: 1, valid_from: null, valid_to: null }]
  const reply = (body: unknown, status = 200) => Promise.resolve(new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } }))
  const fetcher = vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input), 'http://localhost')
    const path = decodeURIComponent(url.pathname)
    const method = init?.method ?? 'GET'
    asked.push({ path: `${method} ${path}`, body: init?.body ? JSON.parse(String(init.body)) : null })
    if (path === '/api/instances') {
      return reply([server('ws-1', 'dbdataworkspace'), server('plant', 'dbdatarunner'), server('gw-other', 'dbdatagateway'), server('gw-edge', 'dbdatagateway'),
        server('gw-plain', 'dbdatagateway'), server('gw-broken', 'dbdatagateway'), server('gw-away', 'dbdatagateway', false)])
    }
    if (path === '/relay/gw-edge/api/v1/store/models') return reply({ models: [{ name: 'territories', title: null, version: 1, entities: ['territory'] }] })
    if (path === '/relay/gw-other/api/v1/store/models') return reply({ models: [{ name: 'budgets', title: null, version: 1, entities: ['budget'] }] })
    if (path === '/relay/gw-broken/api/v1/store/models') return reply({ error: 'The Gateway did not answer in time.' }, 502)
    const store = '/relay/gw-edge/api/v1/store/models/territories'
    if (path === `${store}/schema`) return reply(territories)
    if (path === `${store}/entities/territory/rows`) return reply({ rows, limit: 25, offset: 0 })
    if (path === `${store}/changes` && method === 'POST') {
      const change = (JSON.parse(String(init!.body)) as { changes: { values: Record<string, unknown> }[] }).changes[0]!
      rows[0] = { ...rows[0]!, values: { ...rows[0]!.values, ...change.values }, version: 2 }
      return reply({ applied: 1 })
    }
    return reply({ error: `no ${path}` }, 404)
  })
  return { asked, fetcher }
}

let root: Root
let host: HTMLDivElement
const settle = async () => { for (let i = 0; i < 15; i++) await act(async () => { await new Promise((r) => setTimeout(r, 5)) }) }
const q = (sel: string) => host.querySelector(sel) as HTMLElement | null
const all = (sel: string) => [...host.querySelectorAll(sel)] as HTMLElement[]

async function mount() {
  host = document.createElement('div')
  document.body.appendChild(host)
  root = createRoot(host)
  await act(async () => {
    root.render(
      <ModuleProvider info={{ basePath: '/servers/ws-1', apiBase: '/relay/ws-1' }}>
        <StoreRowsPanel paths={['models/territories.yaml', 'models/budgets.yaml', 'models/ledger.yaml']} canWrite />
      </ModuleProvider>,
    )
  })
  await settle()
}

async function chooseModel(model: string) {
  await act(async () => {
    const select = q('[data-testid="wsp-rows-model"]') as HTMLSelectElement
    select.value = model
    select.dispatchEvent(new Event('change', { bubbles: true }))
  })
  await settle()
}

describe('the Gateways the Rows tab offers', () => {
  it('are the Gateways the console lists, by name, never a Runner or the Workspace server', () => {
    expect(gatewaysOf([server('ws-1', 'dbdataworkspace'), server('plant', 'dbdatarunner'), server('b', 'dbdatagateway'), server('a', 'dbdatagateway', false), server('x', null)]))
      .toEqual([{ id: 'a', name: 'a', reachable: false }, { id: 'b', name: 'b', reachable: true }])
  })
  it('are the ones whose store serves the model', () => {
    const edge = { id: 'gw-edge', name: 'edge', reachable: true }
    const other = { id: 'gw-other', name: 'other', reachable: true }
    const answers = [{ gateway: other, models: ['budgets'], problem: null }, { gateway: edge, models: ['territories', 'budgets'], problem: null }, { gateway: { ...edge, id: 'gw-x' }, models: null, problem: 'No answer.' }]
    expect(servingGateways(answers, 'territories').map((g) => g.id)).toEqual(['gw-edge'])
    expect(servingGateways(answers, 'budgets').map((g) => g.id)).toEqual(['gw-other', 'gw-edge'])
  })
  it('send the controls through the console relay to the Gateway\'s store paths', () => {
    expect(storeBaseOf('gw-1')).toBe('/relay/gw-1/api/v1/store')
    expect(storeBaseOf('a b')).toBe('/relay/a%20b/api/v1/store')
  })
})

describe('the Rows tab of a Store project', () => {
  let console_: ReturnType<typeof fakeConsole>
  beforeEach(() => { console_ = fakeConsole(); vi.stubGlobal('fetch', console_.fetcher) })
  afterEach(() => { vi.unstubAllGlobals() })

  it('says a draft with no model has no rows, and that the rows need the console when the screens are not hosted by one', () => {
    const panel = (paths: string[]) => renderToStaticMarkup(<QueryClientProvider client={new QueryClient()}><StoreRowsPanel paths={paths} canWrite /></QueryClientProvider>)
    expect(panel(['README.md'])).toContain('data-testid="wsp-rows-no-model"')
    expect(panel(['models/territories.yaml'])).toContain('data-testid="wsp-rows-unavailable"')
  })

  it('asks each reachable Gateway which models it serves, and reports one that does not answer without stopping the others', async () => {
    const gateways = gatewaysOf([server('gw-edge', 'dbdatagateway'), server('gw-plain', 'dbdatagateway'), server('gw-broken', 'dbdatagateway'), server('gw-away', 'dbdatagateway', false)])
    const answers = await servedModels(gateways)
    expect(answers.map((a) => [a.gateway.id, a.models, a.problem])).toEqual([
      ['gw-away', null, 'The console cannot reach it.'], ['gw-broken', null, 'The Gateway did not answer in time.'], ['gw-edge', ['territories'], null], ['gw-plain', [], null],
    ])
    expect(console_.asked.map((a) => a.path)).not.toContain('GET /relay/gw-away/api/v1/store/models')
  })

  describe('in the console', () => {
    afterEach(() => { act(() => root.unmount()); host.remove() })

    it('lists the Gateways from the console\'s answer that serve the model, edits a row on one, and asks the Workspace server nothing', async () => {
      await mount()
      // the models are offered by name, so `budgets` comes first: only the Gateway that serves it is offered
      expect(all('[data-testid="wsp-rows-gateway"] option').map((o) => o.textContent)).toEqual(['gw-other'])
      await chooseModel('territories')
      expect(all('[data-testid="wsp-rows-gateway"] option').map((o) => o.textContent)).toEqual(['gw-edge'])
      expect(all('[data-testid="wsp-rows-gateway-problem"]').map((p) => p.dataset.gateway)).toEqual(['gw-away', 'gw-broken'])
      expect(q('[data-testid="sui-row"]')?.dataset.key).toBe('NE')

      await act(async () => { q('[data-testid="sui-row"]')!.click() })
      await settle()
      const name = q('[data-testid="sui-field-name"]') as HTMLInputElement
      await act(async () => {
        Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!.call(name, 'North-East')
        name.dispatchEvent(new Event('input', { bubbles: true }))
      })
      await act(async () => { q('[data-testid="sui-submit"]')!.click() })
      await settle()

      const write = console_.asked.find((a) => a.path === 'POST /relay/gw-edge/api/v1/store/models/territories/changes')
      expect(write?.body).toMatchObject({ changes: [{ action: 'update', entity: 'territory', key: { code: 'NE' }, expected_version: 1, values: { name: 'North-East' } }] })
      expect(q('[data-testid="sui-row"]')?.textContent).toContain('North-East')
      // the Gateways came from the console; no route of the Workspace server was asked, its target list least of all
      expect(console_.asked.filter((a) => a.path.includes('/relay/ws-1/') || a.path.includes('publish-targets'))).toEqual([])
    })

    it('offers no Gateway for a model none serves, and says how to get one', async () => {
      await mount()
      await chooseModel('ledger')
      expect(all('[data-testid="wsp-rows-gateway"] option').map((o) => o.textContent)).toEqual(['No Gateway serves ledger'])
      expect(q('[data-testid="wsp-rows-no-gateway"]')?.textContent).toContain('No Gateway the console manages serves ledger')
      expect(q('[data-testid="sui-grid"]')).toBeNull()
    })
  })
})
