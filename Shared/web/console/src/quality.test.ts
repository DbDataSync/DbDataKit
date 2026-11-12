import { describe, expect, it } from 'vitest'
import { alertsLine, blockLine, datasetState, historyPath, measureLine, panelLine, runBlockedReason, sortDatasets, stateWord, type QualityCheck, type QualityDataset, type QualityServer } from './quality'

const check = (over: Partial<QualityCheck> = {}): QualityCheck => ({
  check_id: 'c', severity: 'block', on_error: 'alert', status: 'pass', status_since: 't', measured: 0, threshold: 0, evaluated_at: 't', blocking: false, blocked_since: null, server: 'Sync one', server_id: 'sync-1', product: 'dbdatasync', ...over,
})
const dataset = (name: string, ...checks: QualityCheck[]): QualityDataset => ({ dataset: name, blocking: checks.filter((c) => c.blocking).length, checks })
const server = (over: Partial<QualityServer> = {}): QualityServer => ({ server: 'Sync one', server_id: 'sync-1', product: 'dbdatasync', error: null, checks: 1, can_run: true, run_note: null, ...over })

describe('a dataset as a whole', () => {
  it('is blocked when a check blocks, failing for a failed warn check, not checked when one could not run, else passing', () => {
    expect(datasetState(dataset('a', check({ status: 'fail', blocking: true })))).toBe('blocked')
    expect(datasetState(dataset('a', check({ status: 'fail', severity: 'warn' })))).toBe('failing')
    expect(datasetState(dataset('a', check({ status: 'error' }), check()))).toBe('error')
    expect(datasetState(dataset('a', check()))).toBe('pass')
    expect(stateWord('error')).toBe('Could not be checked')
  })

  it('lists the blocked first, then failing, then the unchecked, then the passing, by name inside a state', () => {
    const sorted = sortDatasets([
      dataset('d-pass', check()), dataset('b-blocked', check({ status: 'fail', blocking: true })), dataset('c-error', check({ status: 'error' })),
      dataset('a-pass', check()), dataset('e-failing', check({ status: 'fail', severity: 'warn' })), dataset('a-blocked', check({ status: 'fail', blocking: true })),
    ])
    expect(sorted.map((d) => d.dataset)).toEqual(['a-blocked', 'b-blocked', 'e-failing', 'c-error', 'a-pass', 'd-pass'])
  })
})

describe('the sentences', () => {
  it('say what was measured against what is allowed, and that an error could not run', () => {
    expect(measureLine(check({ status: 'fail', measured: 2, threshold: 0 }))).toBe('measured 2, allowed 0')
    expect(measureLine(check({ status: 'fail', measured: 7, threshold: null }))).toBe('measured 7')
    expect(measureLine(check({ status: 'error', measured: null }))).toBe('could not run')
    expect(measureLine(check({ status: 'pass', measured: null, threshold: null }))).toBe('within what the check allows')
  })

  it('say how a block ends, and nothing for a check that does not block', () => {
    expect(blockLine(check())).toBeNull()
    expect(blockLine(check({ blocking: true, blocked_since: '2026-11-08' }))).toContain('since 2026-11-08')
    expect(blockLine(check({ blocking: true, blocked_since: '2026-11-08' }))).toContain('clears when the check passes')
  })

  it('give a lineage node its line, or say that none is stored', () => {
    expect(panelLine(undefined)).toBe('No check is stored for this dataset.')
    expect(panelLine(dataset('a', check({ status: 'fail', blocking: true }), check()))).toBe('2 checks, 1 blocking, 1 failing')
    expect(panelLine(dataset('a', check()))).toBe('1 check, all passing')
    expect(panelLine(dataset('a', check({ status: 'error' })))).toBe('1 check, some could not run')
  })
})

describe('whether a check can be evaluated from the screen', () => {
  it('can for an administrator on a server that evaluates on request', () => {
    expect(runBlockedReason(server(), true)).toBeNull()
  })

  it('says why not: no server, a server that did not answer, one that evaluates after a refresh, a person who is not an administrator', () => {
    expect(runBlockedReason(undefined, true)).toContain('not paired')
    expect(runBlockedReason(server({ error: 'timed out' }), true)).toBe('Sync one did not answer: timed out')
    expect(runBlockedReason(server({ can_run: false, run_note: 'Build evaluates after a refresh.' }), true)).toBe('Build evaluates after a refresh.')
    expect(runBlockedReason(server({ can_run: false }), true)).toBe('Sync one cannot evaluate on request.')
    expect(runBlockedReason(server(), false)).toContain('admin role')
  })
})

describe('where the history of a check is read', () => {
  it('is the evaluating product\'s own route', () => {
    expect(historyPath('dbdatabuild')).toBe('/api/v1/quality/history')
    expect(historyPath('dbdatasync')).toBe('/api/quality/history')
  })
})

describe('alertsLine (plan 23a FQ13)', () => {
  const state = { configured: true, channel: 'ops', channelKnown: true, problem: null, recipients: ['a@example.com'] }
  it('says what is set, and what is wrong with it', () => {
    expect(alertsLine({ ...state, configured: false, channel: null, recipients: [] })).toContain('are off')
    expect(alertsLine(state)).toBe('A failed check is sent through the channel ops to a@example.com, once when it fails and once when it passes again.')
    expect(alertsLine({ ...state, channelKnown: false })).toContain('nothing is sent')
    expect(alertsLine({ ...state, problem: 'settings.yaml, line 4: bad' })).toBe('The alert settings cannot be used: settings.yaml, line 4: bad')
  })
})
