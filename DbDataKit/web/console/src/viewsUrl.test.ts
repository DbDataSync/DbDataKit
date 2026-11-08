import { describe, expect, it } from 'vitest'
import type { ParameterDefinition } from '@dbdatakit/visuals'
import { crumbAddress, describeValue, drillAddress, readKiosk, readLayout, readParameters, readSource, readTrail, viewPath, withSource, withoutTrail, writeKiosk, writeLayout, writeParameters, writeSource, writeTrail } from './viewsUrl'

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

describe('kiosk mode in the address', () => {
  it('is `?mode=kiosk`, set and cleared without touching the rest', () => {
    expect(readKiosk(q(''))).toBe(false)
    expect(readKiosk(q('mode=wall'))).toBe(false)
    expect(readKiosk(q('mode=kiosk&layout=wall'))).toBe(true)
    expect(writeKiosk(q('layout=wall&p.top=5'), true).toString()).toBe('layout=wall&p.top=5&mode=kiosk')
    expect(writeKiosk(q('layout=wall&mode=kiosk'), false).toString()).toBe('layout=wall')
  })
})

describe('drill address and the way back', () => {
  const target: ParameterDefinition[] = [{ name: 'region', type: 'string', required: true }, { name: 'top', type: 'integer', default: 10 }, { name: 'many', type: 'string', many: true, default: [] }]
  const here = { kind: 'dashboard' as const, name: 'retail sales', search: q('layout=desk&p.region=West&mode=kiosk') }

  it('writes the parameters the drill gives as the target\'s own, each as the target\'s type, and leaves the defaults out', () => {
    const address = drillAddress({ drill: { kind: 'dashboard', name: 'detail', parameters: { region: 'East', top: 10 } }, targetParameters: target, here })
    const [path, search] = address.split('?') as [string, string]
    expect(path).toBe('/views/dashboards/detail')
    const params = new URLSearchParams(search)
    expect(params.get('p.region')).toBe('East')
    expect(params.has('p.top')).toBe(false)
    expect(params.get('mode')).toBe('kiosk')
    expect(readParameters(params, target)).toEqual({ region: 'East', top: 10, many: [] })
  })
  it('an integer from the row is an integer, a list is a repeated key, a value of the wrong type is the default', () => {
    const a = new URLSearchParams(drillAddress({ drill: { kind: 'report', name: 'monthly', parameters: { region: 'x', top: 25, many: ['a', 'b'] } }, targetParameters: target, here }).split('?')[1])
    expect(a.getAll('p.many')).toEqual(['a', 'b'])
    expect(a.get('p.top')).toBe('25')
    expect(drillAddress({ drill: { kind: 'report', name: 'monthly', parameters: { region: 'x', top: 'many' } }, targetParameters: target, here })).toContain('/views/reports/monthly?')
    expect(new URLSearchParams(drillAddress({ drill: { kind: 'dashboard', name: 'd', parameters: { region: 'x', top: 'many' } }, targetParameters: target, here }).split('?')[1]).has('p.top')).toBe(false)
  })
  it('records the board it leaves, in the state it leaves it, and going back is an address that restores it', () => {
    const address = drillAddress({ drill: { kind: 'dashboard', name: 'detail', parameters: { region: 'East' } }, targetParameters: target, here })
    const search = new URLSearchParams(address.split('?')[1])
    const trail = readTrail(search)
    expect(trail).toEqual([{ kind: 'dashboard', name: 'retail sales', search: 'layout=desk&p.region=West&mode=kiosk' }])
    expect(crumbAddress(trail, 0)).toBe('/views/dashboards/retail%20sales?layout=desk&p.region=West&mode=kiosk')
  })
  it('a second drill grows the trail, and a crumb\'s address keeps the trail before it', () => {
    const first = drillAddress({ drill: { kind: 'dashboard', name: 'detail', parameters: { region: 'East' } }, targetParameters: target, here })
    const second = drillAddress({ drill: { kind: 'dashboard', name: 'deeper', parameters: { region: 'East' } }, targetParameters: target, here: { kind: 'dashboard', name: 'detail', search: new URLSearchParams(first.split('?')[1]) } })
    const trail = readTrail(new URLSearchParams(second.split('?')[1]))
    expect(trail.map((c) => c.name)).toEqual(['retail sales', 'detail'])
    // the board the first drill left has no trail; the second has the first
    expect(crumbAddress(trail, 0)).not.toContain('trail=')
    expect(readTrail(new URLSearchParams(crumbAddress(trail, 1).split('?')[1])).map((c) => c.name)).toEqual(['retail sales'])
  })
  it('the trail round-trips, leaves out an entry that is not one, and `withoutTrail` drops it', () => {
    const search = writeTrail(q('layout=wall'), [{ kind: 'report', name: 'a&b', search: 'x=1&y=2' }])
    expect(readTrail(search)).toEqual([{ kind: 'report', name: 'a&b', search: 'x=1&y=2' }])
    expect(readTrail(q('trail=nonsense&trail=dashboard%2Fa%3F'))).toEqual([{ kind: 'dashboard', name: 'a', search: '' }])
    expect(withoutTrail(search).toString()).toBe('layout=wall')
    expect(viewPath('dashboard', 'a b')).toBe('/views/dashboards/a%20b')
  })
})

describe('the source in the address', () => {
  it('is read, written and kept beside the layout and the parameters', () => {
    expect(readSource(new URLSearchParams(''))).toBeNull()
    expect(readSource(new URLSearchParams('source=gateway-1&layout=wall'))).toBe('gateway-1')
    expect(writeSource(new URLSearchParams('layout=wall&p.region=West'), 'fixtures').toString()).toBe('layout=wall&p.region=West&source=fixtures')
    expect(writeSource(new URLSearchParams('source=a&layout=wall'), null).toString()).toBe('layout=wall')
    expect(writeLayout(new URLSearchParams('source=a'), 'wall').toString()).toBe('source=a&layout=wall')
    expect(writeParameters(new URLSearchParams('source=a'), defs, { region: ['West'] }).toString()).toContain('source=a')
    // a drill stays in the source it was made in
    expect(new URLSearchParams(drillAddress({ drill: { kind: 'dashboard', name: 'detail', parameters: {} }, targetParameters: undefined, here: { kind: 'dashboard', name: 'sales', search: new URLSearchParams('source=a&mode=kiosk') } }).split('?')[1]).get('source')).toBe('a')
  })

  it('is added to a path only when one was chosen', () => {
    expect(withSource('/views/dashboards', null)).toBe('/views/dashboards')
    expect(withSource('/views/dashboards/sales', 'gateway 1')).toBe('/views/dashboards/sales?source=gateway%201')
    expect(withSource('/views/dashboards?new=1', 'a')).toBe('/views/dashboards?new=1&source=a')
    expect(withSource('?edit=1', 'a')).toBe('?edit=1&source=a')
  })
})
