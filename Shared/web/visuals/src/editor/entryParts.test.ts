import { readFileSync } from 'node:fs'
import { join } from 'node:path'
import { describe, expect, it } from 'vitest'
import { parse } from 'yaml'
import type { CatalogRoles } from '../views/derive'
import { deriveRequest } from '../views/derive'
import { ICON_NAMES } from '../views/icons'
import { ICON_GLYPHS } from '../CellContent'
import coreSchema from '../../schemas/core.schema.json'
import type { ViewsDoc, WidgetDoc } from '../views/model'
import { sampleBundle } from '../testing/sampleBundle'
import { draftFromOpened, edit } from './draft'
import { localFileStore } from './store'
import { entryChange, entryFilterChange, entryIconChange, entryProblems, entryRefs, groupDefsOf, groupProblems, groupUses, linkNamesOf } from './entryParts'
import { repeatedAlias, wellFields, wellOps, wellsOf } from './wells'

const bundle = sampleBundle()
const open = async () => draftFromOpened(await localFileStore(bundle.documents).open('dashboard', 'retail-sales-today'))
const must = (r: ReturnType<typeof edit>) => { expect(r.error?.message).toBeUndefined(); return r.draft }
const goldens = join(import.meta.dirname, '../../../../conformance/views/derive')
const read = (file: string) => JSON.parse(readFileSync(join(goldens, file), 'utf8')) as { widgets: { w: WidgetDoc }; groups?: Record<string, unknown> }
const roles = (JSON.parse(readFileSync(join(goldens, 'cases.json'), 'utf8')) as { catalogs: Record<string, CatalogRoles> }).catalogs.sales_catalog!

/** The widget as the saved text holds it. */
const widgetOf = (d: { text: string }, id = 'w') => (parse(d.text) as ViewsDoc).widgets[id] as WidgetDoc

describe('filtered and repeated measures through the wells (DBR50)', () => {
  const start = { type: 'chart', mark: 'bar', encodings: { x: 'products.category', y: 'orders.net_revenue' }, query: { model: 'sales_catalog', limit: 500 } } as unknown as WidgetDoc

  it('repeating a measure puts it in again under an alias of its own, and each copy keeps its own options through a move or a removal', () => {
    const ops = wellOps(start, 'w', wellsOf('chart').find((s) => s.id === 'y')!, { op: 'repeat', index: 0 }, roles)
    expect(ops).toEqual([{ op: 'set', path: ['widgets', 'w', 'encodings', 'y'], value: ['orders.net_revenue', { field: 'orders.net_revenue', alias: 'net_revenue_2' }] }])
    const twice = { ...start, encodings: { ...(start as { encodings: object }).encodings, y: [{ field: 'orders.net_revenue', alias: 'a', title: 'A' }, { field: 'orders.net_revenue', alias: 'b', title: 'B' }] } } as unknown as WidgetDoc
    const spec = wellsOf('chart').find((s) => s.id === 'y')!
    expect(wellOps(twice, 'w', spec, { op: 'move', from: 0, to: 1 }, roles)[0]).toMatchObject({ value: [{ alias: 'b' }, { alias: 'a' }] })
    expect(wellOps(twice, 'w', spec, { op: 'remove', index: 0 }, roles)[0]).toMatchObject({ value: { field: 'orders.net_revenue', alias: 'b', title: 'B' } })
    expect(wellFields(twice, spec)).toEqual(['orders.net_revenue', 'orders.net_revenue'])
  })

  it('names the alias a repeated measure gets: the last part of the name and the next number nothing has', () => {
    expect(repeatedAlias('orders.net_revenue', new Set())).toBe('net_revenue_2')
    expect(repeatedAlias('orders.net_revenue', new Set(['net_revenue_2', 'net_revenue_3']))).toBe('net_revenue_4')
  })

  it('a chart of two series of one measure built through the wells and the entry controls is the golden widget, and derives the golden request', async () => {
    const golden = read('good/chart-y-list-two-series-of-one-measure.json')
    let d = must(edit(await open(), { type: 'addWidget', id: 'w', widget: start }))
    d = must(edit(d, { type: 'setWell', widget: 'w', well: 'y', change: { op: 'repeat', index: 0 }, roles }))
    const set = (index: number, key: string, value: unknown) => {
      const w = widgetOf(d)
      const ref = entryRefs(w, roles).find((r) => r.well === 'y' && r.index === index)!
      const c = entryChange('w', w, ref, key, value)
      d = must(edit(d, { type: 'setPath', path: c.path, value: c.value }))
    }
    const filter = (index: number, values: string[]) => {
      const w = widgetOf(d)
      const ref = entryRefs(w, roles).find((r) => r.well === 'y' && r.index === index)!
      const c = entryFilterChange('w', w, ref, { op: 'add', filter: { attribute: 'orders.status', operator: 'equals', values } })
      d = must(edit(d, { type: 'setPath', path: c.path, value: c.value }))
    }
    set(0, 'alias', 'open_revenue'); filter(0, ['Open', 'Pending']); set(0, 'title', 'Open')
    set(1, 'alias', 'completed_revenue'); filter(1, ['Completed']); set(1, 'title', 'Completed')
    const built = widgetOf(d)
    expect(built).toEqual({ ...golden.widgets.w, query: { model: 'sales_catalog', limit: 500 } })
    expect(deriveRequest({ id: 'w', raw: built as never }, roles).issues).toEqual([])
  })

  it('an option goes back to a bare name when it is removed from a chart channel, and a table column stays an object', () => {
    const w = { type: 'chart', mark: 'bar', encodings: { x: 'a.b', y: { field: 'orders.net_revenue', alias: 'x_1' } } } as unknown as WidgetDoc
    const ref = entryRefs(w, roles).find((r) => r.well === 'y')!
    expect(entryChange('w', w, ref, 'alias', undefined)).toEqual({ path: ['widgets', 'w', 'encodings', 'y'], value: 'orders.net_revenue' })
    const table = { type: 'table', encodings: { columns: [{ field: 'orders.net_revenue', alias: 'a' }] } } as unknown as WidgetDoc
    const column = entryRefs(table, roles)[0]!
    expect(entryChange('w', table, column, 'alias', undefined)).toEqual({ path: ['widgets', 'w', 'encodings', 'columns', 0], value: { field: 'orders.net_revenue' } })
    expect(entryFilterChange('w', table, column, { op: 'remove', index: 0 }).value).toEqual({ field: 'orders.net_revenue', alias: 'a' })
  })

  it('says what a bad alias or filter list is, in the loaders\' words', () => {
    expect(entryProblems({ field: 'a.b', alias: 'Open Revenue' }, false).alias).toContain('snake_case')
    expect(entryProblems({ field: 'a.b', alias: 'ok_1' }, false)).toEqual({})
  })
})

