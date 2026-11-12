import { describe, expect, it } from 'vitest'
import type { PostureSettingInfo, RunUnit } from './api'
import { choiceFor, choosableSettings, durationText, emptyPostureForm, parseStepResult, postureBlock, sortUnits, stepBlock, unitsLine, unitStateText } from './operator'

const unit = (over: Partial<RunUnit>): RunUnit => ({ name: 'table', key: 'a', state: 'completed', attempt: 1, started: '2026-11-12T10:00:00Z', completed: null, duration_ms: null, rows: null, counts: {}, error: null, ...over })
const settings: PostureSettingInfo[] = [
  { name: 'longer_limits', default: 'default limits', effect: 'more time' },
  { name: 'production_binding_on_non_production', default: 'a sandbox', effect: 'production far side' },
]

describe('units', () => {
  it('words the states and the durations', () => {
    expect(unitStateText('started')).toBe('running')
    expect(unitStateText('completed')).toBe('done')
    expect(unitStateText('weird')).toBe('weird')
    expect([durationText(null), durationText(120), durationText(3400), durationText(180_000)]).toEqual(['', '120 ms', '3.4 s', '3 min'])
  })
  it('sums the table in one line', () => {
    expect(unitsLine({ total: 0, done: 0, skipped: 0, failed: 0, running: 0 })).toBe('The run kept no units.')
    expect(unitsLine({ total: 1, done: 1, skipped: 0, failed: 0, running: 0 })).toBe('1 unit: 1 done')
    expect(unitsLine({ total: 4, done: 1, skipped: 1, failed: 1, running: 1 })).toBe('4 units: 1 done, 1 running, 1 failed, 1 not run')
  })
  it('puts failed units first, then running ones, and keeps the order otherwise', () => {
    const sorted = sortUnits([unit({ key: 'a' }), unit({ key: 'b', state: 'started' }), unit({ key: 'c', state: 'failed' }), unit({ key: 'd' })])
    expect(sorted.map((u) => u.key)).toEqual(['c', 'b', 'a', 'd'])
  })
})

describe('the answer for a step in doubt', () => {
  it('needs valid JSON only when the step happened, and empty is no value', () => {
    expect(stepBlock({ happened: false, result: 'not json', retry: true })).toBeNull()
    expect(stepBlock({ happened: true, result: '', retry: true })).toBeNull()
    expect(stepBlock({ happened: true, result: '{"id": 7}', retry: true })).toBeNull()
    expect(stepBlock({ happened: true, result: '{oops', retry: true })).toContain('not JSON')
    expect(parseStepResult(' 42 ')).toEqual({ ok: true, value: 42 })
    expect(parseStepResult('')).toEqual({ ok: true, value: null })
  })
})

describe('the posture form', () => {
  it('offers every default but the binding one, which is chosen where the binding is made', () => {
    expect(choosableSettings(settings).map((s) => s.name)).toEqual(['longer_limits'])
    expect(emptyPostureForm(settings).setting).toBe('longer_limits')
  })
  it('asks for a package and a confirmation before it can be recorded', () => {
    const form = emptyPostureForm(settings)
    expect(postureBlock(form)).toContain('Name the package')
    expect(postureBlock({ ...form, scope: 'Bad Name' })).toContain('lower case')
    expect(postureBlock({ ...form, scope: 'sales' })).toContain('Confirm')
    expect(postureBlock({ ...form, scope: 'sales', confirmed: true })).toBeNull()
    expect(postureBlock({ ...form, scope: '*', confirmed: true })).toBeNull()
  })
  it('finds the choice that stands for a setting and a scope', () => {
    const choices = [{ setting: 'longer_limits', scope: 'sales', chosen_by: 'ada', chosen_at: '2026-11-12T10:00:00Z', note: null }]
    expect(choiceFor(choices, 'longer_limits', 'sales')?.chosen_by).toBe('ada')
    expect(choiceFor(choices, 'longer_limits', '*')).toBeNull()
  })
})
