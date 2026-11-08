import { describe, expect, it } from 'vitest'
import { validateRenderedView } from '../validate'
import { sampleBundle } from '../testing/sampleBundle'
import { fixtureClient, FIXTURE_AS_OF, NO_RECORDED_RESULT, placedWidgets } from './fixtureClient'
import { moduleClient } from './moduleClient'
import { validateText } from './text'
import { ViewsClientError } from './types'

const bundle = sampleBundle()
const dashboard = { kind: 'dashboard', name: 'retail-sales-today' } as const
const report = { kind: 'report', name: 'retail-sales-monthly' } as const

describe('the sample bundle', () => {
  it.each([dashboard, report])('$name opens with no diagnostic', async ({ kind, name }) => {
    const opened = await fixtureClient(bundle).open(kind, name)
    expect(opened.diagnostics).toEqual([])
    expect(opened.document?.kind).toBe(kind)
    expect(opened.version).toMatch(/^[0-9a-f]{8}$/)
  })

  it('lists both documents, by kind', async () => {
    const client = fixtureClient(bundle)
    expect(await client.list()).toEqual([
      { name: 'retail-sales-monthly', kind: 'report', title: 'Retail sales by month' },
      { name: 'retail-sales-today', kind: 'dashboard', title: 'Retail sales today' },
    ])
    expect((await client.list('dashboard')).map((d) => d.name)).toEqual(['retail-sales-today'])
  })

  it('the dashboard has the layouts the manual test names, and the report a flow default', async () => {
    const d = (await fixtureClient(bundle).open('dashboard', 'retail-sales-today')).document!
    expect(d.layouts.map((l) => `${l.name}:${l.type}`)).toEqual(['desk:reflow', 'wall:canvas', 'phone:canvas', 'print:flow'])
    expect(d.layouts.find((l) => l.default)!.name).toBe('desk')
    const r = (await fixtureClient(bundle).open('report', 'retail-sales-monthly')).document!
    expect(r.layouts.find((l) => l.default)).toMatchObject({ name: 'paged', type: 'flow' })
  })

  it('every recorded result is the widget type it is recorded for, and matrix and chart results stay inside the rendered-view subset', () => {
    for (const [key, results] of Object.entries(bundle.results!)) {
      const document = validateText(bundle.documents[key]!).document!
      for (const [id, result] of Object.entries(results)) {
        const widget = document.widgets[id]
        expect(widget, `${key}/${id} names a widget the document has`).toBeDefined()
        expect(result.type, `${key}/${id}`).toBe(widget!.type)
        if (result.type === 'chart' || result.type === 'matrix') {
          expect(validateRenderedView({ kind: 'rendered_view', status: 'complete', views: [result] }), `${key}/${id}`).toEqual([])
          expect(result.id).toBe(id)
        }
        if (result.type === 'table') for (const row of result.rows) expect(row.cells.length, `${key}/${id}`).toBe(result.columns.length)
      }
    }
  })
})

