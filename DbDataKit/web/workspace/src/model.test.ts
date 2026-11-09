import { describe, expect, it } from 'vitest'
import { abandonState, createState, describeEvent, isName, nameProblem } from './model'

describe('names', () => {
  it('accepts what the server accepts and nothing else', () => {
    for (const ok of ['a', 'new-orders', 'a_b', '0abc', 'a'.repeat(32)]) expect(isName(ok)).toBe(true)
    for (const bad of ['', 'A', '_a', '-a', 'a b', 'a.b', 'a'.repeat(33), '../x']) expect(isName(bad)).toBe(false)
  })
  it('says why a name would be refused', () => {
    expect(nameProblem('', 'workspace')).toBe('Give the workspace a name.')
    expect(nameProblem('a'.repeat(33), 'project')).toBe('At most 32 characters.')
    expect(nameProblem('Has Space', 'project')).toContain('Lower-case')
    expect(nameProblem('fine', 'project')).toBeNull()
  })
})

describe('create', () => {
  const ok = { name: 'draft', canWrite: true, busy: false, what: 'workspace' as const }
  it('is on for a good name by someone who may', () => {
    expect(createState(ok)).toEqual({ enabled: true, why: null })
  })
  it('is off while a call is out, with a bad name, a taken name, or without the role, and says why', () => {
    expect(createState({ ...ok, busy: true })).toEqual({ enabled: false, why: null })
    expect(createState({ ...ok, name: '' })).toEqual({ enabled: false, why: null })
    expect(createState({ ...ok, name: 'Bad' }).why).toContain('Lower-case')
    expect(createState({ ...ok, taken: ['draft'] }).why).toBe('There is a workspace called "draft" already.')
    expect(createState({ ...ok, canWrite: false }).why).toContain('author')
    expect(createState({ ...ok, canWrite: false, what: 'project' }).why).toContain('administrator')
  })
})

describe('abandon', () => {
  it('is the owner\'s, with the author role', () => {
    expect(abandonState({ mine: true, canWrite: true })).toEqual({ enabled: true, why: null })
    expect(abandonState({ mine: false, canWrite: true }).why).toBe('A workspace is abandoned by its owner.')
    expect(abandonState({ mine: true, canWrite: false }).why).toContain('author')
  })
})

describe('history', () => {
  it('reads as a sentence, with the detail when there is one', () => {
    expect(describeEvent({ kind: 'committed', actor: 'Ada', detail: 'Title the grid', at: '2026-11-09T10:00:00Z' })).toBe('Ada committed: Title the grid')
    expect(describeEvent({ kind: 'created', actor: 'Ada', detail: null, at: '' })).toBe('Ada made')
    expect(describeEvent({ kind: 'frobbed', actor: 'Ada', detail: null, at: '' })).toBe('Ada frobbed')
  })
})
