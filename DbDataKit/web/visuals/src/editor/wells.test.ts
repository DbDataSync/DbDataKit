import { readFileSync } from 'node:fs'
import { join } from 'node:path'
import { describe, expect, it } from 'vitest'
import { parse } from 'yaml'
import { fixtureClient } from '../client/fixtureClient'
import { catalogDiagnostics, catalogFromModel, catalogRoles, retailCatalog } from '../views/catalog'
import { deriveRequest } from '../views/derive'
import type { CatalogRoles } from '../views/derive'
import type { ViewsDoc, WidgetDoc } from '../views/model'
import { sampleBundle } from '../testing/sampleBundle'
import { EditError } from './commands'
import { draftFromOpened, edit, redo, undo } from './draft'
import { localFileStore } from './store'
import { bindingIssues, changedFields, convertWidget, derivedQuery, describeDerivation, explicitQuery, parseRequestText, wellFields, wellsOf, WIDGET_TYPES } from './wells'

const bundle = sampleBundle()
const TODAY = bundle.documents['dashboard/retail-sales-today']!
const roles = catalogRoles(retailCatalog)
const open = async () => draftFromOpened(await localFileStore(bundle.documents).open('dashboard', 'retail-sales-today'))
const must = (r: ReturnType<typeof edit>) => { expect(r.error?.message).toBeUndefined(); return r.draft }
const docOf = (d: { text: string }) => parse(d.text) as ViewsDoc

const goldens = join(import.meta.dirname, '../../../../conformance/views/derive')
const read = (relative: string) => JSON.parse(readFileSync(join(goldens, relative), 'utf8')) as Record<string, any>
const cases = read('cases.json') as { catalogs: Record<string, CatalogRoles>; good: { name: string; file: string }[] }

describe('the wells of each widget type', () => {
  it('has a well list for every type, and the wells the derivation reads', () => {
    expect(WIDGET_TYPES).toHaveLength(7)
    expect(wellsOf('metric').map((w) => w.id)).toEqual(['value', 'compare'])
    expect(wellsOf('chart').map((w) => w.id)).toEqual(['x', 'y', 'color', 'column', 'row', 'tooltip'])
    expect(wellsOf('matrix').map((w) => [w.id, w.kind])).toEqual([['rows', 'attribute'], ['columns', 'attribute'], ['values', 'measure']])
    expect(wellsOf('table').map((w) => w.id)).toEqual(['columns'])
    expect(wellsOf('control').map((w) => w.id)).toEqual(['attribute'])
    expect(wellsOf('text')).toEqual([])
  })

  for (const g of cases.good)
    it(`${g.name}: every field the golden request asks for is in a well (a table sort field apart)`, () => {
      const c = read(g.file)
      const widget = c.widgets.w as WidgetDoc
      const inWells = new Set(wellsOf(widget.type).flatMap((spec) => wellFields(widget, spec)))
      const sortField = (widget as any).encodings?.sort?.by
      const q = c.request.query as { measures: string[]; attributes?: string[] }
      for (const f of [...q.measures, ...(q.attributes ?? [])]) if (f !== sortField && !((widget as any).query?.order ?? []).some((o: unknown[]) => o[0] === f)) expect(inWells.has(f), f).toBe(true)
    })
})