describe('fixtureClient.resolve', () => {
  it.each([
    [dashboard, 'desk', ['regions', 'net', 'margin', 'note', 'by_region', 'top_products']],
    [dashboard, 'wall', ['regions', 'net', 'margin', 'note', 'by_region', 'top_products']],
    [dashboard, 'phone', ['regions', 'net', 'margin', 'by_region', 'top_products']],
    [dashboard, 'print', ['net', 'margin', 'note', 'by_region', 'top_products']],
    [report, 'paged', ['intro', 'net', 'margin', 'units', 'by_month', 'products']],
    [report, 'wall', ['net', 'margin', 'units', 'by_month', 'intro']],
  ] as const)('%j in %s gives the recorded result of exactly the widgets the layout places', async (ref, layout, ids) => {
    const result = await fixtureClient(bundle).resolve(ref, layout, {})
    expect(Object.keys(result.widgets)).toEqual(ids)
    expect(result).toMatchObject({ kind: 'rendered_widgets', layout, as_of: FIXTURE_AS_OF })
    for (const [id, widget] of Object.entries(result.widgets)) expect(widget.type, id).not.toBe('error')
  })

  it('a text widget needs no result, a control lists its recorded values, a metric is the recorded number', async () => {
    const { widgets } = await fixtureClient(bundle).resolve(dashboard, 'desk', {})
    expect(widgets.note).toEqual({ type: 'text' })
    expect(widgets.regions).toMatchObject({ type: 'control', values: [{ value: 'West' }, { value: 'Central' }, { value: 'East' }, { value: 'South' }] })
    expect(widgets.net).toMatchObject({ type: 'metric', value: { text: '$3.30M' } })
  })

  it('a widget with no recorded result gets its own error entry and the others are still given', async () => {
    const thin = structuredClone(bundle)
    delete thin.results!['dashboard/retail-sales-today']!.by_region
    const { widgets } = await fixtureClient(thin).resolve(dashboard, 'desk', {})
    expect(widgets.by_region).toMatchObject({ type: 'error', code: NO_RECORDED_RESULT })
    expect((widgets.by_region as { message: string }).message).toContain('by_region')
    expect(widgets.net!.type).toBe('metric')
    expect(widgets.top_products!.type).toBe('table')
  })

  it('a control that lists values and has no recorded values is an error entry', async () => {
    const thin = structuredClone(bundle)
    delete thin.results!['dashboard/retail-sales-today']!.regions
    const { widgets } = await fixtureClient(thin).resolve(dashboard, 'desk', {})
    expect(widgets.regions).toMatchObject({ type: 'error', code: NO_RECORDED_RESULT })
  })

  it('an image is its recorded bytes by hash, and an error entry when the bundle has none', async () => {
    const hash = `sha256:${'ab'.repeat(32)}`
    const src = 'data:image/png;base64,AAAA'
    const text = `schemaVersion: 1\nkind: dashboard\ntitle: Logo\nwidgets:\n  logo: {type: image, hash: "${hash}", media_type: image/png, alt: Logo}\nlayouts:\n  - {name: desk, type: reflow, default: true, grid: {columns: 1}, tiles: [{widget: logo, x: 0, y: 0, w: 1, h: 1}]}\n`
    const ref = { kind: 'dashboard', name: 'logo' } as const
    expect((await fixtureClient({ documents: { 'dashboard/logo': text }, images: { [hash]: src } }).resolve(ref, 'desk', {})).widgets.logo).toEqual({ type: 'image', src })
    expect((await fixtureClient({ documents: { 'dashboard/logo': text } }).resolve(ref, 'desk', {})).widgets.logo).toMatchObject({ type: 'error', code: 'no_recorded_image' })
  })

  it('only narrows the resolution to the widgets asked for, and onResolve says which ones ran', async () => {
    const calls: Array<{ widgets: string[]; parameters: unknown }> = []
    const client = fixtureClient(bundle, { onResolve: (c) => calls.push({ widgets: c.widgets, parameters: c.parameters }) })
    const result = await client.resolve(dashboard, 'desk', { region: ['West'] }, { only: ['net', 'margin', 'not_placed'] })
    expect(Object.keys(result.widgets)).toEqual(['net', 'margin'])
    expect(calls).toEqual([{ widgets: ['net', 'margin'], parameters: { region: ['West'] } }])
  })

  it('refuses a document it does not have and a layout the document does not have, naming what exists', async () => {
    const client = fixtureClient(bundle)
    await expect(client.open('dashboard', 'nope')).rejects.toMatchObject({ name: 'ViewsClientError', code: 'not_found' })
    await expect(client.resolve({ kind: 'dashboard', name: 'nope' }, 'desk', {})).rejects.toMatchObject({ code: 'not_found' })
    await expect(client.resolve(dashboard, 'tv', {})).rejects.toThrow(/`desk`, `wall`, `phone`, `print`/)
  })

  it('refuses to resolve a document with errors', async () => {
    const broken = structuredClone(bundle)
    broken.documents['dashboard/retail-sales-today'] = broken.documents['dashboard/retail-sales-today']!.replace('{widget: net, x: 0, y: 1, w: 3, h: 2}', '{widget: net, x: 11, y: 1, w: 3, h: 2}')
    await expect(fixtureClient(broken).resolve(dashboard, 'desk', {})).rejects.toMatchObject({ code: 'invalid' })
  })

  it('placedWidgets reads the blocks of a flow layout, rows included, once each', async () => {
    const d = (await fixtureClient(bundle).open('dashboard', 'retail-sales-today')).document!
    expect(placedWidgets(d, 'print')).toEqual(['net', 'margin', 'note', 'by_region', 'top_products'])
    expect(placedWidgets(d, 'nope')).toEqual([])
  })
})

