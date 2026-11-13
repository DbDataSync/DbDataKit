import type { ParameterDefinition } from '@dbdatakit/visuals'
import { describe, expect, it } from 'vitest'
import { LAYOUT_PARAM, parametersFromQuery, queryFromParameters, stemOf, targetFile } from './boards'
import type { DocumentSpec, ViewSpec } from './spec'

const defs: ParameterDefinition[] = [
  { name: 'region', type: 'string', default: 'all' },
  { name: 'year', type: 'integer' },
  { name: 'big', type: 'boolean', default: false },
  { name: 'tags', type: 'string', many: true },
]

describe('the route is the document\'s parameters', () => {
  it('reads each declared name over its default, of its type, and ignores the rest', () => {
    expect(parametersFromQuery({}, defs)).toEqual({ region: 'all', year: null, big: false, tags: [] })
    expect(parametersFromQuery({ region: 'West', year: '2026', big: 'true', tags: 'a,b', secret: 'x', _layout: 'print' }, defs)).toEqual({ region: 'West', year: 2026, big: true, tags: ['a', 'b'] })
  })

  it('keeps the default for a value of the wrong type and reads an empty value as nothing chosen', () => {
    expect(parametersFromQuery({ year: 'abc', big: 'maybe', tags: 'a,,b', region: '' }, defs)).toEqual({ region: null, year: null, big: false, tags: ['a', 'b'] })
    expect(parametersFromQuery({ year: '1.5' }, defs).year).toBeNull()
  })

  it('writes only what differs from the default, with the layout, so a route can be copied', () => {
    expect(queryFromParameters(defs, { region: 'all', year: null, big: false, tags: [] })).toEqual({})
    expect(queryFromParameters(defs, { region: 'West', year: 2026, big: true, tags: ['a', 'b'] }, 'print')).toEqual({ region: 'West', year: '2026', big: 'true', tags: 'a,b', [LAYOUT_PARAM]: 'print' })
    const round = queryFromParameters(defs, { region: 'West', year: 2026, big: false, tags: ['x'] })
    expect(parametersFromQuery(round, defs)).toEqual({ region: 'West', year: 2026, big: false, tags: ['x'] })
  })
})

describe('where a link goes', () => {
  const document = (kind: 'dashboard' | 'report'): DocumentSpec => ({ kind: 'document', document: kind, title: 't', model: '', params: [], links: [] })
  const spec = (kind: string) => ({ kind, title: 't', model: 'm', entity: 'e', params: [], links: [] }) as unknown as ViewSpec
  const doc = {
    files: ['views/orders-form.yaml', 'views/orders.yaml', 'dashboards/regional.yaml', 'dashboards/regional-report.yaml'],
    views: { 'views/orders-form.yaml': spec('form'), 'views/orders.yaml': spec('grid'), 'dashboards/regional.yaml': document('dashboard'), 'dashboards/regional-report.yaml': document('report') },
  }

  it('is a file of the app named by its stem and kind', () => {
    expect(stemOf('views/orders-form.yaml')).toBe('orders-form')
    expect(targetFile(doc, { targetKind: 'form', target: 'orders-form' })).toBe('views/orders-form.yaml')
    expect(targetFile(doc, { targetKind: 'view', target: 'orders' })).toBe('views/orders.yaml')
    expect(targetFile(doc, { targetKind: 'dashboard', target: 'regional' })).toBe('dashboards/regional.yaml')
    expect(targetFile(doc, { targetKind: 'report', target: 'regional-report' })).toBe('dashboards/regional-report.yaml')
  })

  it('is nothing for a name of another kind, a name the app lacks, and a page (the shell serves those)', () => {
    expect(targetFile(doc, { targetKind: 'dashboard', target: 'regional-report' })).toBeNull()
    expect(targetFile(doc, { targetKind: 'form', target: 'orders' })).toBeNull()
    expect(targetFile(doc, { targetKind: 'view', target: 'nowhere' })).toBeNull()
    expect(targetFile(doc, { targetKind: 'page', target: 'orders' })).toBeNull()
  })
})