describe('attributes in the cell area through the wells (DBR51)', () => {
  it('a matrix with a status per month column, built through the values well and the entry controls, is the golden widget', async () => {
    const golden = read('good/matrix-attribute-in-the-cell-area.json')
    const start = { type: 'matrix', encodings: { rows: ['customers.region'], columns: ['orders.ordered_at_month'], values: [] }, query: { model: 'sales_catalog', limit: 10000 } } as unknown as WidgetDoc
    let d = must(edit(await open(), { type: 'addWidget', id: 'w', widget: { ...start, encodings: { rows: ['customers.region'], columns: ['orders.ordered_at_month'], values: [{ field: 'orders.net_revenue' }] } } as unknown as WidgetDoc }))
    d = must(edit(d, { type: 'setWell', widget: 'w', well: 'values', change: { op: 'add', field: 'orders.status' }, roles }))
    expect((parse(d.text) as ViewsDoc).widgets.w).toMatchObject({ encodings: { values: [{ field: 'orders.net_revenue' }, { attribute: 'orders.status' }] } })
    const set = (key: string, value: unknown) => {
      const w = widgetOf(d)
      const ref = entryRefs(w, roles).find((r) => r.well === 'values' && r.index === 1)!
      const c = entryChange('w', w, ref, key, value)
      d = must(edit(d, { type: 'setPath', path: c.path, value: c.value }))
    }
    const icon = (value: string, name: string) => {
      const w = widgetOf(d)
      const ref = entryRefs(w, roles).find((r) => r.well === 'values' && r.index === 1)!
      const c = entryIconChange('w', w, ref, value, name)
      d = must(edit(d, { type: 'setPath', path: c.path, value: c.value }))
    }
    icon('Open', 'circle'); icon('Completed', 'check'); set('display', 'icon'); set('many', 'mixed'); set('link', 'set-status'); set('label', 'Status')
    const built = widgetOf(d) as unknown as { encodings: { values: unknown[] } }
    expect(built.encodings.values).toEqual((golden.widgets.w as unknown as { encodings: { values: unknown[] } }).encodings.values)
    expect(entryProblems(built.encodings.values[1], true)).toEqual({})
  })

  it('refuses in the loaders\' words: an icon display with no icons, an icon outside the vocabulary, many on a table', () => {
    expect(entryProblems({ attribute: 'a.b', display: 'icon' }, true).display).toContain('needs `icons`')
    expect(entryProblems({ attribute: 'a.b', display: 'icon', icons: { Open: 'smiley' } }, true).icons).toContain('An icon is one of')
    expect(entryProblems({ attribute: 'a.b', display: 'badge', icons: { Open: 'star' } }, true).icons).toContain('belong to `display: icon`')
    expect(entryProblems({ field: 'a.b', many: 'first' }, false).many).toContain('a table row has one value')
  })

  it('knows the vocabulary: the same icons as the core schema and the client\'s glyphs', () => {
    expect([...ICON_NAMES].sort()).toEqual([...(coreSchema.$defs as unknown as { icon: { enum: string[] } }).icon.enum].sort())
    expect(Object.keys(ICON_GLYPHS).sort()).toEqual([...ICON_NAMES].sort())
  })

  it('lists the links of the document as the choices of an entry\'s link', () => {
    expect(linkNamesOf({ links: { 'set-status': {}, other: {} } } as unknown as ViewsDoc)).toEqual(['set-status', 'other'])
  })
})

