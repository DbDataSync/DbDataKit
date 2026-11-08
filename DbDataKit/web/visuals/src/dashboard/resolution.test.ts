import { describe, expect, it } from 'vitest'
import { fixtureClient, placedWidgets } from '../client/fixtureClient'
import { validateText } from '../client/text'
import { sampleBundle } from '../testing/sampleBundle'
import type { ViewsDoc } from '../views/model'
import { defaultParameters, parametersKey, resolveLayout } from './resolution'

const bundle = sampleBundle()
const document = validateText(bundle.documents['dashboard/retail-sales-today']!).document as ViewsDoc
const ref = { kind: 'dashboard', name: 'retail-sales-today' } as const

describe('parameters', () => {
  it('start from the document\'s defaults: a list for many, nothing otherwise', () => {
    expect(defaultParameters(document)).toEqual({ region: [] })
    expect(defaultParameters({ parameters: [{ name: 'q', type: 'string' }, { name: 'n', type: 'integer', default: 5 }, { name: 'm', type: 'string', many: true }] })).toEqual({ q: null, n: 5, m: [] })
    expect(defaultParameters({})).toEqual({})
  })

  it('are one key whatever the order they were set in', () => {
    expect(parametersKey({ a: 1, b: ['x'] })).toBe(parametersKey({ b: ['x'], a: 1 }))
    expect(parametersKey({ a: 1 })).not.toBe(parametersKey({ a: 2 }))
  })
})

describe('resolveLayout', () => {
  const calls: string[][] = []
  const client = () => { calls.length = 0; return fixtureClient(bundle, { onResolve: (c) => calls.push(c.widgets) }) }
  const placed = (layout: string) => placedWidgets(document, layout)

  it('asks for what is missing, keeps the rest, and does not ask at all when nothing is missing', async () => {
    const c = client()
    const parameters = { region: [] }
    const first = await resolveLayout({ client: c, ref, layout: 'phone', placed: placed('phone'), parameters, previous: null })
    expect(Object.keys(first.results)).toEqual(['regions', 'net', 'margin', 'by_region', 'top_products'])
    const second = await resolveLayout({ client: c, ref, layout: 'desk', placed: placed('desk'), parameters, previous: first })
    expect(calls).toEqual([placed('phone'), ['note']])
    expect(Object.keys(second.results)).toEqual(['regions', 'net', 'margin', 'by_region', 'top_products', 'note'])
    const third = await resolveLayout({ client: c, ref, layout: 'phone', placed: placed('phone'), parameters, previous: second })
    expect(third).toBe(second)
    expect(calls.length).toBe(2)
    expect(Object.keys(third.results)).toContain('note')
  })

  it('asks for every placed widget again when the parameters changed, and forgets the results of the others', async () => {
    const c = client()
    const first = await resolveLayout({ client: c, ref, layout: 'desk', placed: placed('desk'), parameters: { region: [] }, previous: null })
    const next = await resolveLayout({ client: c, ref, layout: 'phone', placed: placed('phone'), parameters: { region: ['West'] }, previous: first })
    expect(calls).toEqual([placed('desk'), placed('phone')])
    expect(Object.keys(next.results)).toEqual(placed('phone'))
    expect(next.key).toBe(parametersKey({ region: ['West'] }))
    expect(next.asOf).toBe('2026-11-08T09:00:00Z')
  })
})
