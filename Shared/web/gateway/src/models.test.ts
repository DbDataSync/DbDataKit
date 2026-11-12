import { describe, expect, it } from 'vitest'
import { pruneCandidates, ownerLabel, removeWarning, serveBlock, servedLabel, sortModels } from './models'

describe('where a model stands on this instance', () => {
  it('is serving, catching up, not yet served, or could not load', () => {
    expect(servedLabel({ current_version: 2, served_version: 2, failed_version: null, failure: null })).toEqual({ text: 'serving version 2', tone: 'ok' })
    const lag = servedLabel({ current_version: 3, served_version: 2, failed_version: null, failure: null })
    expect(lag.tone).toBe('warn')
    expect(lag.text).toContain('the group is on 3')
    expect(servedLabel({ current_version: 1, served_version: null, failed_version: null, failure: null }).text).toContain('not served')
    const bad = servedLabel({ current_version: 3, served_version: 2, failed_version: 3, failure: 'the catalog does not load' })
    expect(bad.tone).toBe('bad')
    expect(bad.text).toBe('version 3 did not load; still serving 2: the catalog does not load')
    expect(servedLabel({ current_version: 1, served_version: null, failed_version: 1, failure: null }).text).toContain('nothing is served')
  })
})

describe('serving an earlier version', () => {
  it('is off, with a reason, with one version or a change under way', () => {
    const row = { name: 'sales', current_version: 1, versions: 1, managed_by: null, served: true, served_version: 1, failed_version: null, failure: null }
    expect(serveBlock(row, false)).toContain('no earlier version')
    expect(serveBlock({ ...row, versions: 2 }, true)).toContain('under way')
    expect(serveBlock({ ...row, versions: 2 }, false)).toBeNull()
  })
})

describe('words', () => {
  it('sorts by name, warns about removing and says who owns a model', () => {
    expect(sortModels([{ name: 'b' }, { name: 'a' }]).map((r) => r.name)).toEqual(['a', 'b'])
    expect(removeWarning('semantic', 'sales')).toContain('every instance of the group')
    expect(removeWarning('entity', 'customers')).toContain('The Store still holds')
    expect(ownerLabel(null)).toBe('made by an administrator')
    expect(ownerLabel('build-1')).toContain('build-1')
  })
})

describe('pruneCandidates', () => {
  it('preselects all but the served version, the one before it and the three newest', () => {
    expect(pruneCandidates([1, 2, 3, 4, 5, 6, 7, 8], 5)).toEqual([1, 2, 3])
    expect(pruneCandidates([1, 2, 3], 3)).toEqual([])
    expect(pruneCandidates([1, 2, 3, 4, 5, 6], null)).toEqual([1, 2, 3])
  })
})
