import { describe, expect, it } from 'vitest'
import type { Diagnostic } from './api'
import { isModelName, markersOf, newModelText, saveState, summaryOf } from './model'

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