describe('a well change', () => {
  it('replaces the field of a single well, keeps the key order, and is one undo step', async () => {
    const draft = await open()
    const next = must(edit(draft, { type: 'setWell', widget: 'net', well: 'value', change: { op: 'add', field: 'sales.gross_amount' }, roles }))
    expect(docOf(next).widgets.net).toMatchObject({ encodings: { value: 'sales.gross_amount' } })
    expect(undo(next).text).toBe(draft.text)
    expect(redo(undo(next)).text).toBe(next.text)
  })

  it('adds to a list well once, inserts at a position, moves, removes, clears', async () => {
    let d = await open()
    d = must(edit(d, { type: 'setWell', widget: 'top_products', well: 'columns', change: { op: 'add', field: 'stores.store' }, roles }))
    d = must(edit(d, { type: 'setWell', widget: 'top_products', well: 'columns', change: { op: 'add', field: 'stores.store' }, roles }))
    const table = () => docOf(d).widgets.top_products as any
    expect(table().encodings.columns.map((c: any) => c.field)).toEqual(['products.product', 'sales.units', 'stores.store'])
    // a column keeps its own label when it moves, and a new one is a bare field
    d = must(edit(d, { type: 'setWell', widget: 'top_products', well: 'columns', change: { op: 'move', from: 2, to: 0 }, roles }))
    expect(table().encodings.columns.map((c: any) => c.field)).toEqual(['stores.store', 'products.product', 'sales.units'])
    expect(table().encodings.columns[2]).toMatchObject({ label: 'Units', align: 'right' })
    d = must(edit(d, { type: 'setWell', widget: 'top_products', well: 'columns', change: { op: 'add', field: 'sales.net_amount', index: 1 }, roles }))
    expect(table().encodings.columns[1]).toEqual({ field: 'sales.net_amount' })
    d = must(edit(d, { type: 'setWell', widget: 'top_products', well: 'columns', change: { op: 'remove', index: 0 }, roles }))
    expect(table().encodings.columns.map((c: any) => c.field)).toEqual(['sales.net_amount', 'products.product', 'sales.units'])
    d = must(edit(d, { type: 'setWell', widget: 'top_products', well: 'columns', change: { op: 'clear' }, roles }))
    expect(table().encodings.columns).toBeUndefined()
  })

  it('writes a one-field tooltip as the field and more as a list; removing the last takes the key away', async () => {
    let d = await open()
    const chart = () => (docOf(d).widgets.by_region as any).encodings
    d = must(edit(d, { type: 'setWell', widget: 'by_region', well: 'tooltip', change: { op: 'add', field: 'sales.units' }, roles }))
    expect(chart().tooltip).toBe('sales.units')
    d = must(edit(d, { type: 'setWell', widget: 'by_region', well: 'tooltip', change: { op: 'add', field: 'sales.order_count' }, roles }))
    expect(chart().tooltip).toEqual(['sales.units', 'sales.order_count'])
    d = must(edit(d, { type: 'setWell', widget: 'by_region', well: 'tooltip', change: { op: 'clear' }, roles }))
    expect('tooltip' in chart()).toBe(false)
  })

  it('refuses a dimension in a measure well in the engine\'s words, and changes nothing', async () => {
    const draft = await open()
    const r = edit(draft, { type: 'setWell', widget: 'net', well: 'value', change: { op: 'add', field: 'customers.region' }, roles })
    expect(r.error).toBeInstanceOf(EditError)
    expect(r.error).toMatchObject({ code: 'wrong_member_kind', message: 'customers.region is a attribute, not a measure.' })
    expect(r.draft.text).toBe(draft.text)
    // the derivation says the same words for the same field
    const wrong = { id: 'w', raw: { type: 'metric', encodings: { value: 'customers.region' }, query: { model: 'retail_sales' } } }
    expect(deriveRequest(wrong, roles).issues[0]).toMatchObject({ code: 'wrong_member_kind', message: 'customers.region is a attribute, not a measure.' })
  })

  it('refuses a measure in a matrix axis; takes a measure or an attribute in a chart channel', () => {
    const spec = wellsOf('matrix')[0]!
    expect(() => changedFields([], spec, { op: 'add', field: 'sales.net_amount' }, roles)).toThrow('sales.net_amount is a measure, not a attribute.')
    const color = wellsOf('chart')[2]!
    expect(changedFields([], color, { op: 'add', field: 'sales.net_amount' }, roles)).toEqual(['sales.net_amount'])
    expect(() => changedFields([], color, { op: 'add', field: '  ' }, roles)).toThrow('Name a field')
  })

  it('places a field the catalog lacks and flags it unknown_member through the derivation', async () => {
    let d = await open()
    d = must(edit(d, { type: 'setWell', widget: 'by_region', well: 'color', change: { op: 'add', field: 'products.colour' }, roles }))
    expect((docOf(d).widgets.by_region as any).encodings.color).toBe('products.colour')
    const derived = deriveRequest({ id: 'by_region', raw: docOf(d).widgets.by_region as any }, roles)
    expect(derived.issues).toEqual([expect.objectContaining({ code: 'unknown_member', path: 'widgets.by_region.encodings.color', message: 'products.colour is not a catalog member.' })])
  })

  it('refuses an unknown well and a position that is not there', async () => {
    const draft = await open()
    expect(edit(draft, { type: 'setWell', widget: 'net', well: 'x', change: { op: 'clear' } }).error).toMatchObject({ code: 'unknown_well' })
    expect(edit(draft, { type: 'setWell', widget: 'top_products', well: 'columns', change: { op: 'remove', index: 9 } }).error).toMatchObject({ code: 'bad_position' })
    expect(edit(draft, { type: 'setWell', widget: 'nope', well: 'x', change: { op: 'clear' } }).error).toMatchObject({ code: 'unknown_widget' })
  })

  it('a control\'s attribute well sets and clears values.attribute', async () => {
    let d = await open()
    d = must(edit(d, { type: 'setWell', widget: 'regions', well: 'attribute', change: { op: 'add', field: 'stores.region' }, roles }))
    expect((docOf(d).widgets.regions as any).values).toEqual({ attribute: 'stores.region', limit: 20 })
    expect(edit(d, { type: 'setWell', widget: 'regions', well: 'attribute', change: { op: 'add', field: 'sales.units' }, roles }).error).toMatchObject({ code: 'wrong_member_kind' })
    d = must(edit(d, { type: 'setWell', widget: 'regions', well: 'attribute', change: { op: 'clear' }, roles }))
    expect((docOf(d).widgets.regions as any).values).toEqual({ limit: 20 })
  })
})

