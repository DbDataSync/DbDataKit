import { describe, expect, it } from 'vitest'
import type { CheckpointChange } from './api'
import { describeChange, keyLabel, partsLine, rewindOptions } from './checkpoints'

const change = (over: Partial<CheckpointChange>): CheckpointChange => ({ id: 1, part: 'at', type: 'int', old: null, new: '10', version: 1, run_id: null, actor: 'run', at: '2026-11-11T10:00:00Z', ...over })

describe('checkpoint helpers', () => {
  it('writes the parts of an item on one line, in name order', () => {
    expect(partsLine({ key: '', version: 2, updated: '', updated_by: null, parts: { z: { type: 'int', value: '1' }, a: { type: 'text', value: 'x' } } })).toBe('a=x, z=1')
    expect(partsLine({ key: '', version: 2, updated: '', updated_by: null, parts: {} })).toBe('(no value)')
  })
  it('names the empty key as the whole task', () => {
    expect(keyLabel('')).toBe('the whole task')
    expect(keyLabel('customer-7')).toBe('customer-7')
  })
  it('says a change with who made it', () => {
    expect(describeChange(change({}))).toBe('at set to 10 by run')
    expect(describeChange(change({ old: '10', new: '20', run_id: '3f2a9c1d-0000-0000-0000-000000000000' }))).toBe('at 10 -> 20 by run 3f2a9c1d')
    expect(describeChange(change({ old: '20', new: null, actor: 'Ada' }))).toBe('at (was 20) removed by Ada')
  })
  it('offers one rewind per version, going back before its oldest change, newest first', () => {
    const changes = [change({ id: 9, version: 3, part: 'b', old: '2', new: '3' }), change({ id: 8, version: 3, part: 'a', old: '1', new: '2' }), change({ id: 5, version: 2, part: 'a', old: '0', new: '1' })]
    const options = rewindOptions(changes)
    expect(options.map((o) => [o.id, o.version])).toEqual([[8, 3], [5, 2]])
    expect(options[0].label).toBe('Go back before version 3: b=2, a=1')
  })
})
