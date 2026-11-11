import { readFileSync } from 'node:fs'
import { join } from 'node:path'
import { describe, expect, it } from 'vitest'
import { validateViewsDocument } from './document'
import { describeLink, linkQuery, linkTypeFits, linkValues, printTreatment } from './links'
import type { LinkDoc, LinkTargetParam } from './links'

const root = join(import.meta.dirname, '../../../../conformance/views')
const read = (relative: string) => JSON.parse(readFileSync(join(root, relative), 'utf8')) as Record<string, any>
const shared = read('links.json') as { targets: Record<string, LinkTargetParam[]>; fits: { given: string; many: boolean; wanted: string; wantedMany: boolean; fits: boolean }[] }
const good = read('good/dashboard-links.json')

describe('the pure rules shared with .NET', () => {
  for (const c of shared.fits)
    it(`${c.many ? 'a list of ' : ''}${c.given} ${c.fits ? 'fits' : 'does not fit'} ${c.wantedMany ? 'a list of ' : ''}${c.wanted}`, () => {
      expect(linkTypeFits(c.given, c.many, c.wanted, c.wantedMany)).toBe(c.fits)
    })
  it('the good document is accepted by every target it names', () => {
    expect(validateViewsDocument(good, { kind: 'dashboard', targets: shared.targets })).toEqual([])
  })
})

describe('reading a link', () => {
  const links = good.links as Record<string, LinkDoc>
  it('applies the defaults: navigate, none, and refresh for a form', () => {
    const page = describeLink('region-page', links['region-page']!)
    expect([page.targetKind, page.target, page.as, page.thenKind, page.hasAddress, page.targetKey]).toEqual(['page', 'regional-detail', 'navigate', 'none', true, 'page:regional-detail'])
    const form = describeLink('order-form', links['order-form']!)
    expect([form.valuesKey, form.as, form.thenKind, form.hasAddress, form.lock, form.print]).toEqual(['new', 'dialog', 'refresh', false, ['region'], 'hidden'])
    const edit = describeLink('order', links.order!)
    expect([edit.valuesKey, edit.thenKind, edit.thenWidgets, edit.label]).toEqual(['edit', 'widgets', ['orders_table'], 'Open the order'])
    expect(describeLink('x', { form: 'orders', edit: { id: 1 } }).thenKind).toBe('refresh')
  })
  it('tells a literal from a field of the row and from a parameter, and types a literal', () => {
    const v = describeLink('year-report', links['year-report']!).values
    expect(v.region).toMatchObject({ kind: 'param', text: 'region' })
    expect(v.year).toMatchObject({ kind: 'literal', literalType: 'integer', value: 2026 })
    expect(describeLink('f', { form: 'orders', new: { a: '$orders.id', b: 'open', c: true, d: 1.5 } }).values).toMatchObject({
      a: { kind: 'field', text: 'orders.id' }, b: { kind: 'literal', literalType: 'string' }, c: { literalType: 'boolean' }, d: { literalType: 'number' },
    })
  })
})

describe('what a click carries', () => {
  const links = good.links as Record<string, LinkDoc>
  it('reads a field of the row by its full name or by its alias, a parameter, a literal', () => {
    const link = describeLink('order-form', links['order-form']!)
    expect(linkValues(link, { 'customers.id': 7, region: 'EMEA' }, {})).toEqual({ customer_id: 7, region: 'EMEA', status: 'open' })
    expect(linkValues(link, { id: 9, region: 'APAC' }, {})).toEqual({ customer_id: 9, region: 'APAC', status: 'open' })
    expect(linkValues(describeLink('p', links['region-page']!), { 'customers.region': 'EMEA' }, { year: 2026 })).toEqual({ region: 'EMEA', year: 2026 })
  })
  it('leaves out what has no value, never the text undefined', () => {
    const link = describeLink('region-page', links['region-page']!)
    expect(linkValues(link, { 'customers.region': null }, { year: undefined })).toEqual({})
    expect(linkValues(link, {}, {})).toEqual({})
  })
  it('a list parameter is a list, a date is its day', () => {
    const link = describeLink('x', { page: 'p', with: { regions: '$params.regions', day: '$d' } })
    expect(linkValues(link, { d: new Date('2026-03-04T10:00:00Z') }, { regions: ['EMEA', 'APAC'] })).toEqual({ regions: ['EMEA', 'APAC'], day: '2026-03-04' })
  })
  it('writes the values as a sorted, encoded query string; a list repeats its name', () => {
    expect(linkQuery({ year: 2026, region: 'North & South', tags: ['a', 'b c'] })).toBe('region=North%20%26%20South&tags=a&tags=b%20c&year=2026')
    expect(linkQuery({})).toBe('')
  })
})

describe('what an export draws', () => {
  const navigate = { hasAddress: true }
  it('the developer decides; the default follows the meaning, never the look', () => {
    expect(printTreatment({ ...navigate }, 'value')).toBe('normal')
    expect(printTreatment({ ...navigate }, 'affordance')).toBe('hidden')
    expect(printTreatment({ ...navigate, print: 'link' }, 'affordance')).toBe('link')
    expect(printTreatment({ ...navigate, print: 'hidden' }, 'value')).toBe('hidden')
  })
  it('a PNG has no annotation: link is drawn as normal, and image defaults to print', () => {
    expect(printTreatment({ ...navigate, print: 'link' }, 'value', 'image')).toBe('normal')
    expect(printTreatment({ ...navigate, print: 'link', image: 'hidden' }, 'value', 'image')).toBe('hidden')
    expect(printTreatment({ ...navigate, print: 'hidden', image: 'normal' }, 'affordance', 'image')).toBe('normal')
  })
  it('a target with no address is never a link, even unchecked', () => {
    expect(printTreatment({ hasAddress: false, print: 'link' }, 'value')).toBe('normal')
    expect(printTreatment({ hasAddress: false, print: 'link' }, 'affordance')).toBe('hidden')
  })
})