describe('the type chooser', () => {
  const ctx = { roles, model: 'retail_sales' }
  const table = (docOf({ text: TODAY }).widgets.top_products) as WidgetDoc

  it('turns a table into a chart: the attribute to x, the measure to y, the title and filters kept, the table\'s order and limit gone', () => {
    const { widget, dropped } = convertWidget(table, 'chart', ctx)
    expect(widget).toMatchObject({ type: 'chart', title: 'Best-selling products', mark: 'bar', encodings: { x: 'products.product', y: 'sales.units' }, query: { model: 'retail_sales', filters: [{ attribute: 'customers.region' }] } })
    expect((widget as any).query.order).toBeUndefined()
    expect(dropped).toEqual([])
  })

  it('turns a chart into a metric: the title becomes the label, the measure the value, the attributes are reported as dropped', () => {
    const chart = docOf({ text: TODAY }).widgets.by_region as WidgetDoc
    const { widget, dropped } = convertWidget(chart, 'metric', ctx)
    expect(widget).toMatchObject({ type: 'metric', label: 'Net sales by region and category', encodings: { value: 'sales.net_amount' } })
    expect(dropped).toEqual(['customers.region', 'products.category'])
  })

  it('turns a metric into a table and a matrix; every result is a valid widget shape for its type', async () => {
    const metric = docOf({ text: TODAY }).widgets.net as WidgetDoc
    expect(convertWidget(metric, 'table', ctx).widget).toMatchObject({ type: 'table', encodings: { columns: [{ field: 'sales.net_amount' }] } })
    expect(convertWidget(metric, 'matrix', ctx).widget).toMatchObject({ type: 'matrix', encodings: { values: [{ field: 'sales.net_amount' }] } })
    const chart = docOf({ text: TODAY }).widgets.by_region as WidgetDoc
    expect(convertWidget(chart, 'matrix', ctx).widget).toMatchObject({ encodings: { rows: ['customers.region', 'products.category'], values: [{ field: 'sales.net_amount' }] } })
  })

  it('changes the type as one command and one undo step; a control needs a parameter', async () => {
    const draft = await open()
    const next = must(edit(draft, { type: 'changeWidgetType', id: 'top_products', to: 'chart', roles }))
    expect((docOf(next).widgets.top_products as any).type).toBe('chart')
    expect(undo(next).text).toBe(draft.text)
    expect(must(edit(draft, { type: 'changeWidgetType', id: 'net', to: 'control', roles }) ).text).toContain('type: control')
    const noParameters = must(edit(draft, { type: 'removeParameter', name: 'region' }))
    expect(edit(noParameters, { type: 'changeWidgetType', id: 'net', to: 'control' }).error).toMatchObject({ code: 'unknown_parameter', message: expect.stringContaining('add a parameter first') })
  })

  it('keeps the same fields when nothing changes type', () => {
    expect(convertWidget(table, 'table', ctx)).toEqual({ widget: table, dropped: [] })
  })
})

