import { describe, expect, it } from 'vitest'
import type { DocumentVersions } from '@dbdatakit/visuals'
import { lifecycleMessage, pruneCandidates, rollbackState, versionRows, whenText } from './viewsVersions'

const versions = (over: Partial<DocumentVersions> = {}): DocumentVersions => ({
  kind: 'dashboard', name: 'sales', active: 2, previous: 1,
  versions: [
    { version: 1, publishedBy: 'Bob', publishedAt: '2026-11-07T09:30:15Z', note: null, active: false, dependsOn: [] },
    { version: 2, publishedBy: 'Ada', publishedAt: '2026-11-08T10:00:00Z', note: 'second', active: true, dependsOn: ['semantic-model:retail@1'] },
  ],
  ...over,
})

describe('the versions of a published document', () => {
  it('lists the newest first with who, when and why, and marks the one that is served', () => {
    const rows = versionRows(versions(), true)
    expect(rows.map((r) => r.version)).toEqual([2, 1])
    expect(rows[0]).toMatchObject({ who: 'Ada', note: 'second', served: true, when: '2026-11-08 10:00 UTC' })
    expect(rows[1]).toMatchObject({ who: 'Bob', note: '', served: false })
  })

  it('offers to serve any version but the served one, and says why not', () => {
    const [served, earlier] = versionRows(versions(), true)
    expect(served!.switchBlocked).toContain('served now')
    expect(earlier!.switchBlocked).toBeNull()
    expect(versionRows(versions(), false)[1]!.switchBlocked).toContain('author role')
  })

  it('offers a rollback to the version before, and says why it cannot', () => {
    expect(rollbackState(versions(), true)).toEqual({ version: 1, blocked: null })
    expect(rollbackState(versions({ previous: null }), true).blocked).toContain('no earlier version')
    expect(rollbackState(versions({ active: null }), true).blocked).toContain('Nothing is served')
    expect(rollbackState(versions(), false).blocked).toContain('author role')
  })

  it('keeps a time it cannot read as it came', () => {
    expect(whenText('last Tuesday')).toBe('last Tuesday')
  })

  it('says what a change did, including the model versions that moved with the document', () => {
    expect(lifecycleMessage('dashboard', 'sales', 'switch', 1, [])).toBe('Version 1 of sales is served now.')
    expect(lifecycleMessage('dashboard', 'sales', 'rollback', 1, ['semantic-model:retail@1', 'dashboard:sales@1'])).toContain('Rolled back: version 1')
    expect(lifecycleMessage('dashboard', 'sales', 'rollback', 1, ['semantic-model:retail@1', 'dashboard:sales@1'])).toContain('semantic-model:retail@1')
    expect(lifecycleMessage('report', 'monthly', 'retire', null, [])).toContain('no longer served')
  })
})

describe('pruneCandidates', () => {
  const versions = (active: number | null, previous: number | null, n: number) => ({
    kind: 'dashboard' as const, name: 'd', active, previous,
    versions: Array.from({ length: n }, (_, i) => ({ version: i + 1, publishedBy: 'a', publishedAt: '2026-01-01T00:00:00Z', note: null, active: active === i + 1, dependsOn: [] })),
  })
  it('preselects all but the served, the previous and the three newest', () => {
    expect(pruneCandidates(versions(5, 4, 8))).toEqual([1, 2, 3])
    expect(pruneCandidates(versions(2, 1, 3))).toEqual([])
    expect(pruneCandidates(versions(null, null, 5))).toEqual([1, 2])
  })
})
