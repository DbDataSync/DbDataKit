import { describe, expect, it } from 'vitest'
import { checkLine, moneyWords, parseMinimum, promotionLine, runLine, scoreWords, scoresOverTime, type EvalCheck, type EvalState } from './evaluation'

// Plan 27a AG24: what the evaluation panel of an agent says. The server runs and checks; these are the words, and the rule that a measurement of the suite and a judgement of the judge model are told apart.

const check = (over: Partial<EvalCheck> = {}): EvalCheck => ({ kind: 'answer-contains', detail: 'answer-contains: EMEA', method: 'suite', score: 1, message: 'The answer says it.', judgement: null, ...over })

describe('what a check says', () => {
  it('says a check of the suite held or did not hold', () => {
    expect(checkLine(check())).toBe('Held: The answer says it.')
    expect(checkLine(check({ score: 0, message: 'The answer does not say it.' }))).toBe('Did not hold: The answer does not say it.')
  })
  it('shows the judge model\'s score and reasons as a judgement and never as a measurement', () => {
    expect(checkLine(check({ kind: 'rubric', method: 'judge', score: 0.5, message: 'The judge model scored it 0.5.', judgement: 'It gives the figures but not the period.' })))
      .toBe('Judged by the judge model at 50%: It gives the figures but not the period. (a judgement, not a measurement)')
  })
  it('says a check nobody could decide, which is left out of the score', () => {
    expect(checkLine(check({ kind: 'rubric', method: 'judge', score: null, message: 'No judge model is bound, so the rubric was not judged.' }))).toBe('Not decided: No judge model is bound, so the rubric was not judged.')
  })
})

describe('runs', () => {
  const run = { agentVersion: 'abc123', model: 'anthropic/claude', passed: 5, total: 8, score: 0.62, cost: 0.0425, trigger: 'manual' }

  it('says the version, the model, the cases that passed, the score and the cost', () => {
    expect(runLine(run)).toBe('Version abc123 on anthropic/claude: 5 of 8 cases passed, score 62%, cost $0.04.')
    expect(runLine({ ...run, model: null, total: 1, passed: 1, score: 1, cost: 0, trigger: 'schedule' })).toBe('Version abc123: 1 of 1 case passed, score 100%, cost $0 (scheduled).')
  })
  it('writes the scores over time oldest first', () => {
    expect(scoresOverTime([{ score: 0.9 }, { score: 0.62 }, { score: 0.5 }])).toBe('50% → 62% → 90%')
  })
  it('writes small amounts with enough digits to see them', () => {
    expect(moneyWords(0.0016)).toBe('$0.0016')
    expect(scoreWords(0.8333)).toBe('83%')
  })
})

describe('the minimum before a promotion', () => {
  const state = (over: Partial<EvalState> = {}): Pick<EvalState, 'minimum' | 'promotion' | 'channel'> => ({ minimum: null, promotion: { version: 'abc123', refusal: null }, channel: 'main', ...over })

  it('says nothing holds a promotion up when no minimum is set', () => {
    expect(promotionLine(state())).toBe('No minimum score is set for main, so a promotion is not held up by the evaluation.')
  })
  it('gives the refusal the server would give, naming the score', () => {
    const refusal = 'helper abc123 cannot be promoted to main: its last evaluation scored 0.42 (1 of 2 cases passed), below the minimum of 0.80 set for main.'
    expect(promotionLine(state({ minimum: { minimum: 0.8, setBy: 'ada', setAt: '2026-11-12T00:00:00Z' }, promotion: { version: 'abc123', refusal } }))).toBe(refusal)
  })
  it('says a promotion may go on when the version reached the minimum', () => {
    expect(promotionLine(state({ minimum: { minimum: 0.4, setBy: 'ada', setAt: '2026-11-12T00:00:00Z' } }))).toBe('Version abc123 reached the minimum of 40%: a promotion to main may go on.')
  })
  it('reads a minimum typed as a percentage, blank for none, and refuses what is not one', () => {
    expect(parseMinimum('80')).toEqual({ value: 0.8 })
    expect(parseMinimum(' 75% ')).toEqual({ value: 0.75 })
    expect(parseMinimum('')).toEqual({ value: null })
    expect(parseMinimum('101')).toBeNull()
    expect(parseMinimum('0.8')).toBeNull()
    expect(parseMinimum('abc')).toBeNull()
  })
})
