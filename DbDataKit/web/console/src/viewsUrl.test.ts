import { describe, expect, it } from 'vitest'
import type { ParameterDefinition } from '@dbdatakit/visuals'
import { describeValue, readLayout, readParameters, writeLayout, writeParameters } from './viewsUrl'

const defs: ParameterDefinition[] = [
  { name: 'region', type: 'string', many: true, default: [] },
  { name: 'top', type: 'integer', default: 10 },
  { name: 'rate', type: 'number' },
  { name: 'closed', type: 'boolean', default: false },
  { name: 'q', type: 'string' },
]
const q = (s: string) => new URLSearchParams(s)

describe('readParameters', () => {
  it('is the defaults when the address holds none: a list for many, null otherwise', () => {
    expect(readParameters(q(''), defs)).toEqual({ region: [], top: 10, rate: null, closed: false, q: null })
    expect(readParameters(q(''), undefined)).toEqual({})
  })
  it('reads each type, and a repeated key as a list', () => {
    expect(readParameters(q('p.region=West&p.region=East&p.top=5&p.rate=0.25&p.closed=true&p.q=a%20b'), defs))
      .toEqual({ region: ['West', 'East'], top: 5, rate: 0.25, closed: true, q: 'a b' })
  })
  it('ignores a value of the wrong type, and a name the document does not declare', () => {
    expect(readParameters(q('p.top=ten&p.rate=x&p.closed=maybe&p.other=1&top=3'), defs)).toEqual({ region: [], top: 10, rate: null, closed: false, q: null })
  })
  it('reads an empty value as nothing chosen', () => {
    expect(readParameters(q('p.top=&p.region='), defs)).toMatchObject({ top: null, region: [] })
  })
})

describe('writeParameters', () => {
  it('writes only what differs from the default, keeps other keys, and drops what went back to the default', () => {
    const values = readParameters(q('p.region=West&p.top=5'), defs)
    expect(writeParameters(q('layout=wall&p.region=West&p.top=5'), defs, { ...values, top: 10 }).toString()).toBe('layout=wall&p.region=West')
    expect(writeParameters(q(''), defs, { ...values, region: ['A', 'B'] }).toString()).toBe('p.region=A&p.region=B&p.top=5')
  })
  it('round-trips every value, including an emptied choice over a non-empty default', () => {
    const withDefault: ParameterDefinition[] = [{ name: 'region', type: 'string', many: true, default: ['West'] }, { name: 'q', type: 'string', default: 'x' }]
    for (const values of [{ region: [], q: null }, { region: ['A', 'B'], q: 'y' }, { region: ['West'], q: 'x' }]) {
      const written = writeParameters(q(''), withDefault, values)
      expect(readParameters(written, withDefault)).toEqual(values)
    }
    expect(writeParameters(q(''), withDefault, { region: [], q: 'x' }).toString()).toBe('p.region=')
  })
  it('writes numbers and booleans as text that reads back', () => {
    const values = { region: [], top: 3, rate: 1.5, closed: true, q: null }
    expect(readParameters(writeParameters(q(''), defs, values), defs)).toEqual(values)
  })
})

describe('layout in the address', () => {
  const layouts = [{ name: 'desk' }, { name: 'wall' }]
  it('is named, absent, or unknown', () => {
    expect(readLayout(q('layout=wall'), layouts)).toEqual({ layout: 'wall', unknown: null })
    expect(readLayout(q(''), layouts)).toEqual({ layout: null, unknown: null })
    expect(readLayout(q('layout=tv'), layouts)).toEqual({ layout: null, unknown: 'tv' })
  })
  it('is written without touching the parameters', () => {
    expect(writeLayout(q('p.region=West'), 'phone').toString()).toBe('p.region=West&layout=phone')
    expect(writeLayout(q('layout=phone&p.region=West'), null).toString()).toBe('p.region=West')
  })
})

describe('describeValue', () => {
  it('says what is chosen', () => {
    expect(describeValue([])).toBe('none chosen')
    expect(describeValue(null)).toBe('none chosen')
    expect(describeValue(['West', 'East'])).toBe('West, East')
    expect(describeValue(5)).toBe('5')
  })
})
