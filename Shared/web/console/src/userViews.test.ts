import { describe, expect, it } from 'vitest'
import { ViewsClientError, type OpenedDocument, type ViewsClient, type WidgetDoc } from '@dbdatakit/visuals'
import { MINE_NAME, freeWidgetId, parametersUsedBy, personalClient, pinWidget, splitName, widgetFromRequest } from './userViews'
import { personalFor, type ViewsSource } from './viewsSource'

const json = (body: unknown, status = 200) => Promise.resolve(new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } }))

interface Call { method: string; url: string; body?: unknown }

/** The module's routes as a fake: every call is kept, and an answer is looked up by "METHOD path-and-query". */
function fakeModule(answers: Record<string, () => Promise<Response>>) {
  const calls: Call[] = []
  const fetcher = (async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input)
    const method = init?.method ?? 'GET'
    calls.push({ method, url, body: init?.body ? JSON.parse(String(init.body)) : undefined })
    const key = `${method} ${url.replace('/relay/gw/', '/')}`
    return (answers[key] ?? (() => json({ code: 'not_found', message: `no answer for ${key}` }, 404)))()
  }) as typeof fetch
  return { calls, client: personalClient({ baseUrl: '/relay/gw', fetch: fetcher }) }
}

const doc = 'schemaVersion: 1\nkind: dashboard\ntitle: Mine\nwidgets: {}\nlayouts: [{name: desk, type: reflow, default: true, grid: {columns: 12}, tiles: []}]\n'

describe('names of a person\'s documents', () => {
  it('writes a document of another person as name@owner, and a name of your own as the module takes it', () => {
    expect(splitName('sales')).toEqual({ name: 'sales', owner: null })
    expect(splitName('sales@u-alice')).toEqual({ name: 'sales', owner: 'u-alice' })
    expect(MINE_NAME.test('sales-by_region2')).toBe(true)
    for (const bad of ['Sales', '1sales', 'a b', '', 'a'.repeat(65), 'sales@u-alice']) expect(MINE_NAME.test(bad)).toBe(false)
  })
})