describe('field groups through the wells (DBR52)', () => {
  it('a line chart over a group built through the x well is the golden widget, and the groups of the document derive the golden request', async () => {
    const golden = read('good/chart-line-over-a-group.json')
    let d = must(edit(await open(), { type: 'addWidget', id: 'w', widget: { type: 'chart', mark: 'line', encodings: { y: 'orders.net_revenue' }, query: { model: 'sales_catalog', limit: 500 } } as unknown as WidgetDoc }))
    d = must(edit(d, { type: 'setPath', path: ['groups', 'period'], value: golden.groups!.period }))
    d = must(edit(d, { type: 'setWell', widget: 'w', well: 'x', change: { op: 'add', field: '@period' }, roles }))
    const built = widgetOf(d)
    expect(built).toEqual({ ...golden.widgets.w, query: { model: 'sales_catalog', limit: 500 } })
    const doc = parse(d.text) as ViewsDoc
    expect(entryRefs(built, roles).filter((r) => r.kind === 'group').map((r) => [r.well, r.field])).toEqual([['x', '@period']])
    const groups = groupDefsOf(doc)
    expect(Object.keys(groups)).toEqual(['period'])
    expect(deriveRequest({ id: 'w', raw: built as never, groups }, roles).issues).toEqual([])
    expect(groupUses(doc, 'period')).toEqual(['w'])
    expect(groupUses(doc, 'other')).toEqual([])
  })

  it('the details of the rows are `@name#always`, kept, moved and removed like a field', () => {
    const matrix = { type: 'matrix', encodings: { rows: ['customers.region', { group: 'details', show: 'always' }], values: [{ field: 'orders.net_revenue' }] }, query: { model: 'sales' } } as unknown as WidgetDoc
    const spec = wellsOf('matrix').find((s) => s.id === 'rows')!
    expect(wellFields(matrix, spec)).toEqual(['customers.region', '@details#always'])
    expect(wellOps(matrix, 'w', spec, { op: 'move', from: 1, to: 0 }, roles)[0]).toMatchObject({ value: [{ group: 'details', show: 'always' }, 'customers.region'] })
    expect(wellOps(matrix, 'w', spec, { op: 'remove', index: 0 }, roles)[0]).toMatchObject({ value: [{ group: 'details', show: 'always' }] })
    expect(wellOps({ ...matrix, encodings: { rows: ['customers.region'], values: [] } } as unknown as WidgetDoc, 'w', spec, { op: 'add', field: '@details#always' }, roles)[0]).toMatchObject({ value: ['customers.region', { group: 'details', show: 'always' }] })
  })

  it('says what is wrong with a group as it is typed, in the loaders\' words', () => {
    expect(groupProblems('period', { fields: ['a.b'] })[0]).toContain('must NOT have fewer than 2 items')
    expect(groupProblems('period', { fields: ['a.b', 'a.b'] })).toContain('A field is listed once in a group.')
    expect(groupProblems('period', { fields: ['a.b', 'a.c'], template: '{1} {3}' })[0]).toContain('names a level the group does not have')
    expect(groupProblems('period', { fields: ['a.b', 'a.c'], template: '{2} {1}' })).toEqual([])
  })
})
