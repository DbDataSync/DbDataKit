import { describe, expect, it } from 'vitest'
import type { TaskDecision, TaskSchedule } from './api'
import { decisionSummary, effectiveOwner, isoOf, openOwnerForm, ownerBlock, ownerBody, ownerLabel, parseInputs, type OwnerForm } from './ownership'

const schedule = (over: Partial<TaskSchedule> = {}): TaskSchedule => ({ task: 'p/c/t', owner: 'old', owner_set_by: null, owner_set_at: null, reason: null, handled_up_to: null, paused: false, paused_by: null, paused_at: null, running: 0, queued: 0, version: 1, ...over })
const form = (over: Partial<OwnerForm> = {}): OwnerForm => ({ owner: 'observe', reason: 'trial', handledUpTo: '', ...over })

describe('who schedules a task', () => {
  it('is the runner when the runner says nothing, and the words are the product\'s or the runner\'s', () => {
    expect(effectiveOwner(undefined)).toBe('runner')
    expect(effectiveOwner('old')).toBe('old')
    expect(effectiveOwner('observe')).toBe('observe')
    expect(effectiveOwner('runner')).toBe('runner')
    expect(ownerLabel('old')).toBe('The publishing product')
    expect(ownerLabel(undefined)).toBe('This runner')
    expect(ownerLabel('other')).toBe('other')
  })
  it('opens on the next step: observe, then the runner, then back', () => {
    expect(openOwnerForm('old', undefined).owner).toBe('observe')
    expect(openOwnerForm('observe', undefined).owner).toBe('runner')
    expect(openOwnerForm('runner', undefined).owner).toBe('old')
    expect(openOwnerForm('observe', schedule({ handled_up_to: '2026-11-08T10:00:00Z' })).handledUpTo).toBe('2026-11-08T10:00:00Z')
  })
})

describe('what stops a move', () => {
  it('refuses the owner it already has and a move without a reason', () => {
    expect(ownerBlock('old', form({ owner: 'old' }))).toContain('already')
    expect(ownerBlock('old', form({ reason: ' ' }))).toContain('Say why')
    expect(ownerBlock('old', form())).toBeNull()
  })
  it('needs the position, as a time, to hand a task to the runner (not to take it back)', () => {
    expect(ownerBlock('observe', form({ owner: 'runner' }))).toContain('Give the time')
    expect(ownerBlock('observe', form({ owner: 'runner', handledUpTo: 'yesterday' }))).toContain('not a time')
    expect(ownerBlock('observe', form({ owner: 'runner', handledUpTo: '2026-11-08T10:00:00Z' }))).toBeNull()
    expect(ownerBlock('runner', form({ owner: 'old' }))).toBeNull()
  })
  it('sends the reason trimmed, and the position as an ISO time only for a hand-over', () => {
    expect(ownerBody('old', form({ reason: ' trial ' }))).toEqual({ owner: 'observe', reason: 'trial' })
    expect(ownerBody('observe', form({ owner: 'runner', handledUpTo: '2026-11-08 10:00' }))).toEqual({ owner: 'runner', reason: 'trial', handled_up_to: '2026-11-08T10:00:00.000Z' })
    expect(ownerBody('runner', form({ owner: 'old', handledUpTo: '2026-11-08T10:00:00Z' }))).toEqual({ owner: 'old', reason: 'trial' })
    expect(ownerBody('old', form({ reason: '' }))).toBeNull()
  })
  it('reads a time with or without a zone as UTC unless it says another', () => {
    expect(isoOf('2026-11-08T10:00')).toBe('2026-11-08T10:00:00.000Z')
    expect(isoOf('2026-11-08T10:00:00+01:00')).toBe('2026-11-08T09:00:00.000Z')
    expect(isoOf('')).toBeNull()
    expect(isoOf('nonsense')).toBeNull()
  })
})

describe('the inputs of a run', () => {
  it('reads name=value lines and names the first that is not one', () => {
    expect(parseInputs('a=1\n\n b = two words \r\nc=')).toEqual({ inputs: { a: '1', b: 'two words', c: '' }, problem: null })
    expect(parseInputs('').inputs).toEqual({})
    expect(parseInputs('a=1\nnope').problem).toBe('"nope" is not name=value.')
    expect(parseInputs('1a=1').problem).toContain('not an input name')
    expect(parseInputs('a=1\na=2').problem).toContain('twice')
  })
})

describe('the decisions of an observed task', () => {
  const at = (minutes: number) => new Date(Date.UTC(2026, 10, 8, 10, minutes)).toISOString()
  const d = (source: string, minutes: number, paused = false): TaskDecision => ({ source, at: at(minutes), missed: 0, paused })
  it('pairs what the runner would have queued with what the product did, within a minute and a half and by pause', () => {
    expect(decisionSummary([d('runner', 0), d('old', 1), d('runner', 60), d('old', 60)])).toEqual({ runner: 2, old: 2, unmatched: 0 })
    expect(decisionSummary([d('runner', 0), d('old', 5)])).toEqual({ runner: 1, old: 1, unmatched: 2 })
    expect(decisionSummary([d('runner', 0, true), d('old', 0, false)]).unmatched).toBe(2)
    expect(decisionSummary([])).toEqual({ runner: 0, old: 0, unmatched: 0 })
  })
})