describe('the client of a person\'s own documents', () => {
  it('lists yours, then what others shared with you as name@owner with the owner in the title, and keeps the module\'s shares for the detailed list', async () => {
    const { client, calls } = fakeModule({
      'GET /api/v1/my/views?kind=dashboard': () => json({ items: [
        { kind: 'dashboard', owner: 'u-wendy', owner_name: 'Wendy', name: 'west', title: 'West', version: '2', updated_at: '2026-11-08T12:00:00Z', own: true, shared_with: ['group:analyst'] },
        { kind: 'dashboard', owner: 'u-alice', owner_name: 'Alice', name: 'sales', title: 'Sales', version: '1', updated_at: '2026-11-08T12:00:00Z', own: false, shared_with: [] },
      ] }),
    })
    expect(await client.list('dashboard')).toEqual([
      { kind: 'dashboard', name: 'sales@u-alice', title: 'Sales (shared by Alice)' },
      { kind: 'dashboard', name: 'west', title: 'West' },
    ])
    const detailed = await client.mine('dashboard')
    expect(detailed[0]).toMatchObject({ owner: 'u-wendy', ownerName: 'Wendy', own: true, sharedWith: ['group:analyst'], version: '2' })
    expect(detailed[1]).toMatchObject({ owner: 'u-alice', ownerName: 'Alice', own: false, sharedWith: [] })
    expect(calls.every((c) => c.method === 'GET' && c.url.startsWith('/relay/gw/api/v1/my/views'))).toBe(true)
  })

  it('opens your own document and another person\'s through ?owner=, resolves as the viewer, and saves with the version it read', async () => {
    const { client, calls } = fakeModule({
      'GET /api/v1/my/dashboards/west': () => json({ name: 'west', version: '2', text: doc }),
      'GET /api/v1/my/dashboards/sales?owner=u-alice': () => json({ name: 'sales', version: '1', text: doc }),
      'POST /api/v1/my/dashboards/sales/render?owner=u-alice': () => json({ kind: 'rendered_widgets', layout: 'desk', as_of: 'x', widgets: {} }),
      'PUT /api/v1/my/dashboards/west': () => json({ owner: 'wendy', name: 'west', version: '3' }),
    })
    expect(await client.open('dashboard', 'west')).toMatchObject({ name: 'west', version: '2' })
    expect(await client.open('dashboard', 'sales@u-alice')).toMatchObject({ name: 'sales@u-alice', version: '1' })
    await client.resolve({ kind: 'dashboard', name: 'sales@u-alice' }, 'desk', { region: ['West'] }, { only: ['net'] })
    expect(await client.save('dashboard', 'west', doc, '2')).toEqual({ name: 'west', version: '3' })
    expect(calls.map((c) => `${c.method} ${c.url.replace('/relay/gw', '')}`)).toEqual([
      'GET /api/v1/my/dashboards/west',
      'GET /api/v1/my/dashboards/sales?owner=u-alice',
      'POST /api/v1/my/dashboards/sales/render?owner=u-alice',
      'PUT /api/v1/my/dashboards/west',
    ])
    expect(calls[2]!.body).toEqual({ parameters: { region: ['West'] }, layout: 'desk', only: ['net'] })
    expect(calls[3]!.body).toEqual({ text: doc, expected_version: '2' })
  })

  it('refuses in words, before sending anything, what is not yours to change: save, delete, share, history, restore and promote of a shared name; and a name the module would refuse', async () => {
    const { client, calls } = fakeModule({})
    const denied = async (run: () => Promise<unknown>) => {
      const error = await run().then(() => null, (e: unknown) => e)
      expect(error).toBeInstanceOf(ViewsClientError)
      expect((error as ViewsClientError).code).toBe('denied')
      expect((error as ViewsClientError).message).toContain("another person's")
    }
    await denied(() => client.save('dashboard', 'sales@u-alice', doc, '1'))
    await denied(() => client.delete('dashboard', 'sales@u-alice'))
    await denied(() => client.setShares('dashboard', 'sales@u-alice', ['*']))
    await denied(() => client.shares('dashboard', 'sales@u-alice'))
    await denied(() => client.history('dashboard', 'sales@u-alice'))
    await denied(() => client.restore('dashboard', 'sales@u-alice', '1', '2'))
    await denied(() => client.promote('dashboard', 'sales@u-alice'))
    await expect(client.save('dashboard', 'Bad Name', doc)).rejects.toMatchObject({ code: 'invalid' })
    expect(calls).toEqual([])
  })

  it('sends the share list, the history, the restore and the promote to the module\'s routes and reads their answers', async () => {
    const { client, calls } = fakeModule({
      'PUT /api/v1/my/dashboards/west/shares': () => json({ grantees: ['group:analyst', 'user:bob'] }),
      'GET /api/v1/my/dashboards/west/history': () => json({ items: [{ version: '3', saved_by: 'wendy', saved_at: 'b', current: true }, { version: '2', saved_by: 'wendy', saved_at: 'a', current: false }] }),
      'GET /api/v1/my/dashboards/west/history?version=2': () => json({ version: '2', text: 'old' }),
      'POST /api/v1/my/dashboards/west/restore': () => json({ owner: 'wendy', name: 'west', version: '4', restored: '2' }),
      'GET /api/v1/my/reports/west/promote': () => json({ path: 'reports/west.yaml', text: 'kind: report\n' }),
    })
    expect(await client.setShares('dashboard', 'west', ['group:analyst', 'user:bob'])).toEqual(['group:analyst', 'user:bob'])
    expect((await client.history('dashboard', 'west')).map((v) => [v.version, v.current])).toEqual([['3', true], ['2', false]])
    expect(await client.historyText('dashboard', 'west', '2')).toBe('old')
    expect(await client.restore('dashboard', 'west', '2', '3')).toEqual({ name: 'west', version: '4', restored: '2' })
    expect(await client.promote('report', 'west')).toEqual({ path: 'reports/west.yaml', text: 'kind: report\n' })
    expect(calls[0]!.body).toEqual({ grantees: ['group:analyst', 'user:bob'] })
    expect(calls[3]!.body).toEqual({ version: 2, expected_version: 3 })
  })

  it('maps the module\'s refusals to codes a page can act on, with the module\'s own words and diagnostics', async () => {
    const { client } = fakeModule({
      'PUT /api/v1/my/dashboards/a': () => json({ code: 'version_conflict', message: 'is at version 2', current_version: '2' }, 409),
      'PUT /api/v1/my/dashboards/b': () => json({ code: 'invalid', message: 'does not load', diagnostics: [{ code: 'DBD-VIEW-001', severity: 'error', path: 'widgets', message: 'x' }] }, 400),
      'GET /api/v1/my/dashboards/c': () => json({ code: 'not_found', message: 'There is no dashboard' }, 404),
      'GET /api/v1/my/views?kind=report': () => json({ code: 'denied', message: 'no' }, 403),
    })
    await expect(client.save('dashboard', 'a', doc, '1')).rejects.toMatchObject({ code: 'version_conflict', message: 'is at version 2', status: 409 })
    await expect(client.save('dashboard', 'b', doc)).rejects.toMatchObject({ code: 'invalid', diagnostics: [{ code: 'DBD-VIEW-001' }] })
    await expect(client.open('dashboard', 'c')).rejects.toMatchObject({ code: 'not_found' })
    await expect(client.list('report')).rejects.toMatchObject({ code: 'denied' })
  })
})