describe('diagnostics are placed in the text', () => {
  const text = bundle.documents['dashboard/retail-sales-today']!

  it('a tile outside the grid says its file, line and column', () => {
    const bad = text.replace('{widget: net, x: 0, y: 1, w: 3, h: 2}', '{widget: net, x: 11, y: 1, w: 3, h: 2}')
    const result = validateText(bad, { file: 'retail-sales-today.yaml' })
    expect(result.ok).toBe(false)
    const d = result.diagnostics.find((x) => x.code === 'DBD-VIEW-026')!
    expect(d).toMatchObject({ severity: 'error', file: 'retail-sales-today.yaml', path: 'layouts[0].tiles[1]' })
    expect(bad.split('\n')[d.line! - 1]).toContain('widget: net')
    expect(d.column).toBeGreaterThan(1)
  })

  it('a missing key is placed at the parent that exists; a syntax error and a duplicate key stop before validation', () => {
    const noQuery = validateText(text.replace('    query: {model: retail_sales, order: [[sales.net_amount, desc]], limit: 40}\n', ''))
    expect(noQuery.diagnostics.some((d) => d.code === 'DBD-105' && d.line! > 1)).toBe(true)
    const syntax = validateText('widgets: [unclosed')
    expect(syntax).toMatchObject({ ok: false, document: null })
    expect(syntax.diagnostics[0]).toMatchObject({ code: 'DBD-101', line: 1 })
    const dup = validateText('title: a\ntitle: b\n')
    expect(dup.diagnostics[0]).toMatchObject({ code: 'DBD-102', line: 2 })
    expect(validateText('').diagnostics[0]!.code).toBe('DBD-106')
  })

  it('a warning does not stop the document', () => {
    const result = validateText(text.replace('when: {width: {min: 2400}, medium: screen}', 'when: {width: {min: 9, max: 2}}'))
    expect(result.ok).toBe(true)
    expect(result.diagnostics.some((d) => d.severity === 'warning' && d.code === 'DBD-VIEW-081')).toBe(true)
  })

  it('open gives the diagnostics and no document for a broken file', async () => {
    const broken = structuredClone(bundle)
    broken.documents['dashboard/retail-sales-today'] = 'kind: dashboard\n'
    const opened = await fixtureClient(broken).open('dashboard', 'retail-sales-today')
    expect(opened.document).toBeNull()
    expect(opened.diagnostics.length).toBeGreaterThan(0)
  })
})

