import { readFileSync } from 'node:fs'
import { join } from 'node:path'
import { describe, expect, it } from 'vitest'
import { deriveExpansion, deriveRequest, normaliseRequestPath } from './derive'
import type { CatalogRoles } from './derive'
import { validateWidgets } from './widgets'
import { Issues } from './common'
import { checkGroups } from './groups'

/** The derivation goldens shared with the .NET suite (`DbDataKit.Views.Tests`, `DeriveTests`): `Shared/conformance/views/derive`. */
const root = join(import.meta.dirname, '../../../../conformance/views/derive')
const read = (relative: string) => JSON.parse(readFileSync(join(root, relative), 'utf8')) as Record<string, any>
const cases = read('cases.json') as { catalogs: Record<string, CatalogRoles>; good: { name: string; file: string }[]; refused: { name: string; file: string }[] }
const roles = cases.catalogs.sales_catalog!

const widgetOf = (c: Record<string, any>, key = 'widgets') => {
  // the same shape check the .NET loader makes before anything is derived, over the document's groups when the golden has them
  const issues = new Issues()
  const groups = c.groups === undefined ? undefined : checkGroups(c.groups, issues)
  expect(issues.list).toEqual([])
  expect(validateWidgets(c[key], c.parameters, 'widgets', undefined, groups)).toEqual([])
  return { id: 'w', raw: c[key].w as Record<string, any>, groups }
}

describe('the goldens of DeriveRequest', () => {
  for (const g of cases.good)
    it(`${g.name} gives exactly the golden request, key for key`, () => {
      const c = read(g.file)
      const derived = deriveRequest(widgetOf(c), roles)
      expect(derived.issues).toEqual([])
      expect(JSON.stringify(derived.request)).toBe(JSON.stringify(c.request))
      expect(derived.bindings).toEqual(c.bindings ?? [])
    })

  for (const g of cases.good.filter((g) => read(g.file).equivalent))
    it(`${g.name}: the hand-written equivalent is the same request`, () => {
      const c = read(g.file)
      const written = (c.equivalent.w.query.semantic.request) as Record<string, any>
      expect(deriveRequest(widgetOf(c), roles).request).toEqual({ spec_version: '0.3', kind: 'query', query: written })
    })

  for (const g of cases.refused)
    it(`${g.name} is refused with the golden codes in the golden places`, () => {
      const c = read(g.file)
      const derived = deriveRequest(widgetOf(c), roles)
      expect(derived.request).toBeUndefined()
      expect(derived.issues.map((i) => [i.code, i.path])).toEqual(c.refusal.map((r: any) => [r.code, r.path]))
      c.refusal.forEach((r: any, i: number) => { if (r.message) expect(derived.issues[i]!.message).toBe(r.message) })
    })
})

describe('where a place of the request comes from in the document', () => {
  const c = read('good/chart-dimension-measure-color.json')
  const derived = deriveRequest(widgetOf(c), roles)
  it('maps an entry to the field that asked for it, and a place inside an entry to its entry', () => {
    expect(derived.documentPath('$.query.measures[0]')).toBe('widgets.w.encodings.y')
    expect(derived.documentPath('$.query.attributes[1]')).toBe('widgets.w.encodings.color')
    expect(derived.documentPath('$.query.filters[0].attribute')).toBe('widgets.w.query.filters[0].attribute')
    expect(derived.documentPath('$.query.order[0]')).toBe('widgets.w.query.order[0]')
    expect(derived.documentPath('$.query.limit')).toBe('widgets.w.query.limit')
  })
  it('reads the engine\'s two ways of naming a place', () => {
    expect(normaliseRequestPath('$/query/filters/0')).toBe('$.query.filters[0]')
    expect(derived.documentPath('$/query/filters/0')).toBe('widgets.w.query.filters[0]')
  })
})

describe('the rules the goldens cannot say alone', () => {
  const metric = (encodings: Record<string, unknown>, query: Record<string, unknown> = {}) => ({ id: 'w', raw: { type: 'metric', encodings, query: { model: 'sales_catalog', ...query } } })
  it('takes at most 10,000 rows whatever the query says', () => {
    expect((deriveRequest(metric({ value: 'orders.net_revenue' }, { limit: 50000 }), roles).request as any).query.limit).toBe(10_000)
  })
  it('a field of no role goes to the attributes of a chart and is refused as unknown', () => {
    const d = deriveRequest({ id: 'w', raw: { type: 'chart', mark: 'bar', encodings: { x: 'nope.x', y: 'orders.net_revenue' }, query: { model: 'sales_catalog' } } }, roles)
    expect(d.issues.map((i) => i.code)).toEqual(['unknown_member'])
  })
})

describe('the request for one expanded row (DBR55)', () => {
  const file = read('expansions.json') as unknown as { groups: Record<string, unknown>; widgets: Record<string, any>; cases: { name: string; key: unknown[]; request: unknown }[] }
  for (const c of file.cases)
    it(`${c.name} gives exactly the golden request`, () => {
      const issues = new Issues()
      const groups = checkGroups(file.groups, issues)
      expect(issues.list).toEqual([])
      const derived = deriveExpansion({ id: 'w', raw: file.widgets.w, groups }, roles, c.key)
      expect(derived.issues).toEqual([])
      expect(JSON.stringify(derived.request)).toBe(JSON.stringify(c.request))
    })
  it('refuses a key that does not name a value for each of the rows\' fields', () => {
    const groups = checkGroups(file.groups, new Issues())
    const derived = deriveExpansion({ id: 'w', raw: file.widgets.w, groups }, roles, ['West', 'extra'])
    expect(derived.request).toBeUndefined()
    expect(derived.issues[0]!.message).toContain('names a value for each of the rows')
  })
})
