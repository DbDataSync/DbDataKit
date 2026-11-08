import { describe, expect, it } from 'vitest'
import { ApiError } from '@dbdatakit/shell'
import { CHECK_KINDS, blankDraft, currentOf, draftOf, faultsOf, kindInfo, sameChecks, saveBlockedReason, summary, wire } from './checkForms'

describe('the seven kinds', () => {
  it('are the kinds of the check model, each with a label and a hint', () => {
    expect(CHECK_KINDS.map((k) => k.kind)).toEqual(['freshness', 'not_null', 'unique', 'accepted_values', 'range', 'referential', 'row_count_change'])
    expect(CHECK_KINDS.every((k) => k.label !== '' && k.hint !== '')).toBe(true)
    expect(kindInfo('nope')).toBeUndefined()
  })
})

describe('a check as typed becomes what the API takes', () => {
  it('sends only the keys the kind has: a freshness check has an age and an optional column, nothing of a range', () => {
    const draft = { ...blankDraft('freshness', ' dbo.orders '), id: ' orders_fresh ', maxAge: '6h', severity: 'block', min: '5', values: 'x' }
    expect(wire(draft)).toEqual({ id: 'orders_fresh', kind: 'freshness', dataset: 'dbo.orders', severity: 'block', on_error: 'alert', description: null, max_age: '6h' })
  })

  it('writes one column as `column` and several as `columns`, split at commas', () => {
    expect(wire({ ...blankDraft('not_null', 'd'), id: 'a', columns: 'order_id' })).toMatchObject({ column: 'order_id' })
    const several = wire({ ...blankDraft('unique', 'd'), id: 'a', columns: 'region, order_no ,' })
    expect(several.columns).toEqual(['region', 'order_no'])
    expect(several.column).toBeUndefined()
  })

  it('splits accepted values at line ends, so a value may hold a comma', () => {
    expect(wire({ ...blankDraft('accepted_values', 'd'), id: 'a', columns: 'status', values: 'open\r\nin progress\n\na,b' }).values).toEqual(['open', 'in progress', 'a,b'])
  })

  it('takes a range\'s bounds, a referential check\'s target and a row count check\'s limits as typed, an empty one as nothing', () => {
    expect(wire({ ...blankDraft('range', 'd'), id: 'a', columns: 'amount', min: '-5', max: '' })).toMatchObject({ column: 'amount', min: '-5', max: null })
    expect(wire({ ...blankDraft('referential', 'd'), id: 'a', columns: 'customer_id', referencesDataset: 'dbo.customers', referencesColumns: 'id' }).references).toEqual({ dataset: 'dbo.customers', column: 'id' })
    expect(wire({ ...blankDraft('referential', 'd'), id: 'a', columns: 'a, b', referencesDataset: 'x', referencesColumns: 'c, d' }).references).toEqual({ dataset: 'x', columns: ['c', 'd'] })
    expect(wire({ ...blankDraft('row_count_change', 'd'), id: 'a', maxDecrease: '20' })).toMatchObject({ max_increase_percent: null, max_decrease_percent: '20' })
  })

  it('drops what belongs to another kind when the kind is changed', () => {
    const range = { ...blankDraft('range', 'd'), id: 'a', columns: 'amount', min: '1', max: '9' }
    const changed = wire({ ...range, kind: 'not_null' })
    expect(changed).not.toHaveProperty('min')
    expect(changed).not.toHaveProperty('max')
    expect(changed.column).toBe('amount')
  })
})

describe('a check as the server sent it', () => {
  it('opens in the editor and is written back unchanged', () => {
    const forms = [
      { id: 'a', kind: 'freshness', dataset: 'dbo.orders', severity: 'block', on_error: 'alert', description: null, max_age: '6h' },
      { id: 'b', kind: 'unique', dataset: 'd', severity: 'warn', on_error: 'block', description: null, columns: ['x', 'y'] },
      { id: 'c', kind: 'accepted_values', dataset: 'd', severity: 'warn', on_error: 'alert', description: 'why', column: 's', values: ['open', 'in progress'] },
      { id: 'd', kind: 'referential', dataset: 'd', severity: 'warn', on_error: 'alert', description: null, column: 'cid', references: { dataset: 'e', column: 'id' } },
    ]
    for (const f of forms) expect(wire(draftOf(f))).toMatchObject(f)
  })

  it('is told in a line', () => {
    expect(summary({ kind: 'freshness', dataset: 'dbo.orders', max_age: '10m' })).toBe('dbo.orders: no older than 10m')
    expect(summary({ kind: 'not_null', dataset: 'dbo.orders', column: 'Name' })).toBe('dbo.orders.Name has no missing value')
    expect(summary({ kind: 'range', dataset: 'd', column: 'amount', min: '0', max: '9' })).toBe('d.amount is at least 0 and at most 9')
    expect(summary({ kind: 'row_count_change', dataset: 'd', max_decrease_percent: '20' })).toBe('d: row count down at most 20%')
    expect(summary({ kind: 'referential', dataset: 'd', column: 'c', references: { dataset: 'e', column: 'id' } })).toBe('d.c exists in e')
  })
})

describe('what a refusal says', () => {
  const fault = { code: 'DBD-QLT-001', file: 'task.yaml', line: 7, column: 11, message: '`freshnes` is not a kind of check. Did you mean `freshness`?' }

  it('is the strict reader\'s diagnostics, from the body of a 400 or 422', () => {
    expect(faultsOf(new ApiError(400, 'x', { diagnostics: [fault] }))).toEqual([fault])
    expect(faultsOf(new ApiError(500, 'x', {}))).toEqual([])
    expect(faultsOf(new Error('x'))).toEqual([])
  })

  it('is the current checks, from a 409 only', () => {
    const current = { checks: [{ id: 'a' }], version: 'v2' }
    expect(currentOf(new ApiError(409, 'x', { current }))).toEqual(current)
    expect(currentOf(new ApiError(400, 'x', { current }))).toBeNull()
    expect(currentOf(null)).toBeNull()
  })
})

describe('whether the list is changed and may be saved', () => {
  it('compares the order and every key', () => {
    expect(sameChecks([{ id: 'a' }, { id: 'b' }], [{ id: 'a' }, { id: 'b' }])).toBe(true)
    expect(sameChecks([{ id: 'a' }, { id: 'b' }], [{ id: 'b' }, { id: 'a' }])).toBe(false)
    expect(sameChecks([{ id: 'a', max_age: '1h' }], [{ id: 'a', max_age: '2h' }])).toBe(false)
  })

  it('says why not: not an administrator, saving, nothing changed', () => {
    expect(saveBlockedReason(true, true, false)).toBeNull()
    expect(saveBlockedReason(false, true, false)).toContain('admin role')
    expect(saveBlockedReason(true, true, true)).toBe('Saving…')
    expect(saveBlockedReason(true, false, false)).toBe('Nothing has changed.')
  })
})
