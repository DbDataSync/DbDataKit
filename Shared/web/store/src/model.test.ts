import { describe, expect, it } from 'vitest'
import type { ChangeRequestRow, CopyState, Diagnostic, MigrationPlan, ProposedChange, RequestList, StoragePlan } from './api'
import { changeSummary, copyAction, decideState, fieldDiff, isModelName, markersOf, migrationSummary, newModelText, saveState, sortRequests, storageReason, summaryOf } from './model'

const error = (over: Partial<Diagnostic> = {}): Diagnostic => ({ code: 'DBD-1', severity: 'error', file: 'm.yaml', line: 3, column: 5, message: 'bad type', fix: 'use VARCHAR(n)', ...over })
const inputs = { isNew: false, name: 'm', canWrite: true, changed: true, checking: false, stale: false, diagnostics: [] as Diagnostic[], saving: false }

describe('names', () => {
  it('accepts what the Store accepts and nothing else', () => {
    for (const ok of ['a', 'sales_territories', 'a-b', '0abc', 'a'.repeat(64)]) expect(isModelName(ok)).toBe(true)
    for (const bad of ['', 'A', '_a', '-a', 'a b', 'a.b', 'a'.repeat(65), '../x']) expect(isModelName(bad)).toBe(false)
  })
  it('starts a new model from a text that names it', () => {
    expect(newModelText('crm')).toContain('name: crm')
    expect(newModelText('')).toContain('name: my_model')
    expect(newModelText('crm')).toContain('schemaVersion: 1')
  })
})

describe('markers', () => {
  it('puts a problem with no position on the first line and adds the fix to the message', () => {
    expect(markersOf([error({ line: 0, column: 0, fix: null, message: 'no entities' }), error({ severity: 'warning' })])).toEqual([
      { line: 1, column: 1, message: 'no entities', severity: 'error' },
      { line: 3, column: 5, message: 'bad type use VARCHAR(n)', severity: 'warning' },
    ])
  })
})

describe('Save', () => {
  it('is on when the text changed, the Store has checked it and found nothing wrong', () => {
    expect(saveState(inputs)).toEqual({ allowed: true, reason: null })
    expect(saveState({ ...inputs, diagnostics: [error({ severity: 'warning' })] }).allowed).toBe(true)
  })
  it('says why it is off, in the order a person would fix things', () => {
    expect(saveState({ ...inputs, canWrite: false }).reason).toMatch(/author or administrator/)
    expect(saveState({ ...inputs, isNew: true, name: '' }).reason).toMatch(/name/)
    expect(saveState({ ...inputs, isNew: true, name: 'Bad Name' }).reason).toMatch(/lower-case/)
    expect(saveState({ ...inputs, changed: false }).reason).toMatch(/Nothing has changed/)
    expect(saveState({ ...inputs, stale: true }).reason).toMatch(/checking/)
    expect(saveState({ ...inputs, checking: true }).allowed).toBe(false)
    expect(saveState({ ...inputs, diagnostics: [error(), error()] }).reason).toMatch(/2 problems/)
    expect(saveState({ ...inputs, diagnostics: [error()] }).reason).toMatch(/1 problem to fix/)
  })
  it('a new model is saveable unchanged (it has never been saved)', () => {
    expect(saveState({ ...inputs, isNew: true, changed: false }).allowed).toBe(true)
  })
  it('the role comes first, whatever else is wrong', () => {
    expect(saveState({ ...inputs, canWrite: false, diagnostics: [error()], changed: false }).reason).toMatch(/role/)
  })
})

describe('summary', () => {
  it('counts entities and attributes', () => {
    expect(summaryOf(undefined)).toBe('No entities yet.')
    expect(summaryOf({ a: { attributes: { x: 1, y: 2 } }, b: {} })).toBe('2 entities, 2 attributes')
    expect(summaryOf({ a: { attributes: { x: 1 } } })).toBe('1 entity, 1 attribute')
  })
})

const plan = (existing: string[] | null, over: Partial<StoragePlan> = {}): StoragePlan => ({ engine: 'postgres', statements: [], existing, ...over })
const state = (status: string): CopyState => ({ name: 'default', definition: 'x', applied_at: null, status })

describe('what a copy needs next', () => {
  it('creates tables when none exist, migrates when behind or unrecorded, and is done when current', () => {
    expect(copyAction(undefined, undefined).action).toBe('none')
    expect(copyAction(plan([]), undefined).action).toBe('create')
    expect(copyAction(plan(['t']), state('behind')).action).toBe('migrate')
    expect(copyAction(plan(['t']), state('unknown')).action).toBe('migrate')
    expect(copyAction(plan(['t']), state('current'))).toEqual({ action: 'none', text: 'The tables are up to date with the model.' })
  })
  it('shows the connection problem instead of offering anything', () => {
    expect(copyAction(plan(null, { connection_problem: 'no login' }), undefined)).toEqual({ action: 'none', text: 'no login' })
  })
})

