import { describe, expect, it } from 'vitest'
import { validateRenderedView } from '../validate'
import { sampleBundle } from '../testing/sampleBundle'
import { fixtureClient, FIXTURE_AS_OF, NO_RECORDED_RESULT, placedWidgets } from './fixtureClient'
import { moduleClient } from './moduleClient'
import { diagnosticsFromRefusal } from './refusal'
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
    expect(widgets.net).toMatchObject({ type: 'metric', value: { text: '5.2M' } })
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

  it('the lifecycle calls (UE04) read the versions as camel case and send the version the caller saw', async () => {
    const versions = { kind: 'dashboard', name: 'sales', active: 2, previous: 1, versions: [
      { version: 2, published_by: 'Ada', published_at: '2026-11-08T10:00:00Z', note: 'second', active: true, depends_on: ['semantic-model:retail@1'] },
      { version: 1, published_by: 'Bob', published_at: '2026-11-07T10:00:00Z', note: null, active: false },
    ] }
    const { calls, client } = fake([{ body: versions }, { body: { version: 1, moved: [] } }, { body: { version: 2, moved: ['semantic-model:retail@1'] } }, { body: { retired: true } }, { status: 409, body: { code: 'version_conflict', message: 'it changed since you looked', current_version: 3 } }, { body: { retired: true } }])
    const listed = await client.versions!('dashboard', 'sales')
    expect(calls[0]).toMatchObject({ method: 'GET', url: 'https://views.example/api/v1/dashboards/sales/versions' })
    expect(listed.active).toBe(2)
    expect(listed.versions[0]).toEqual({ version: 2, publishedBy: 'Ada', publishedAt: '2026-11-08T10:00:00Z', note: 'second', active: true, dependsOn: ['semantic-model:retail@1'] })
    expect(listed.versions[1]).toMatchObject({ publishedBy: 'Bob', note: null, dependsOn: [] })
    await client.switchTo!('dashboard', 'sales', 1, 2)
    expect(calls[1]).toMatchObject({ method: 'POST', url: 'https://views.example/api/v1/dashboards/sales/switch', body: { version: 1, expected_version: 2 } })
    expect((await client.rollback!('report', 'monthly', 1)).moved).toEqual(['semantic-model:retail@1'])
    expect(calls[2]).toMatchObject({ method: 'POST', url: 'https://views.example/api/v1/reports/monthly/rollback', body: { expected_version: 1 } })
    await client.retire!('dashboard', 'sales', 2)
    expect(calls[3]).toMatchObject({ method: 'DELETE', url: 'https://views.example/api/v1/dashboards/sales?expected_version=2' })
    // a stale call is refused in the server's words, as a conflict
    await expect(client.retire!('dashboard', 'sales', 1)).rejects.toMatchObject({ code: 'version_conflict', message: 'it changed since you looked' })
    // the editor's Delete on a published source is a retire
    await client.delete!('dashboard', 'sales')
    expect(calls[5]).toMatchObject({ method: 'DELETE', url: 'https://views.example/api/v1/dashboards/sales' })
  })

  it('resolve posts parameters, the layout and the narrowed widgets (`only`) to .../render of the kind', async () => {
    const { calls, client } = fake([{ body: rendered }, { body: rendered }])
    expect(await client.resolve(dashboard, 'desk', { region: ['West'] }, { only: ['net'] })).toEqual(rendered)
    await client.resolve(report, 'paged', {})
    expect(calls[0]).toMatchObject({ method: 'POST', url: 'https://views.example/api/v1/dashboards/retail-sales-today/render', body: { parameters: { region: ['West'] }, layout: 'desk', only: ['net'] } })
    expect(calls[1]).toMatchObject({ url: 'https://views.example/api/v1/reports/retail-sales-monthly/render', body: { parameters: {}, layout: 'paged' } })
    expect(calls[1]!.headers['content-type']).toBe('application/json')
  })

  it('preview posts to /api/v1/views/preview; list reads both collections; save puts the text with the expected version', async () => {
    const { calls, client } = fake([
      { body: { kind: 'rendered_widgets', layout: 'preview', as_of: FIXTURE_AS_OF, widgets: { note: { type: 'text' } } } },
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
    expect(calls[0]!.body).toMatchObject({ kind: 'dashboard', widget_id: 'note', parameters: {} })
    expect(calls[3]!.body).toEqual({ text: 'text', expected_version: 'v1' })
  })

  it('preview sends the widget with its document\'s parameters and kind and gives the one entry; a module that gives none is a bad response', async () => {
    const d = validateText(bundle.documents['dashboard/retail-sales-today']!).document!
    const { calls, client } = fake([
      { body: { kind: 'rendered_widgets', layout: 'preview', as_of: FIXTURE_AS_OF, widgets: { net: { type: 'metric', value: { text: '1' } } } } },
      { body: { kind: 'rendered_widgets', layout: 'preview', as_of: FIXTURE_AS_OF, widgets: {} } },
    ])
    expect(await client.preview({ widget: d.widgets.net!, widgetId: 'net', draft: d, parameters: { region: ['West'] } })).toEqual({ type: 'metric', value: { text: '1' } })
    expect(calls[0]!.body).toEqual({ kind: 'dashboard', widget: d.widgets.net, widget_id: 'net', document: { kind: 'dashboard', parameters: d.parameters }, parameters: { region: ['West'] } })
    const e = (await client.preview({ widget: d.widgets.net!, kind: 'report' }).catch((x: unknown) => x)) as ViewsClientError
    expect(e.code).toBe('bad_response')
    expect(calls[1]!.body).toMatchObject({ kind: 'report', widget_id: 'preview' })
  })

  it('previewDocument posts the text with the layout, the widgets and the parameters, and gives the rendered widgets; a refusal keeps its diagnostics', async () => {
    const diagnostics = [{ code: 'DBD-VIEW-020', severity: 'error', path: 'layouts[0].tiles[1].widget', message: 'twice', line: 9, column: 4 }]
    const { calls, client } = fake([{ body: rendered }, { body: rendered }, { status: 400, body: { code: 'invalid', message: 'The dashboard does not load: x', diagnostics } }])
    expect(await client.previewDocument!({ kind: 'dashboard', text: 'kind: dashboard', layout: 'desk', only: ['net'], parameters: { region: 'West' } })).toEqual(rendered)
    await client.previewDocument!({ kind: 'report', text: 't' })
    expect(calls[0]).toMatchObject({ method: 'POST', url: 'https://views.example/api/v1/views/preview', body: { kind: 'dashboard', text: 'kind: dashboard', layout: 'desk', only: ['net'], parameters: { region: 'West' } } })
    expect(calls[1]!.body).toEqual({ kind: 'report', text: 't', parameters: {} })
    const e = (await client.previewDocument!({ kind: 'dashboard', text: 'x' }).catch((x: unknown) => x)) as ViewsClientError
    expect([e.code, e.status, e.diagnostics]).toEqual(['invalid', 400, diagnostics])
  })

  it('the fixture source has no previewDocument: the editor asks it for each widget', () => {
    expect(fixtureClient(bundle).previewDocument).toBeUndefined()
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

  it('validate with serverCheck asks the module (nothing to resolve) of a text the local validator passes, and shows its refusal as diagnostics; any other answer leaves the local one', async () => {
    const text = bundle.documents['dashboard/retail-sales-today']!
    const refusal = 'dashboard/draft.json:1:132: error unknown_member: orders.no_such_measure is not a catalog member. [widgets.net.encodings.value]'
    const asked: unknown[] = []
    const answers: Array<{ status?: number; body?: unknown }> = [{ body: rendered }, { status: 400, body: { code: 'invalid', message: refusal, diagnostics: [] } }, { status: 404, body: {} }, { status: 400, body: { code: 'invalid', message: refusal, diagnostics: [] } }]
    const fetcher = (async (_: string, init: RequestInit) => { asked.push(JSON.parse(init.body as string)); const a = answers.shift()!; return new Response(JSON.stringify(a.body), { status: a.status ?? 200 }) }) as unknown as typeof fetch
    const checked = moduleClient({ baseUrl: 'https://x', fetch: fetcher, serverCheck: true })
    expect((await checked.validate(text, { kind: 'dashboard', file: 'a.yaml' })).ok).toBe(true)
    expect(asked[0]).toEqual({ kind: 'dashboard', text, only: [], parameters: {} })
    const bad = await checked.validate(text, { kind: 'dashboard', file: 'a.yaml' })
    expect(bad.ok).toBe(false)
    expect(bad.diagnostics).toEqual([{ code: 'unknown_member', severity: 'error', path: 'widgets.net.encodings.value', message: 'orders.no_such_measure is not a catalog member.', file: 'a.yaml', line: 1, column: 132 }])
    // a module that cannot be asked (404 from a relay without the route), or a text the local validator refuses, is not asked or not heard
    expect((await checked.validate(text, { kind: 'dashboard' })).ok).toBe(true)
    expect(asked).toHaveLength(3)
    expect((await checked.validate('kind: dashboard\n', { kind: 'dashboard' })).ok).toBe(false)
    expect(asked).toHaveLength(3)
    // off by default: never asked
    const quiet = moduleClient({ baseUrl: 'https://x', fetch: fetcher })
    expect((await quiet.validate(text, { kind: 'dashboard' })).ok).toBe(true)
    expect(asked).toHaveLength(3)
  })

  it('a resolve in flight is cancelled by its signal, and the abort is not turned into a module error', async () => {
    const controller = new AbortController()
    const abort = new DOMException('aborted', 'AbortError')
    const fetcher = (async () => { controller.abort(); throw abort }) as unknown as typeof fetch
    const client = moduleClient({ baseUrl: 'https://x', fetch: fetcher })
    await expect(client.resolve(dashboard, 'desk', {}, { signal: controller.signal })).rejects.toBe(abort)
  })

  it('resolve sends the viewer\'s sort as `order` by widget id (DBR45) and says it sorts on the server; an empty one is not sent', async () => {
    const { calls, client } = fake([{ body: rendered }, { body: rendered }])
    expect(client.sortsOnServer).toBe(true)
    expect(fixtureClient(bundle).sortsOnServer).toBeUndefined()
    await client.resolve(dashboard, 'desk', {}, { only: ['top_products'], order: { top_products: { by: 'sales.units', dir: 'desc' } } })
    await client.resolve(dashboard, 'desk', {}, { order: {} })
    expect(calls[0]!.body).toEqual({ parameters: {}, layout: 'desk', only: ['top_products'], order: { top_products: { by: 'sales.units', dir: 'desc' } } })
    expect(calls[1]!.body).toEqual({ parameters: {}, layout: 'desk' })
  })

  describe('exportPdf (DBR27)', () => {
    const bytes = new TextEncoder().encode('%PDF-1.7 body')
    function exporter(answers: Array<Response | Error>) {
      const urls: string[] = []
      const heads: Array<Record<string, string>> = []
      const fetcher = (async (input: string, init: RequestInit) => { urls.push(input); heads.push(init.headers as Record<string, string>); const a = answers.shift()!; if (a instanceof Error) throw a; return a }) as unknown as typeof fetch
      return { urls, heads, client: moduleClient({ baseUrl: 'https://views.example', fetch: fetcher, headers: () => ({ authorization: 'Bearer t' }) }) }
    }

    it('asks GET .../export?format=pdf with the layout and the parameters as p.name (a list repeats, an empty value is left out) and reads the bytes, the page count and the file name', async () => {
      const { urls, heads, client } = exporter([new Response(bytes, { status: 200, headers: { 'content-type': 'application/pdf', 'content-disposition': 'attachment; filename="retail-sales-monthly.pdf"', 'X-DbData-Export-Pages': '4', 'X-DbData-Export-Browser': 'Chrome/131' } })])
      const file = await client.exportPdf!(report, { layout: 'paged', parameters: { region: ['West', 'East'], year: 2026, none: null, blank: '' } })
      expect(urls[0]).toBe('https://views.example/api/v1/reports/retail-sales-monthly/export?format=pdf&layout=paged&p.region=West&p.region=East&p.year=2026')
      expect(heads[0]!.authorization).toBe('Bearer t')
      expect(file).toMatchObject({ fileName: 'retail-sales-monthly.pdf', contentType: 'application/pdf', pages: 4, browser: 'Chrome/131' })
      expect(Array.from(file.bytes)).toEqual(Array.from(bytes))
    })

    it('through the console\'s relay the file is JSON: base64 content and the module\'s headers', async () => {
      const base64 = btoa(String.fromCharCode(...bytes))
      const { client } = exporter([new Response(JSON.stringify({ file_name: 'r.pdf', content_type: 'application/pdf', size: bytes.length, content_base64: base64, headers: { 'X-DbData-Export-Pages': '2' } }), { status: 200, headers: { 'content-type': 'application/json' } })])
      const file = await client.exportPdf!(report)
      expect(file).toMatchObject({ fileName: 'r.pdf', pages: 2, browser: null })
      expect(Array.from(file.bytes)).toEqual(Array.from(bytes))
    })

    it('a refusal keeps the module\'s words (501 export_unavailable names the setting) and maps to the code of its status; a missing page count is null', async () => {
      const { client } = exporter([
        new Response(JSON.stringify({ code: 'export_unavailable', message: 'Export of the report is unavailable: it is off on this host (the views.export setting is not set).' }), { status: 501, headers: { 'content-type': 'application/json' } }),
        new Response(JSON.stringify({ message: 'No report.' }), { status: 404, headers: { 'content-type': 'application/json' } }),
        new Response(bytes, { status: 200, headers: { 'content-type': 'application/pdf' } }),
        new Error('connection refused'),
      ])
      const refused = (await client.exportPdf!(report).catch((x: unknown) => x)) as ViewsClientError
      expect([refused.code, refused.status]).toEqual(['unavailable', 501])
      expect(refused.message).toContain('views.export setting')
      expect(((await client.exportPdf!(report).catch((x: unknown) => x)) as ViewsClientError).code).toBe('not_found')
      expect((await client.exportPdf!(report)).pages).toBeNull()
      expect(((await client.exportPdf!(report).catch((x: unknown) => x)) as ViewsClientError).code).toBe('unavailable')
    })

    it('the fixture source has no exporter', () => {
      expect(fixtureClient(bundle).exportPdf).toBeUndefined()
    })
  })
})

describe('diagnosticsFromRefusal', () => {
  it('reads each finding of a publish refusal back: code, message, place in the text and key path', () => {
    expect(diagnosticsFromRefusal('d/x.yaml:3:5: error unknown_member: a.b is not a catalog member. [widgets.w.encodings.x] | d/x.yaml:9:1: error DBD-VIEW-043: No model. [widgets.v.query]', 'x.yaml')).toEqual([
      { code: 'unknown_member', severity: 'error', path: 'widgets.w.encodings.x', message: 'a.b is not a catalog member.', file: 'x.yaml', line: 3, column: 5 },
      { code: 'DBD-VIEW-043', severity: 'error', path: 'widgets.v.query', message: 'No model.', file: 'x.yaml', line: 9, column: 1 },
    ])
  })

  it('a finding with no place is kept without one, a loader refusal loses its lead-in, and a text it cannot read is kept whole', () => {
    expect(diagnosticsFromRefusal('error DBD-VIEW-040: The model `m` has no version 3.')).toEqual([{ code: 'DBD-VIEW-040', severity: 'error', path: '', message: 'The model `m` has no version 3.', file: undefined }])
    expect(diagnosticsFromRefusal('The dashboard does not load: d.yaml:2:1: error DBD-106: bad.')[0]).toMatchObject({ code: 'DBD-106', line: 2, column: 1, message: 'bad.' })
    expect(diagnosticsFromRefusal('something went wrong')).toEqual([{ code: 'DBD-VIEW-SERVER', severity: 'error', path: '', message: 'something went wrong', file: undefined }])
  })
})