/** An in-memory store with the module's save rule (a stale or a second create is a conflict): the fake the pin logic runs against. */
function memoryClient(initial: Record<string, string> = {}) {
  const docs = new Map(Object.entries(initial).map(([k, v]) => [k, { text: v, version: 1 }]))
  const saves: string[] = []
  const client = {
    async open(kind, name) {
      const d = docs.get(name)
      if (!d) throw new ViewsClientError('not_found', `There is no ${kind} ${name}`)
      return { name, kind, text: d.text, version: String(d.version), document: null, diagnostics: [] } as OpenedDocument
    },
    async save(_kind, name, text, expected) {
      const d = docs.get(name)
      if (d && String(d.version) !== expected) throw new ViewsClientError('version_conflict', `${name} is at version ${d.version}`)
      if (!d && expected !== undefined) throw new ViewsClientError('not_found', 'gone')
      const version = (d?.version ?? 0) + 1
      docs.set(name, { text, version })
      saves.push(name)
      return { name, version: String(version) }
    },
  } as ViewsClient
  return { client, docs, saves }
}

const metric = (extra: object = {}): WidgetDoc => ({ type: 'metric', label: 'Revenue', encodings: { value: 'orders.net_revenue' }, query: { model: 'sales_catalog', ...extra } }) as WidgetDoc

describe('pinning a widget into a dashboard of your own', () => {
  it('makes the dashboard when there is none: the widget added and placed in the default layout', async () => {
    const { client, docs } = memoryClient()
    const result = await pinWidget(client, { target: 'my-board', title: 'My board', widget: metric(), id: 'revenue' })
    expect(result).toEqual({ name: 'my-board', version: '1', widgetId: 'revenue', created: true })
    const text = docs.get('my-board')!.text
    expect(text).toContain('title: My board')
    expect(text).toMatch(/revenue:\s*\n\s+type: metric/)
    expect(text).toMatch(/widget: revenue/)
  })

  it('adds to an existing dashboard under a free id, below what is already there, saving on the version it read; a second pin of the same widget is a second widget', async () => {
    const { client, docs } = memoryClient()
    await pinWidget(client, { target: 'b', title: 'B', widget: metric(), id: 'revenue' })
    const second = await pinWidget(client, { target: 'b', widget: metric(), id: 'revenue' })
    expect(second).toMatchObject({ version: '2', widgetId: 'revenue2', created: false })
    const text = docs.get('b')!.text
    expect(text).toContain('revenue2')
    // the second tile is placed under the first (y after its bottom)
    const tiles = [...text.matchAll(/widget: (revenue2?)[\s\S]*?y: (\d+)/g)].map((m) => [m[1], Number(m[2])])
    expect(tiles.find((t) => t[0] === 'revenue2')![1]).toBeGreaterThan(tiles.find((t) => t[0] === 'revenue')![1] as number)
  })

  it('copies the parameters the widget uses (a filter\'s and a control\'s), and only those, and not twice', async () => {
    const { client, docs } = memoryClient()
    const parameters = [
      { name: 'region', type: 'string', for: 'customers.region', many: true, default: [] },
      { name: 'year', type: 'string' },
    ] as never
    const filtered = metric({ filters: [{ attribute: 'customers.region', operator: 'equals', values: { param: 'region' } }] })
    expect(parametersUsedBy(filtered)).toEqual(['region'])
    expect(parametersUsedBy({ type: 'control', parameter: 'year' } as WidgetDoc)).toEqual(['year'])
    await pinWidget(client, { target: 'b', title: 'B', widget: filtered, id: 'revenue', parameters })
    await pinWidget(client, { target: 'b', widget: filtered, id: 'again', parameters })
    const text = docs.get('b')!.text
    expect(text.match(/- name: region|name: region/g)?.length).toBe(1)
    expect(text).not.toContain('year')
  })

  it('refuses a picture, a dashboard with no layout that places tiles, a stale save and a document that is not a document, in words and saving nothing', async () => {
    const { client, saves } = memoryClient({ flowonly: 'schemaVersion: 1\nkind: dashboard\ntitle: F\nwidgets: {}\nlayouts: [{name: paged, type: flow, default: true, sections: []}]\n', broken: 'not: [yaml' })
    await expect(pinWidget(client, { target: 'x', widget: { type: 'image', hash: 'sha256:' + '0'.repeat(64), media_type: 'image/png', alt: 'a' } as WidgetDoc, id: 'logo' })).rejects.toThrow(/picture cannot be pinned/)
    await expect(pinWidget(client, { target: 'flowonly', widget: metric(), id: 'm' })).rejects.toThrow(/no layout that places tiles/)
    await expect(pinWidget(client, { target: 'broken', widget: metric(), id: 'm' })).rejects.toThrow(/not a document yet/)
    expect(saves).toEqual([])

    // somebody else saved between the open and the save: the module's conflict comes through
    const racing = memoryClient()
    await pinWidget(racing.client, { target: 'b', title: 'B', widget: metric(), id: 'm' })
    const open = racing.client.open.bind(racing.client)
    racing.client.open = async (kind, name) => { const opened = await open(kind, name); racing.docs.get(name)!.version += 1; return opened }
    await expect(pinWidget(racing.client, { target: 'b', widget: metric(), id: 'n' })).rejects.toMatchObject({ code: 'version_conflict' })
  })

  it('names a pinned widget by the id rule', () => {
    expect(freeWidgetId(['a'], 'a')).toBe('a2')
    expect(freeWidgetId(['a', 'a2'], 'a')).toBe('a3')
    expect(freeWidgetId([], '9 lives!')).toBe('lives_')
    expect(freeWidgetId([], '123')).toBe('pinned')
  })
})