describe('the derived request panel', () => {
  it('shows the request, the parameter bindings and where each part comes from in the document', () => {
    const doc = docOf({ text: TODAY })
    const d = describeDerivation('net', doc.widgets.net!, roles)
    expect(d.state).toBe('request')
    expect(d.request).toMatchObject({ kind: 'query', query: { measures: ['sales.net_amount'], limit: 1 } })
    expect(d.bindings).toEqual([{ request: 'region', document: 'region' }])
    expect(d.sources).toContainEqual({ requestPath: '$.query.measures[0]', documentPath: 'widgets.net.encodings.value' })
    expect(d.sources).toContainEqual({ requestPath: '$.query.filters[0]', documentPath: 'widgets.net.query.filters[0]' })
    expect(JSON.parse(d.text!)).toEqual(d.request)
    const chart = describeDerivation('by_region', doc.widgets.by_region!, roles)
    expect(chart.sources).toContainEqual({ requestPath: '$.query.attributes[1]', documentPath: 'widgets.by_region.encodings.color' })
    expect(chart.sources).toContainEqual({ requestPath: '$.query.order[0]', documentPath: 'widgets.by_region.query.order[0]' })
  })

  it('says none for a widget that asks for nothing, refused with the engine\'s words for a wrong field, explicit for a written query', () => {
    const doc = docOf({ text: TODAY })
    expect(describeDerivation('note', doc.widgets.note!, roles).state).toBe('none')
    const bad = describeDerivation('w', { type: 'matrix', encodings: { rows: ['sales.units'], values: [{ field: 'sales.net_amount' }] }, query: { model: 'retail_sales' } } as WidgetDoc, roles)
    expect(bad.state).toBe('refused')
    expect(bad.issues[0]).toMatchObject({ code: 'wrong_member_kind', message: 'sales.units is a measure, not a attribute.', path: 'widgets.w.encodings.rows[0]' })
    const written = explicitQuery('net', doc.widgets.net!, roles)
    expect(written).toMatchObject({ mode: 'explicit', semantic: { model: 'retail_sales', request: { measures: ['sales.net_amount'] } } })
    expect(describeDerivation('net', { ...doc.widgets.net!, query: written } as WidgetDoc, roles).state).toBe('explicit')
  })

  it('the written query is the same request as the derived one, and goes back to derived with its model', () => {
    const doc = docOf({ text: TODAY })
    const widget = doc.widgets.by_region!
    const written = { ...widget, query: explicitQuery('by_region', widget, roles) } as WidgetDoc
    expect((written as any).query.semantic.request).toEqual((describeDerivation('by_region', widget, roles).request as any).query)
    expect(derivedQuery(written)).toEqual({ model: 'retail_sales' })
  })

  it('explicit mode with an output missing an encoded field shows the binding message at the field', () => {
    const doc = docOf({ text: TODAY })
    const widget = doc.widgets.by_region!
    const written = explicitQuery('by_region', widget, roles) as any
    expect(bindingIssues('by_region', { ...widget, query: written } as WidgetDoc)).toEqual([])
    written.semantic.request.attributes = ['customers.region']
    const issues = bindingIssues('by_region', { ...widget, query: written } as WidgetDoc)
    expect(issues).toEqual([{ code: 'DBD-VIEW-005', path: 'widgets.by_region.encodings.color', message: '`products.category` is encoded as color but is not an output of the query (it gives customers.region, sales.net_amount).' }])
  })

  it('reads a request typed by hand', () => {
    expect(parseRequestText('{"measures": ["a"]}')).toEqual({ value: { measures: ['a'] } })
    expect(parseRequestText('[1]')).toEqual({ error: 'A semantic request is a JSON object.' })
    expect('error' in parseRequestText('{oops')).toBe(true)
  })
})

describe('the catalog', () => {
  it('knows every field the two sample documents encode', () => {
    for (const key of ['dashboard/retail-sales-today', 'report/retail-sales-monthly']) {
      const doc = parse(bundle.documents[key]!) as ViewsDoc
      for (const [id, widget] of Object.entries(doc.widgets)) {
        const d = describeDerivation(id, widget, roles)
        if (d.state !== 'none' && d.state !== 'explicit') expect(d.issues, `${key} ${id}`).toEqual([])
      }
    }
  })

  it('reads the Proxy\'s model JSON in its two shapes and the retail model\'s own file shape', () => {
    const fromMap = catalogFromModel({ name: 'm', measures: { 'sales.net': { synonyms: ['net sales'] } }, dimensions: { customers: { attributes: { region: { type: 'string' }, country: {} } } } })
    expect(fromMap).toEqual({ model: 'm', measures: [{ name: 'sales.net', label: 'net sales', description: undefined }], attributes: [{ name: 'customers.region', label: undefined, description: undefined }, { name: 'customers.country', label: undefined, description: undefined }] })
    const fromLists = catalogFromModel({ name: 'm', measures: [{ name: 'sales.net', label: 'Net' }, 'sales.units'], dimensions: [{ name: 'date', attributes: ['year'] }] })
    expect(catalogRoles(fromLists!)).toEqual({ model: 'm', measures: ['sales.net', 'sales.units'], attributes: ['date.year'] })
    expect(catalogFromModel({ name: 'empty' })).toBeNull()
    expect(catalogFromModel(7)).toBeNull()
  })

  it('names measures and attributes apart in the roles', () => {
    expect(roles.measures).toContain('sales.margin_rate')
    expect(roles.attributes).toContain('date.year_month')
    expect(roles.measures.some((m) => roles.attributes.includes(m))).toBe(false)
  })
})

