import { describe, expect, it } from 'vitest'
import { bundleFrom, discoverSources, fixturesSource, pickSource, resolveLog, viewsClient, type ViewsSource } from './viewsSource'
import type { InstanceSummary } from './api'

describe('the fixture source of the Views page', () => {
  it('holds the retail sample bundle: both documents, valid, with the recorded result of every widget the desk layout places', async () => {
    const client = viewsClient()
    expect((await client.list()).map((d) => `${d.kind}/${d.name}`)).toEqual(['report/retail-sales-monthly', 'dashboard/retail-sales-today'])
    const opened = await client.open('dashboard', 'retail-sales-today')
    expect(opened.diagnostics.filter((d) => d.severity === 'error')).toEqual([])
    resolveLog.clear()
    const { widgets } = await client.resolve({ kind: 'dashboard', name: 'retail-sales-today' }, 'desk', {})
    expect(Object.values(widgets).filter((w) => w.type === 'error')).toEqual([])
    expect(resolveLog.snapshot()).toEqual([{ kind: 'dashboard', name: 'retail-sales-today', layout: 'desk', widgets: ['regions', 'net', 'margin', 'note', 'by_region', 'top_products'] }])
  })

  it('names a document and a result by its folder', () => {
    const bundle = bundleFrom({ '/x/dashboards/a.yaml': 'A', '/x/reports/b.yaml': 'B', '/x/other/c.yaml': 'C' }, { '/y/widget-results/dashboard/a/w1.json': { type: 'text' }, '/y/elsewhere.json': 1 })
    expect(bundle.documents).toEqual({ 'dashboard/a': 'A', 'report/b': 'B' })
    expect(bundle.results).toEqual({ 'dashboard/a': { w1: { type: 'text' } } })
  })
})

const server = (id: string, extra: Partial<InstanceSummary> = {}): InstanceSummary =>
  ({ id, name: `Proxy ${id}`, url: `https://${id}.example`, product: 'dbdataproxy', version: '1', apiVersion: 1, reachable: true, ready: true, problem: null, pairedAt: null, ...extra })

const answer = (body: unknown, status = 200) => Promise.resolve(new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } }))

describe('the sources of the Views page', () => {
  it('lists each reachable Proxy that runs the views module, then the fixtures; a Proxy without the module, an unreachable one and another product are not sources', async () => {
    const asked: string[] = []
    const sources = await discoverSources({
      list: async () => [server('a'), server('b'), server('c', { reachable: false }), server('d', { product: 'dbdatasync' }), server('e'), server('f')],
      fetch: (async (input: RequestInfo | URL) => {
        const url = String(input)
        asked.push(url)
        if (url.includes('/a/')) return answer({ enabled: true, state: 'ok' })
        if (url.includes('/b/')) return answer({ error: 'There is no such call.' }, 404)   // a Proxy before DBR11b
        if (url.includes('/e/')) return answer({ enabled: false })                       // the management API answers, the module is off
        throw new TypeError('network')                                                   // f does not answer
      }) as typeof fetch,
    })
    expect(sources.map((s) => s.id)).toEqual(['a', 'fixtures'])
    expect(sources[0]).toEqual({ id: 'a', label: 'Proxy a (published)', published: true })
    // only the reachable Proxies were asked, through the relay and at the module's status route
    expect(asked.sort()).toEqual(['/relay/a/api/v1/views/status', '/relay/b/api/v1/views/status', '/relay/e/api/v1/views/status', '/relay/f/api/v1/views/status'])
  })

  it('has the fixtures when the console cannot list its servers', async () => {
    expect(await discoverSources({ list: () => Promise.reject(new Error('401')), fetch: (() => { throw new Error('not asked') }) as typeof fetch })).toEqual([fixturesSource])
  })

  it('chooses the source an address names, else the first published one, else the fixtures, and says which name it could not use', () => {
    const published: ViewsSource = { id: 'a', label: 'Proxy a (published)', published: true }
    const other: ViewsSource = { id: 'b', label: 'Proxy b (published)', published: true }
    const both = [published, other, fixturesSource]
    expect(pickSource(both, null)).toEqual({ source: published, unknown: null })
    expect(pickSource(both, 'b')).toEqual({ source: other, unknown: null })
    expect(pickSource(both, 'fixtures')).toEqual({ source: fixturesSource, unknown: null })
    expect(pickSource(both, 'gone')).toEqual({ source: published, unknown: 'gone' })
    expect(pickSource([fixturesSource], null)).toEqual({ source: fixturesSource, unknown: null })
    expect(pickSource([fixturesSource], 'a')).toEqual({ source: fixturesSource, unknown: 'a' })
  })

  it('opens, lists, resolves and saves a Proxy\'s documents through the relay with the module\'s routes, and the page can see what was asked', async () => {
    const calls: Array<{ method: string; url: string; body?: unknown }> = []
    const realFetch = globalThis.fetch
    globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input)
      calls.push({ method: init?.method ?? 'GET', url, body: init?.body ? JSON.parse(String(init.body)) : undefined })
      if (url.endsWith('/api/v1/dashboards')) return answer({ items: [{ name: 'sales', title: 'Sales', version: '3' }] })
      if (url.endsWith('/render')) return answer({ kind: 'rendered_widgets', layout: 'desk', as_of: '2026-11-08T12:00:00Z', widgets: { net: { type: 'metric' } } })
      if (init?.method === 'PUT') return answer({ name: 'sales', version: '4' })
      return answer({ error: 'no' }, 404)
    }) as typeof fetch
    try {
      const client = viewsClient('proxy 1')
      expect(await client.list('dashboard')).toEqual([{ name: 'sales', title: 'Sales', kind: 'dashboard' }])
      resolveLog.clear()
      await client.resolve({ kind: 'dashboard', name: 'sales' }, 'desk', {})
      expect(resolveLog.snapshot()).toEqual([{ kind: 'dashboard', name: 'sales', layout: 'desk', widgets: ['net'] }])
      await client.resolve({ kind: 'dashboard', name: 'sales' }, 'desk', {}, { only: ['net'] })
      expect(resolveLog.snapshot().at(-1)?.widgets).toEqual(['net'])
      expect(await client.save('dashboard', 'sales', 'kind: dashboard', '3')).toEqual({ name: 'sales', version: '4' })
      expect(calls.map((c) => `${c.method} ${c.url}`)).toEqual([
        'GET /relay/proxy%201/api/v1/dashboards',
        'POST /relay/proxy%201/api/v1/dashboards/sales/render',
        'POST /relay/proxy%201/api/v1/dashboards/sales/render',
        'PUT /relay/proxy%201/api/v1/dashboards/sales',
      ])
      expect(calls[3]!.body).toEqual({ text: 'kind: dashboard', expected_version: '3' })
      await expect(client.open('dashboard', 'missing')).rejects.toMatchObject({ code: 'not_found' })
    } finally {
      globalThis.fetch = realFetch
    }
  })
})