describe('fixtureClient.save', () => {
  it('saves under the opened version, refuses a stale one naming both, and refuses invalid text with its diagnostics', async () => {
    const client = fixtureClient(structuredClone(bundle))
    const opened = await client.open('dashboard', 'retail-sales-today')
    const edited = opened.text.replace('title: Retail sales today', 'title: Retail sales, today')
    const saved = await client.save('dashboard', 'retail-sales-today', edited, opened.version)
    expect(saved.version).not.toBe(opened.version)
    expect((await client.open('dashboard', 'retail-sales-today')).document!.title).toBe('Retail sales, today')

    const stale = await client.save('dashboard', 'retail-sales-today', edited, opened.version).catch((e: unknown) => e)
    expect(stale).toBeInstanceOf(ViewsClientError)
    expect(stale).toMatchObject({ code: 'version_conflict' })
    expect((stale as Error).message).toContain(saved.version)
    await expect(client.save('dashboard', 'retail-sales-today', edited)).rejects.toMatchObject({ code: 'version_conflict' })

    const invalid = await client.save('dashboard', 'copy', 'kind: dashboard\n').catch((e: unknown) => e as ViewsClientError)
    expect(invalid).toMatchObject({ code: 'invalid' })
    expect((invalid as ViewsClientError).diagnostics!.length).toBeGreaterThan(0)
    expect((await client.list('dashboard')).map((d) => d.name)).toEqual(['retail-sales-today'])

    await client.save('dashboard', 'copy', edited)
    expect((await client.list('dashboard')).map((d) => d.name)).toEqual(['copy', 'retail-sales-today'])
  })
})

describe('fixtureClient.preview', () => {
  it('gives the recorded result of a widget by its id and document, an error entry otherwise', async () => {
    const client = fixtureClient(bundle)
    const d = (await client.open('dashboard', 'retail-sales-today')).document!
    expect(await client.preview({ widget: d.widgets.net!, widgetId: 'net', document: 'retail-sales-today' })).toMatchObject({ type: 'metric' })
    expect(await client.preview({ widget: d.widgets.net! })).toMatchObject({ type: 'error', code: NO_RECORDED_RESULT })
    expect(await client.preview({ widget: d.widgets.note! })).toEqual({ type: 'text' })
  })
})