describe('a widget from a semantic request', () => {
  const request = '{"measures": ["orders.net_revenue"], "attributes": ["customers.region"], "limit": 100}'

  it('keeps the request as it stands and shows its outputs as a table, a chart or a number', () => {
    const table = widgetFromRequest({ model: 'sales_catalog', requestText: request, shape: 'table', title: 'By region' }) as unknown as Record<string, unknown>
    expect(table).toMatchObject({ type: 'table', title: 'By region', encodings: { columns: [{ field: 'customers.region' }, { field: 'orders.net_revenue' }] }, query: { mode: 'explicit', semantic: { model: 'sales_catalog', request: JSON.parse(request) } } })
    expect(widgetFromRequest({ model: 'm', requestText: request, shape: 'chart', title: '' })).toMatchObject({ type: 'chart', title: 'Pinned query', mark: 'bar', encodings: { x: 'customers.region', y: 'orders.net_revenue' } })
    expect(widgetFromRequest({ model: 'm', requestText: request, shape: 'metric', title: 'Revenue' })).toMatchObject({ type: 'metric', label: 'Revenue', encodings: { value: 'orders.net_revenue' } })
    expect(widgetFromRequest({ model: 'm', requestText: '{"measures": [{"name": "avg_order", "agg": "avg"}]}', shape: 'metric', title: '' })).toMatchObject({ encodings: { value: 'avg_order' } })
  })

  it('refuses in words: no model, not JSON, not an object, no measure, an unnamed output, a chart with no attribute', () => {
    const refuse = (model: string, text: string, shape: 'table' | 'chart' | 'metric' = 'table') => () => widgetFromRequest({ model, requestText: text, shape, title: '' })
    expect(refuse(' ', request)).toThrow(/Name the semantic model/)
    expect(refuse('m', '{nope')).toThrow(/not JSON/)
    expect(refuse('m', '[1]')).toThrow(/JSON object/)
    expect(refuse('m', '{"attributes": ["a.b"]}')).toThrow(/no `measures`/)
    expect(refuse('m', '{"measures": [{"agg": "sum"}]}')).toThrow(/must be a name/)
    expect(refuse('m', '{"measures": ["a.b"]}', 'chart')).toThrow(/needs an attribute/)
  })
})

describe('the personal source that goes with a source', () => {
  const published: ViewsSource = { id: 'a', label: 'a', published: true }
  const mine: ViewsSource = { id: 'mine:a', label: 'My', published: false, personal: { server: 'a' } }
  const other: ViewsSource = { id: 'mine:b', label: 'My b', published: false, personal: { server: 'b' } }
  const fixtures: ViewsSource = { id: 'fixtures', label: 'F', published: false }

  it('pairs a server\'s published documents with its own, and a server with no module has none; the fixtures use the first there is', () => {
    const all = [published, mine, other, fixtures]
    expect(personalFor(all, published)).toBe(mine)
    expect(personalFor(all, mine)).toBe(mine)
    expect(personalFor(all, other)).toBe(other)
    expect(personalFor(all, fixtures)).toBe(mine)
    expect(personalFor([fixtures], fixtures)).toBeNull()
    expect(personalFor([{ id: 'c', label: 'c', published: true }, fixtures], { id: 'c', label: 'c', published: true })).toBeNull()
  })
})