describe('what only the server can check goes through ViewsClient.validate', () => {
  it('the fixture client adds the hook\'s diagnostics, and fails the validation on an error', async () => {
    const calls: string[] = []
    const client = fixtureClient(bundle, { catalogs: { retail_sales: retailCatalog }, validateHook: (d, text) => { calls.push(text); return catalogDiagnostics(d, { retail_sales: retailCatalog }) } })
    const good = await client.validate(TODAY, { kind: 'dashboard' })
    expect(good.ok).toBe(true)
    expect(calls).toHaveLength(1)
    const bad = TODAY.replace('order: [[sales.units, desc]]', 'order: [[sales.unitz, desc]]').replace('attribute: customers.region, operator: equals, values: {param: region}}]\n      order', 'attribute: customers.regoin, operator: equals, values: {param: region}}]\n      order')
    const result = await client.validate(bad, { kind: 'dashboard' })
    expect(result.ok).toBe(false)
    expect(result.diagnostics.filter((d) => d.code === 'unknown_member').map((d) => [d.path, d.message])).toEqual([
      ['widgets.top_products.query.filters[0].attribute', 'customers.regoin is not a catalog member.'],
      ['widgets.top_products.query.order[0]', 'sales.unitz is not a catalog member.'],
    ])
  })

  it('a filter on a measure is wrong_member_kind, in the engine\'s words', async () => {
    const parsed = parse(TODAY) as ViewsDoc
    ;(parsed.widgets.net as any).query.filters[0].attribute = 'sales.units'
    expect(catalogDiagnostics(parsed, { retail_sales: retailCatalog })).toEqual([{ code: 'wrong_member_kind', severity: 'error', path: 'widgets.net.query.filters[0].attribute', message: 'sales.units is a measure, not a attribute.' }])
  })

  it('serves a catalog by model name, and refuses a model it does not know', async () => {
    const client = fixtureClient(bundle, { catalogs: { retail_sales: retailCatalog } })
    expect(await client.catalog!('retail_sales')).toBe(retailCatalog)
    await expect(client.catalog!('nope')).rejects.toMatchObject({ code: 'not_found' })
  })
})

describe('a layout\'s widget_options', () => {
  it('sets one option, keeps the others, and removes the entry and the key when the last one goes', async () => {
    let d = await open()
    const options = () => (docOf(d).layouts[0] as any).widget_options
    d = must(edit(d, { type: 'setWidgetOption', layout: 'desk', widget: 'net', key: 'density', value: 'compact' }))
    d = must(edit(d, { type: 'setWidgetOption', layout: 'desk', widget: 'net', key: 'show_title', value: false }))
    expect(options().net).toEqual({ density: 'compact', show_title: false })
    d = must(edit(d, { type: 'setWidgetOption', layout: 'desk', widget: 'by_region', key: 'legend', value: 'hide' }))
    d = must(edit(d, { type: 'setWidgetOption', layout: 'desk', widget: 'net', key: 'density', value: undefined }))
    d = must(edit(d, { type: 'setWidgetOption', layout: 'desk', widget: 'net', key: 'show_title', value: undefined }))
    expect(options()).toEqual({ by_region: { legend: 'hide' } })
    d = must(edit(d, { type: 'setWidgetOption', layout: 'desk', widget: 'by_region', key: 'legend', value: undefined }))
    expect(options()).toBeUndefined()
    expect(edit(d, { type: 'setWidgetOption', layout: 'nope', widget: 'net', key: 'density', value: 'compact' }).error).toMatchObject({ code: 'unknown_layout' })
  })
})
