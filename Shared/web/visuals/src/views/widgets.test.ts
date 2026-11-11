import { readFileSync } from 'node:fs'
import { join } from 'node:path'
import { describe, expect, it } from 'vitest'
import coreSchema from '../../schemas/core.schema.json'
import viewSchema from '../../schemas/view.schema.json'
import { isSafeTextLink } from './common'
import { validateWidgets } from './widgets'
import type { DrillTarget } from './widgets'

/** The widget maps shared with the .NET suite (`DbDataKit.Views.Tests`): `Shared/conformance/views/widgets`. */
const root = join(import.meta.dirname, '../../../../conformance/views/widgets')
const read = (relative: string) => JSON.parse(readFileSync(join(root, relative), 'utf8')) as Record<string, any>
const cases = read('cases.json') as {
  good: { file: string; parameters: string[] }[]
  bad: { name: string; file: string; parameters: string[]; code: string; path: string; targets?: Record<string, DrillTarget[]> }[]
}
const summary = (issues: { code: string; path: string }[]) => issues.map((i) => `${i.code} ${i.path}`)

describe('good widget maps', () => {
  for (const c of cases.good)
    it(`${c.file} is valid`, () => {
      expect(validateWidgets(read(c.file), c.parameters)).toEqual([])
    })
})

describe('bad widget maps', () => {
  for (const c of cases.bad)
    it(`${c.name} is refused as ${c.code} at ${c.path}`, () => {
      const issues = validateWidgets(read(c.file), c.parameters, 'widgets', c.targets)
      expect(issues.some((i) => i.code === c.code && i.path === c.path), JSON.stringify(issues, null, 1)).toBe(true)
      expect(issues.length, JSON.stringify(issues, null, 1)).toBeLessThanOrEqual(2)
    })
})

describe('the widget vocabulary', () => {
  const widgets = read('good/dashboard-widgets.json')
  const params = ['region', 'quarter']
  const refuse = (change: (w: Record<string, any>) => void) => { const copy = structuredClone(widgets); change(copy); return summary(validateWidgets(copy, params)) }

  it('refuses an image by URL and an SVG, raw HTML in text, a Vega expression, a transform and an aggregate, an unknown key and embed', () => {
    expect(refuse((w) => { w.logo.url = 'https://example.com/a.png' })).toEqual(['DBD-VIEW-003 widgets.logo.url'])
    expect(refuse((w) => { w.logo.media_type = 'image/svg+xml' })).toEqual(['DBD-VIEW-003 widgets.logo.media_type'])
    expect(refuse((w) => { w.note.text = '<img src=x onerror=alert(1)>' })).toEqual(['DBD-VIEW-002 widgets.note.text'])
    expect(refuse((w) => { w.by_region.encodings.y.expr = 'datum.a' })).toEqual(['DBD-VIEW-004 widgets.by_region.encodings.y.expr'])
    expect(refuse((w) => { w.by_region.transform = [] })).toEqual(['DBD-VIEW-004 widgets.by_region.transform'])
    expect(refuse((w) => { w.by_region.encodings.y.aggregate = 'sum' })).toEqual(['DBD-VIEW-004 widgets.by_region.encodings.y.aggregate'])
    expect(refuse((w) => { w.by_region.colour = 'red' })).toEqual(['DBD-104 widgets.by_region.colour'])
    expect(refuse((w) => { w.x = { type: 'embed' } })).toEqual(['DBD-VIEW-006 widgets.x.type'])
  })
  it('refuses a missing required key by the shared code', () => {
    expect(refuse((w) => { delete w.logo.alt })).toEqual(['DBD-105 widgets.logo.alt'])
    expect(refuse((w) => { delete w.revenue.encodings.value })).toEqual(['DBD-105 widgets.revenue.encodings.value'])
  })
  it('a derived query has nothing to check: the request is made from the encodings', () => {
    const derived = structuredClone(widgets)
    derived.revenue.encodings.value = 'anything.at_all'
    expect(validateWidgets(derived, params)).toEqual([])
  })
  it('a link in text may only stay on the host', () => {
    expect(isSafeTextLink('/docs/glossary')).toBe(true)
    for (const bad of ['https://example.com', '//example.com', 'javascript:alert(1)', '/a b']) expect(isSafeTextLink(bad)).toBe(false)
  })
  it('a marker may be mapped by the query\'s params', () => {
    const w = { m: { type: 'metric', encodings: { value: 'a.b' }, query: { model: 's', filters: [{ attribute: 'a.c', operator: 'equals', values: [{ param: 'q' }] }], params: { q: 'quarter' } } } }
    expect(validateWidgets(w, ['quarter'])).toEqual([])
    expect(summary(validateWidgets(w, []))).toEqual(['DBD-VIEW-008 widgets.m.query.params.q'])
  })
})

describe('the schema of the widgets', () => {
  const props = (def: any) => Object.keys(def.properties).sort()
  it('the matrix and chart encodings are the view schema\'s own definitions', () => {
    const matrix = (viewSchema as any).$defs.matrix
    const encodings = (coreSchema as any).$defs.matrix_encodings
    expect(props(encodings)).toEqual(props(matrix).filter((p) => p !== 'type' && p !== 'id'))
    // the axes and the values carry a format (DBR41), so they are the core's own definitions; the rest is the view's, referenced
    for (const p of props(encodings).filter((k) => !['rows', 'columns', 'values'].includes(k))) expect(encodings.properties[p].$ref).toBe(`${viewSchema.$id}#/$defs/matrix/properties/${p}`)
    expect(encodings.properties.rows.$ref).toBe('#/$defs/matrix_axis')
    expect(encodings.properties.columns.$ref).toBe('#/$defs/matrix_axis')
    expect(encodings.properties.values.$ref).toBe('#/$defs/matrix_values')
  })
})
