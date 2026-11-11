import { describe, expect, it } from 'vitest'
import { ApiError } from '@dbdatakit/shell'
import { badInput, choiceKey, dryRunBlock, plansOf, refreshBody, refreshFailure, runBlock, summarize, type RefreshChoice } from './refresh'
import type { CommandAnswer, RefreshResult } from './types'

const choice = (over: Partial<RefreshChoice> = {}): RefreshChoice => ({ connection: 'warehouse', plan: 'nightly', selectors: '', onlyFailed: false, inputs: '', dryRun: false, ...over })
const answer = (exit: number, data: RefreshResult): CommandAnswer<RefreshResult> => ({ exit, document: { ok: exit === 0, data }, text: null })

describe('the body of a refresh', () => {
  it('sends only what was chosen, and never says where the checks are recorded', () => {
    expect(refreshBody(choice())).toEqual({ connection: 'warehouse', plan: 'nightly' })
    const body = refreshBody(choice({ selectors: 'marts.fct_events  tag:daily,+staging.events', onlyFailed: true, inputs: 'since=2026-01-01\n\nfct.limit=5', dryRun: true }))
    expect(body).toEqual({ connection: 'warehouse', plan: 'nightly', selectors: ['marts.fct_events', 'tag:daily', '+staging.events'], only_failed: true, input: ['since=2026-01-01', 'fct.limit=5'], dry_run: true })
    expect(Object.keys(body)).not.toContain('check_results')
  })
  it('a real run is the same choice as its dry run', () => {
    expect(choiceKey(choice({ dryRun: true }))).toBe(choiceKey(choice({ dryRun: false })))
    expect(choiceKey(choice({ selectors: 'a' }))).not.toBe(choiceKey(choice()))
  })
})

describe('inputs', () => {
  it('finds the first line that is not name=value', () => {
    expect(badInput('a=1\nm.b=')).toBeNull()
    expect(badInput('a=1\nnonsense\nc=3')).toBe('nonsense')
  })
})

describe('the buttons say why they are off', () => {
  it('names the role, a missing plan, a bad input and a run under way', () => {
    expect(dryRunBlock(choice(), false, false)).toContain('operator role')
    expect(dryRunBlock(choice({ plan: '' }), true, false)).toContain('Choose a refresh plan')
    expect(dryRunBlock(choice({ inputs: 'oops' }), true, false)).toContain('"oops"')
    expect(dryRunBlock(choice(), true, true)).toContain('under way')
    expect(dryRunBlock(choice(), true, false)).toBeNull()
    expect(dryRunBlock(choice(), undefined, false)).toBeNull()   // the offer has not come back yet: the server decides
  })
  it('a run needs a passed dry run of exactly this choice', () => {
    const key = choiceKey(choice())
    expect(runBlock(choice(), true, false, null)).toContain('dry run')
    expect(runBlock(choice({ selectors: 'x' }), true, false, { key, ok: true })).toContain('dry run')
    expect(runBlock(choice(), true, false, { key, ok: false })).toContain('did not pass')
    expect(runBlock(choice(), true, false, { key, ok: true })).toBeNull()
    expect(runBlock(choice(), false, false, { key, ok: true })).toContain('operator role')
  })
})

describe('refusals in words', () => {
  it('shows the server sentence for a 403 and a plain one when nothing answered', () => {
    expect(refreshFailure(new ApiError(403, '`connection refresh` needs the operator role.', null))).toBe('Refused: `connection refresh` needs the operator role.')
    expect(refreshFailure(new ApiError(400, '`check_results` is not an input of connection refresh.', null))).toContain('not an input')
    expect(refreshFailure(new Error('x'))).toContain('did not answer')
  })
})

describe('the summary of an answer', () => {
  it('counts the loads by how they ended and tones the line', () => {
    const s = summarize(answer(1, { outcome: 'completed_with_failures', results: [
      { model: 'a', member: 'l', load: 'l', state: 'done' }, { model: 'b', member: 'l', load: 'l', state: 'failed' }, { model: 'c', member: 'l', load: 'l', state: 'skipped' }, { model: 'd', member: 'd.l', load: 'l', state: 'not run' }] }))!
    expect([s.done, s.failed, s.skipped, s.notRun, s.tone]).toEqual([1, 1, 1, 1, 'warn'])
    expect(s.headline).toBe('The refresh completed with failures.')
  })
  it('says a dry run executed nothing', () => {
    expect(summarize(answer(0, { dry_run: true }))!.headline).toContain('Nothing was executed')
    expect(summarize(answer(1, { dry_run: true }))!.tone).toBe('bad')
    expect(summarize({ exit: 2, document: null, text: 'boom' })).toBeNull()
  })
})

describe('the plans offered', () => {
  it('are those of the chosen connection', () => {
    const plans = [{ connection: 'a', name: 'p', members: 1, settings: {} }, { connection: 'b', name: 'q', members: 2, settings: {} }]
    expect(plansOf(plans, '').length).toBe(2)
    expect(plansOf(plans, 'b').map((p) => p.name)).toEqual(['q'])
  })
})
