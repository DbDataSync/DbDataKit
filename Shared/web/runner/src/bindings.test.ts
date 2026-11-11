import { describe, expect, it } from 'vitest'
import type { DefinitionDetail } from './api'
import { bindingBlock, bindingProblem, bindingsRequest, changedSlots, connectionNames, signaturesText, slotRows, unboundSlots } from './bindings'

const detail = (over: Partial<DefinitionDetail> = {}): Pick<DefinitionDetail, 'slots' | 'bindings'> => ({
  slots: [
    { channel: 'prod', slot: 'source', kind: 'sqlserver', tasks: ['load', 'check'], versions: [1, 2] },
    { channel: 'prod', slot: 'target', kind: 'postgres', tasks: ['load'], versions: [2] },
    { channel: 'test', slot: 'source', kind: 'sqlserver', tasks: ['load'], versions: [2] },
  ],
  bindings: [
    { channel: 'prod', slot: 'source', bound_to: 'erp', revision: 3 },
    { channel: 'prod', slot: 'old_slot', bound_to: 'legacy', revision: 1 },
    { channel: 'test', slot: 'source', bound_to: 'erp-test', revision: 1 },
  ],
  ...over,
})

describe('the slots of a channel', () => {
  it('lists the declared slots of that channel with what each is bound to, then the bindings no version declares', () => {
    const rows = slotRows(detail(), 'prod')
    expect(rows.map((r) => [r.slot, r.boundTo, r.declared])).toEqual([['source', 'erp', true], ['target', null, true], ['old_slot', 'legacy', false]])
    expect(rows[0]).toMatchObject({ kind: 'sqlserver', tasks: ['load', 'check'], versions: [1, 2], revision: 3 })
    expect(rows[2].kind).toBeNull()
  })
  it('is empty for a channel nothing declares a slot in and survives a runner that sends no slots', () => {
    expect(slotRows(detail(), 'dev')).toEqual([])
    expect(slotRows({ bindings: detail().bindings }, 'test').map((r) => r.slot)).toEqual(['source'])
  })
  it('names the declared slots that are not bound', () => {
    expect(unboundSlots(slotRows(detail(), 'prod'))).toEqual(['target'])
    expect(unboundSlots(slotRows(detail(), 'test'))).toEqual([])
  })
})

describe('what was changed', () => {
  const rows = slotRows(detail(), 'prod')
  it('counts a slot only when the trimmed text differs from what is bound', () => {
    expect(changedSlots(rows, { source: ' erp ', target: ' warehouse ' })).toEqual([{ slot: 'target', value: 'warehouse', revision: null }])
    expect(changedSlots(rows, {})).toEqual([])
  })
  it('explains a value that cannot be sent', () => {
    expect(bindingProblem('target', '  ')).toContain('Name the connection for target')
    expect(bindingProblem('target', 'a b')).toContain('without spaces')
    expect(bindingProblem('target', 'x'.repeat(201))).toContain('200')
    expect(bindingProblem('target', 'warehouse')).toBeNull()
    expect(bindingProblem('target', 'secret:warehouse-login')).toBeNull()
  })
  it('says why Save is off: the role first, then nothing changed, then the first bad value', () => {
    expect(bindingBlock(false, rows, { target: 'w' })).toContain('admin role')
    expect(bindingBlock(true, rows, {})).toBe('Change a connection first.')
    expect(bindingBlock(true, rows, { source: '' })).toContain('Name the connection for source')
    expect(bindingBlock(true, rows, { target: 'w' })).toBeNull()
  })
  it('sends the changed slots, with the revision only when exactly one bound slot changed', () => {
    expect(bindingsRequest(rows, { source: 'erp2' })).toEqual({ slots: { source: 'erp2' }, expected_revision: 3 })
    expect(bindingsRequest(rows, { target: 'w' })).toEqual({ slots: { target: 'w' } })
    expect(bindingsRequest(rows, { source: 'erp2', target: 'w' })).toEqual({ slots: { source: 'erp2', target: 'w' } })
    expect(bindingsRequest(rows, {})).toBeNull()
    expect(bindingsRequest(rows, { source: 'a b' })).toBeNull()
  })
})

describe('words and names', () => {
  it('offers each known connection once, sorted', () => {
    expect(connectionNames([{ name: 'b' }, { name: 'a' }, { name: 'b' }])).toEqual(['a', 'b'])
    expect(connectionNames(undefined)).toEqual([])
  })
  it('warns that turning require signatures on refuses every publish while checking is not built', () => {
    expect(signaturesText(true)).toContain('every new publish is refused')
    expect(signaturesText(false)).toContain('recorded as unsigned')
  })
})