describe('moduleClient', () => {
  interface Call { method: string; url: string; headers: Record<string, string>; body: unknown }
  function fake(answers: Array<{ status?: number; body?: unknown; raw?: string } | Error>) {
    const calls: Call[] = []
    const fetcher = (async (input: string, init: RequestInit) => {
      calls.push({ method: init.method!, url: input, headers: init.headers as Record<string, string>, body: init.body === undefined ? undefined : JSON.parse(init.body as string) })
      const answer = answers.shift()!
      if (answer instanceof Error) throw answer
      const status = answer.status ?? 200
      return new Response(answer.raw ?? (answer.body === undefined ? '' : JSON.stringify(answer.body)), { status })
    }) as unknown as typeof fetch
    return { calls, client: moduleClient({ baseUrl: 'https://views.example/', fetch: fetcher, headers: () => ({ authorization: 'Bearer t' }) }) }
  }
  const rendered = { kind: 'rendered_widgets', layout: 'desk', as_of: FIXTURE_AS_OF, widgets: { net: { type: 'metric', value: { text: '1' } } } }

  it('open reads GET /api/v1/dashboards/{name} and validates the text it gets', async () => {
    const { calls, client } = fake([{ body: { name: 'retail-sales-today', version: 'v7', text: bundle.documents['dashboard/retail-sales-today'] } }])
    const opened = await client.open('dashboard', 'retail-sales-today')
    expect(calls[0]).toMatchObject({ method: 'GET', url: 'https://views.example/api/v1/dashboards/retail-sales-today' })
    expect(calls[0]!.headers).toMatchObject({ authorization: 'Bearer t', accept: 'application/json' })
    expect(opened).toMatchObject({ version: 'v7', diagnostics: [] })
    expect(opened.document!.title).toBe('Retail sales today')
  })

  it('resolve posts parameters, the layout and the narrowed widgets to .../render of the kind', async () => {
    const { calls, client } = fake([{ body: rendered }, { body: rendered }])
    expect(await client.resolve(dashboard, 'desk', { region: ['West'] }, { only: ['net'] })).toEqual(rendered)
    await client.resolve(report, 'paged', {})
    expect(calls[0]).toMatchObject({ method: 'POST', url: 'https://views.example/api/v1/dashboards/retail-sales-today/render', body: { parameters: { region: ['West'] }, layout: 'desk', widgets: ['net'] } })
    expect(calls[1]).toMatchObject({ url: 'https://views.example/api/v1/reports/retail-sales-monthly/render', body: { parameters: {}, layout: 'paged' } })
    expect(calls[1]!.headers['content-type']).toBe('application/json')
  })

  it('preview posts to /api/v1/views/preview; list reads both collections; save puts the text with the expected version', async () => {
    const { calls, client } = fake([
      { body: { type: 'text' } },
      { body: { items: [{ name: 'b', title: 'B' }] } },
      { body: { items: [{ name: 'a', title: 'A' }] } },
      { body: { name: 'a', version: 'v2' } },
    ])
    const d = validateText(bundle.documents['dashboard/retail-sales-today']!).document!
    expect(await client.preview({ widget: d.widgets.note!, widgetId: 'note' })).toEqual({ type: 'text' })
    expect(await client.list()).toEqual([{ name: 'a', title: 'A', kind: 'report' }, { name: 'b', title: 'B', kind: 'dashboard' }])
    expect(await client.save('dashboard', 'a', 'text', 'v1')).toEqual({ name: 'a', version: 'v2' })
    expect(calls.map((c) => `${c.method} ${c.url.replace('https://views.example', '')}`)).toEqual([
      'POST /api/v1/views/preview', 'GET /api/v1/dashboards', 'GET /api/v1/reports', 'PUT /api/v1/dashboards/a',
    ])
    expect(calls[0]!.body).toMatchObject({ widget_id: 'note', parameters: {} })
    expect(calls[3]!.body).toEqual({ text: 'text', expected_version: 'v1' })
  })

  it('maps the server\'s refusals to codes, keeping its words and diagnostics', async () => {
    const diagnostics = [{ code: 'DBD-VIEW-005', severity: 'error', path: 'widgets.x', message: 'nope' }]
    const { client } = fake([
      { status: 404, body: { message: 'There is no dashboard named x.' } },
      { status: 409, body: { message: 'stale' } },
      { status: 422, body: { message: 'bad', diagnostics } },
      { status: 403, body: { message: 'denied' } },
      { status: 500, body: {} },
      { status: 200, raw: 'not json' },
      new Error('connection refused'),
    ])
    const codes: string[] = []
    const messages: string[] = []
    let withDiagnostics: unknown
    for (let i = 0; i < 7; i++) {
      const e = (await client.resolve(dashboard, 'desk', {}).catch((x: unknown) => x)) as ViewsClientError
      codes.push(e.code)
      messages.push(e.message)
      if (i === 2) withDiagnostics = e.diagnostics
    }
    expect(codes).toEqual(['not_found', 'version_conflict', 'invalid', 'denied', 'unavailable', 'bad_response', 'unavailable'])
    expect(messages[0]).toBe('There is no dashboard named x.')
    expect(messages[6]).toContain('connection refused')
    expect(withDiagnostics).toEqual(diagnostics)
  })

  it('validate is the local validator, and the module client satisfies the same interface as the fixture', async () => {
    const { client, calls } = fake([])
    const asClient: import('./types').ViewsClient = client
    expect((await asClient.validate('kind: dashboard\n')).ok).toBe(false)
    expect(calls).toEqual([])
  })

  it('a resolve in flight is cancelled by its signal, and the abort is not turned into a module error', async () => {
    const controller = new AbortController()
    const abort = new DOMException('aborted', 'AbortError')
    const fetcher = (async () => { controller.abort(); throw abort }) as unknown as typeof fetch
    const client = moduleClient({ baseUrl: 'https://x', fetch: fetcher })
    await expect(client.resolve(dashboard, 'desk', {}, { signal: controller.signal })).rejects.toBe(abort)
  })
})