describe('a migration dry run', () => {
  const dry = (over: Partial<MigrationPlan>): MigrationPlan => ({ up_to_date: false, applied: false, statements: [], blocked: [], notes: [], ...over })
  const statement = { object: 't', description: 'add column', text: 'ALTER TABLE ...' }
  it('can be applied when it only adds', () => {
    expect(migrationSummary(dry({ statements: [statement] }))).toEqual({ text: '1 statement would be run; nothing is removed or changed in place.', canApply: true })
  })
  it('cannot when something needs more than adding, or when there is nothing to do', () => {
    expect(migrationSummary(dry({ statements: [statement], blocked: ['x'] })).canApply).toBe(false)
    expect(migrationSummary(dry({ blocked: ['x', 'y'] })).text).toMatch(/2 changes need more than adding/)
    expect(migrationSummary(dry({ up_to_date: true })).canApply).toBe(false)
  })
  it('names the reason a storage button is off', () => {
    expect(storageReason(false, true, false)).toMatch(/administrator/)
    expect(storageReason(true, false, false)).toMatch(/not valid/)
    expect(storageReason(true, true, true)).toMatch(/published/)
    expect(storageReason(true, true, false)).toBeNull()
  })
})

const request = (over: Partial<ChangeRequestRow> = {}): ChangeRequestRow => ({
  id: 'a'.repeat(32), model: 'm', instance: 'default', state: 'pending', proposed_by: 'agent:bot', proposed_by_name: null, by_agent: true, proposed_at: '2026-11-08T10:00:00Z',
  description: null, change_count: 1, decided_by: null, decided_at: null, note: null, ...over,
})
const list = (over: Partial<RequestList> = {}): RequestList => ({ requests: [], can_decide: true, decision_needs: 'author', approval: 'steward', ...over })

describe('who may decide', () => {
  it('is allowed for a pending request when the Store says the caller may', () => {
    expect(decideState(list(), request(), false)).toEqual({ allowed: true, reason: null })
  })
  it('says which role is needed when the Store says no, and that an agent never decides', () => {
    const reason = decideState(list({ can_decide: false, decision_needs: 'admin', approval: 'owner' }), request(), false).reason!
    expect(reason).toMatch(/admin role/)
    expect(reason).toMatch(/owner approves/)
    expect(reason).toMatch(/agent never decides/)
  })
  it('a decided request cannot be decided again, and says by whom', () => {
    expect(decideState(list(), request({ state: 'approved', decided_by: 'ada' }), false).reason).toBe('Already approved by ada.')
    expect(decideState(undefined, request(), false).allowed).toBe(false)
    expect(decideState(list(), request(), true).allowed).toBe(false)
  })
})

describe('what a request proposes, beside what the row holds', () => {
  const current = { code: 'EU', name: 'Europe', tier: 'gold' }
  it('an update shows the attributes it sets against the row and leaves the others unchanged', () => {
    const change: ProposedChange = { action: 'update', entity: 't', key: { code: 'EU' }, expected_version: 2, values: { name: 'Europe West' } }
    const diff = fieldDiff(change, current)
    expect(diff.find((f) => f.name === 'name')).toEqual({ name: 'name', before: 'Europe', after: 'Europe West', changed: true })
    expect(diff.find((f) => f.name === 'tier')).toEqual({ name: 'tier', before: 'gold', after: 'gold', changed: false })
    expect(diff.find((f) => f.name === 'code')!.changed).toBe(false)
  })
  it('an insert has no before, and a delete no after', () => {
    const insert = fieldDiff({ action: 'insert', entity: 't', values: { code: 'NA', name: 'North' } }, null)
    expect(insert.every((f) => f.before === undefined && f.changed)).toBe(true)
    const remove = fieldDiff({ action: 'delete', entity: 't', key: { code: 'EU' } }, current)
    expect(remove.every((f) => f.after === undefined && f.changed)).toBe(true)
  })
  it('an update of a row that is gone still lists what it would set', () => {
    const diff = fieldDiff({ action: 'update', entity: 't', key: { code: 'EU' }, values: { name: 'X' } }, null)
    expect(diff.find((f) => f.name === 'name')).toEqual({ name: 'name', before: undefined, after: 'X', changed: true })
  })
  it('summarises a request and sorts the waiting ones first, newest first', () => {
    expect(changeSummary([{ action: 'insert', entity: 't' }, { action: 'insert', entity: 't' }, { action: 'delete', entity: 't' }], 3)).toBe('add 2, remove 1')
    expect(changeSummary(undefined, 1)).toBe('1 change')
    const rows = [request({ id: 'old', state: 'approved' }), request({ id: 'a', proposed_at: '2026-11-01T00:00:00Z' }), request({ id: 'b', proposed_at: '2026-11-05T00:00:00Z' })]
    expect(sortRequests(rows).map((r) => r.id)).toEqual(['b', 'a', 'old'])
  })
})
