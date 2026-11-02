import { describe, expect, it } from 'vitest'
import { draftChange, editable, inputKind, keyOf, parseInput, titleOf, toInput } from './fields'
import { attribute, territory } from './testing'

const quota = territory.attributes.find((a) => a.name === 'quota')!

describe('the control an attribute gets', () => {
  it.each([
    [attribute({ name: 'a', allowed: ['x'] }), 'select'],
    [attribute({ name: 'a', kind: 'boolean' }), 'checkbox'],
    [attribute({ name: 'a', kind: 'int32' }), 'integer'],
    [attribute({ name: 'a', kind: 'decimal' }), 'number'],
    [attribute({ name: 'a', kind: 'date' }), 'date'],
    [attribute({ name: 'a', kind: 'timestamp' }), 'datetime'],
    [attribute({ name: 'a', kind: 'json' }), 'json'],
    [attribute({ name: 'a', length: null }), 'textarea'],
    [attribute({ name: 'a', length: 40 }), 'text'],
  ])('%j is a %s', (a, expected) => expect(inputKind(a)).toBe(expected))
})

describe('what typed text means', () => {
  it('empty is no value, numbers are numbers, and nonsense says what it should be', () => {
    expect(parseInput(quota, '')).toEqual({ ok: true, value: null })
    expect(parseInput(quota, ' 12.5 ')).toEqual({ ok: true, value: 12.5 })
    expect(parseInput(quota, 'abc')).toEqual({ ok: false, message: 'A number.' })
    expect(parseInput(attribute({ name: 'n', kind: 'int32' }), '1.5')).toEqual({ ok: false, message: 'A whole number.' })
    expect(parseInput(attribute({ name: 'j', kind: 'json' }), '{"a":1}')).toEqual({ ok: true, value: { a: 1 } })
    expect(parseInput(attribute({ name: 'j', kind: 'json' }), '{')).toEqual({ ok: false, message: 'Not valid JSON.' })
    expect(parseInput(attribute({ name: 'b', kind: 'boolean' }), 'true')).toEqual({ ok: true, value: true })
  })

  it('a stored value round trips through its input text', () => {
    expect(toInput(quota, 12.5)).toBe('12.5')
    expect(toInput(quota, null)).toBe('')
    expect(toInput(attribute({ name: 'j', kind: 'json' }), { a: 1 })).toContain('"a": 1')
  })
})

describe('the change a form makes', () => {
  const row = { values: { code: 'NA', name: 'North', tier: 'gold', active: true, quota: 10 }, version: 3 }

  it('an insert carries what was filled in and leaves the rest to the model\'s defaults', () => {
    const draft = draftChange(territory, null, { code: 'EU', name: 'Europe', tier: '', active: '', quota: '5' })
    expect(draft.change).toEqual({ action: 'insert', entity: 'territory', values: { code: 'EU', name: 'Europe', quota: 5 } })
  })

  it('an update names the key and the version it read and carries only what differs', () => {
    const draft = draftChange(territory, row, { code: 'NA', name: 'North America', tier: 'gold', active: 'true', quota: '10' })
    expect(draft.change).toEqual({ action: 'update', entity: 'territory', key: { code: 'NA' }, expected_version: 3, values: { name: 'North America' } })
  })

  it('an update that changes nothing is no change, and clearing a value sends null', () => {
    expect(draftChange(territory, row, { name: 'North', tier: 'gold', active: 'true', quota: '10' }).change).toBeNull()
    expect(draftChange(territory, row, { name: 'North', tier: '', active: 'true', quota: '10' }).change).toMatchObject({ values: { tier: null } })
  })

  it('text that does not parse is a problem against its attribute and sends nothing', () => {
    const draft = draftChange(territory, null, { code: 'EU', name: 'Europe', quota: 'lots' })
    expect(draft.change).toBeNull()
    expect(draft.problems).toEqual({ quota: 'A number.' })
  })

  it('a key is edited only on a new row', () => {
    expect(editable(territory, true).map((a) => a.name)).toContain('code')
    expect(editable(territory, false).map((a) => a.name)).not.toContain('code')
    expect(keyOf(territory, row.values)).toEqual({ code: 'NA' })
  })
})

it('a title is the name made readable when the model gives none', () => {
  expect(titleOf({ name: 'sales_territory', title: null })).toBe('Sales territory')
  expect(titleOf({ name: 'x', title: 'Region' })).toBe('Region')
})
