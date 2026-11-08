import { describe, expect, it } from 'vitest'
import type { CopyState, Diagnostic, MigrationPlan, StoragePlan } from './api'
import { copyAction, isModelName, markersOf, migrationSummary, newModelText, saveState, storageReason, summaryOf } from './model'

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

