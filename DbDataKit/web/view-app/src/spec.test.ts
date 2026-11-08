import { describe, expect, it } from 'vitest'
import { linkRoute, parseRoute, resolveParams, routeOf } from './spec'

describe('routes', () => {
  it('the home view is the root, and a view carries its parameters after a question mark', () => {
    expect(parseRoute('/')).toEqual({ file: null, query: {} })
    expect(parseRoute('/views/detail.yaml?id=NA&x=a%20b')).toEqual({ file: 'views/detail.yaml', query: { id: 'NA', x: 'a b' } })
    expect(routeOf('views/detail.yaml', { id: 'N A', n: 3 })).toBe('/views/detail.yaml?id=N+A&n=3')
    expect(parseRoute(routeOf('views/d.yaml', { id: 'a&b=c' })).query).toEqual({ id: 'a&b=c' })
  })
})

describe('parameters', () => {
  const params = [
    { name: 'id', type: 'string' as const, required: true, default: null },
    { name: 'n', type: 'int' as const, required: false, default: '5' },
    { name: 'on', type: 'bool' as const, required: false, default: null },
    { name: 'day', type: 'date' as const, required: false, default: null },
  ]

  it('applies defaults, drops what was not declared, and keeps typed values as text', () => {
    expect(resolveParams(params, { id: 'NA', other: 'x' })).toEqual({ values: { id: 'NA', n: '5' }, errors: [] })
  })

  it('names a missing required parameter and a value of the wrong type', () => {
    expect(resolveParams(params, {}).errors).toEqual(["This view needs the parameter 'id'."])
    const bad = resolveParams(params, { id: 'a', n: '1.5', on: 'yes', day: '2026-13' })
    expect(bad.errors).toHaveLength(3)
    expect(bad.errors[0]).toContain("'n'")
    expect(bad.values).toEqual({ id: 'a' })
  })
})

describe('links', () => {
  const link = { label: 'Open', to: 'views/detail.yaml', row: true, with: { id: { kind: 'attribute' as const, text: 'code' }, region: { kind: 'param' as const, text: 'region' }, mode: { kind: 'literal' as const, text: 'view' } } }

  it('takes a value from the row, from a parameter of the view, or as written', () => {
    expect(linkRoute(link, { code: 'NA' }, { region: 'amer' })).toBe('/views/detail.yaml?id=NA&region=amer&mode=view')
  })

  it('is not drawn when a value is missing, rather than leading to a view that refuses it', () => {
    expect(linkRoute(link, { code: null }, { region: 'amer' })).toBeNull()
    expect(linkRoute(link, { code: 'NA' }, {})).toBeNull()
  })
})
