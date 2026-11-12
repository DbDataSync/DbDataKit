import { isValidElement, type ReactElement, type ReactNode } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { ApiError } from '@dbdatakit/shell'
import type { DeadLetterRecord, InDoubtStep, PostureState, RunUnits, TaskStatus } from '../api'
import { emptyPostureForm, emptyStepForm } from '../operator'
import { DeadLettersView, type DeadLettersViewProps } from './DeadLettersCard'
import { InDoubtView, type InDoubtViewProps } from './InDoubtCard'
import { PostureView, type PostureViewProps } from './PostureCard'
import { RunUnitsView } from './RunUnits'

const task: TaskStatus = { package: 'sales', channel: 'prod', task: 'orders', version: 3, next_due: null, last_run: null, running: 0, queued: 0, next_attempt_at: null, paused: false, paused_by: null, problem: null, required_labels: [], waiting_reason: null, owner: 'runner' }

function findAll(node: ReactNode, testId: string, found: ReactElement<Record<string, unknown>>[] = []): ReactElement<Record<string, unknown>>[] {
  if (Array.isArray(node)) { for (const child of node) findAll(child, testId, found); return found }
  if (!isValidElement(node)) return found
  const element = node as ReactElement<Record<string, unknown>>
  if (element.props['data-testid'] === testId) found.push(element)
  findAll(element.props.children as ReactNode, testId, found)
  return found
}
const press = (view: ReactNode, testId: string, index = 0) => (findAll(view, testId)[index].props.onClick as () => void)()

const step: InDoubtStep = { chain: 'c1', name: 'charge', key: '', attempt: 1, started: '2026-11-12T10:00:00Z', note: null }
const inDoubt = (over: Partial<InDoubtViewProps> = {}): InDoubtViewProps => ({ task, canAct: true, steps: [step], loading: false, error: null, open: null, form: emptyStepForm, busy: false, answered: null, failure: null, onOpen: () => {}, onForm: () => {}, onSend: () => {}, ...over })

describe('the steps in doubt', () => {
  it('lists the steps that wait and says when none do', () => {
    expect(renderToStaticMarkup(InDoubtView(inDoubt()))).toContain('charge')
    expect(renderToStaticMarkup(InDoubtView(inDoubt({ steps: [] })))).toContain('runner-in-doubt-none')
    expect(renderToStaticMarkup(InDoubtView(inDoubt({ steps: undefined, loading: true })))).toContain('runner-in-doubt-loading')
  })
  it('opens the answer form of the step that was pressed', () => {
    const opened: unknown[] = []
    press(InDoubtView(inDoubt({ onOpen: (s) => opened.push(s) })), 'runner-in-doubt-answer')
    expect(opened).toEqual([step])
    const text = renderToStaticMarkup(InDoubtView(inDoubt({ open: step })))
    expect(text).toContain('It did not happen')
    expect(text).toContain('on the audit')
  })
  it('asks for a result only when the step happened and blocks a result that is not JSON', () => {
    expect(renderToStaticMarkup(InDoubtView(inDoubt({ open: step })))).not.toContain('runner-in-doubt-result')
    const view = InDoubtView(inDoubt({ open: step, form: { happened: true, result: '{bad', retry: true } }))
    expect(renderToStaticMarkup(view)).toContain('not JSON')
    expect(findAll(view, 'runner-in-doubt-send')[0].props.disabled).toBe(true)
  })
  it('sends the answer it was written with', () => {
    const sent: unknown[] = []
    const form = { happened: true, result: '{"id":1}', retry: false }
    press(InDoubtView(inDoubt({ open: step, form, onSend: (s, f) => sent.push([s, f]) })), 'runner-in-doubt-send')
    expect(sent).toEqual([[step, form]])
  })
  it('offers nothing to a person without the operator role, and shows what was recorded and a refusal', () => {
    expect(findAll(InDoubtView(inDoubt({ canAct: false })), 'runner-in-doubt-answer')[0].props.disabled).toBe(true)
    expect(renderToStaticMarkup(InDoubtView(inDoubt({ canAct: false })))).toContain('needs the operator role')
    expect(renderToStaticMarkup(InDoubtView(inDoubt({ answered: 'charge: recorded as happened.' })))).toContain('recorded as happened')
    expect(renderToStaticMarkup(InDoubtView(inDoubt({ failure: new ApiError(409, 'The step is not waiting for an answer.', null) })))).toContain('not waiting for an answer')
  })
})

const letter = (over: Partial<DeadLetterRecord> = {}): DeadLetterRecord => ({ id: 'l1', source: 'record:Order', reason: 'the far side said 422', attempts: 2, status: 'open', first: '2026-11-12T09:00:00Z', last: '2026-11-12T10:00:00Z', resolved: null, has_body: true, ...over })
const letters = (over: Partial<DeadLettersViewProps> = {}): DeadLettersViewProps => ({ task, canAct: true, status: 'open', letters: [letter()], loading: false, error: null, busy: false, done: null, failure: null, onStatus: () => {}, onReplay: () => {}, onDiscard: () => {}, ...over })

describe('the dead letters', () => {
  it('shows why each was refused and how often', () => {
    const text = renderToStaticMarkup(DeadLettersView(letters()))
    expect(text).toContain('record:Order: the far side said 422')
    expect(text).toContain('refused 2 times')
    expect(renderToStaticMarkup(DeadLettersView(letters({ letters: [] })))).toContain('No open dead letter')
  })
  it('replays or discards the one that was pressed', () => {
    const calls: string[] = []
    const view = DeadLettersView(letters({ onReplay: (l) => calls.push(`replay ${l.id}`), onDiscard: (l) => calls.push(`discard ${l.id}`) }))
    press(view, 'runner-dead-letter-replay')
    press(view, 'runner-dead-letter-discard')
    expect(calls).toEqual(['replay l1', 'discard l1'])
  })
  it('does not offer a replay for a record with no body, and none for a resolved one', () => {
    const noBody = DeadLettersView(letters({ letters: [letter({ has_body: false })] }))
    expect(findAll(noBody, 'runner-dead-letter-replay')[0].props.disabled).toBe(true)
    expect(renderToStaticMarkup(noBody)).toContain('nothing to send again')
    const replayed = DeadLettersView(letters({ status: 'replayed', letters: [letter({ status: 'replayed', resolved: '2026-11-12T11:00:00Z' })] }))
    expect(findAll(replayed, 'runner-dead-letter-replay')).toHaveLength(0)
  })
  it('switches between open, replayed and discarded and is closed to a viewer', () => {
    const seen: string[] = []
    press(DeadLettersView(letters({ onStatus: (s) => seen.push(s) })), 'runner-dead-letter-status', 2)
    expect(seen).toEqual(['discarded'])
    const viewer = DeadLettersView(letters({ canAct: false }))
    expect(findAll(viewer, 'runner-dead-letter-replay')[0].props.disabled).toBe(true)
    expect(renderToStaticMarkup(viewer)).toContain('needs the operator role')
  })
})

const posture: PostureState = {
  all_packages: '*',
  settings: [{ name: 'longer_limits', default: 'default limits', effect: 'One task can hold the machine for hours.' }, { name: 'production_binding_on_non_production', default: 'a sandbox', effect: 'x' }],
  choices: [{ setting: 'longer_limits', scope: 'sales', chosen_by: 'grace', chosen_at: '2026-11-12T10:00:00Z', note: 'nightly load' }],
}
const postureProps = (over: Partial<PostureViewProps> = {}): PostureViewProps => ({ posture, loading: false, error: null, canEdit: true, form: emptyPostureForm(posture.settings), busy: false, done: null, failure: null, onForm: () => {}, onChoose: () => {}, onRevoke: () => {}, ...over })

describe('the posture card', () => {
  it('shows the choices with who made them and the effect of the default being chosen', () => {
    const text = renderToStaticMarkup(PostureView(postureProps()))
    expect(text).toContain('chosen by grace')
    expect(text).toContain('nightly load')
    expect(text).toContain('One task can hold the machine for hours.')
    expect(text).not.toContain('value="production_binding_on_non_production"')
  })
  it('records nothing until a package is named and the effect is confirmed', () => {
    const view = PostureView(postureProps())
    expect(findAll(view, 'runner-posture-choose')[0].props.disabled).toBe(true)
    expect(renderToStaticMarkup(view)).toContain('Name the package')
    const ready = PostureView(postureProps({ form: { setting: 'longer_limits', scope: 'billing', note: '', confirmed: true } }))
    expect(findAll(ready, 'runner-posture-choose')[0].props.disabled).toBe(false)
  })
  it('sends the choice it was written with and takes a choice back', () => {
    const calls: unknown[] = []
    const form = { setting: 'longer_limits', scope: 'billing', note: 'n', confirmed: true }
    const view = PostureView(postureProps({ form, onChoose: (f) => calls.push(['choose', f]), onRevoke: (s, sc) => calls.push(['revoke', s, sc]) }))
    press(view, 'runner-posture-choose')
    press(view, 'runner-posture-revoke')
    expect(calls).toEqual([['choose', form], ['revoke', 'longer_limits', 'sales']])
  })
  it('shows a non-administrator the choices and no form', () => {
    const text = renderToStaticMarkup(PostureView(postureProps({ canEdit: false })))
    expect(text).toContain('needs the admin role')
    expect(text).not.toContain('runner-posture-form')
  })
  it('says when nothing is loosened', () => {
    expect(renderToStaticMarkup(PostureView(postureProps({ posture: { ...posture, choices: [] } })))).toContain('runner-posture-none')
  })
})

const units: RunUnits = {
  run_id: 'r1', task: 'sales/prod/orders', chain: 'c1', truncated: false, summary: { total: 3, done: 1, skipped: 0, failed: 1, running: 1 },
  units: [
    { name: 'table', key: 'orders', state: 'completed', attempt: 1, started: '2026-11-12T10:00:00Z', completed: '2026-11-12T10:00:02Z', duration_ms: 2000, rows: 120, counts: { rows: 120 }, error: null },
    { name: 'table', key: 'lines', state: 'failed', attempt: 1, started: '2026-11-12T10:00:02Z', completed: '2026-11-12T10:00:03Z', duration_ms: 1000, rows: null, counts: {}, error: 'timeout' },
    { name: 'table', key: 'stock', state: 'started', attempt: 1, started: '2026-11-12T10:00:03Z', completed: null, duration_ms: null, rows: null, counts: {}, error: null },
  ],
}

describe('the table of units', () => {
  it('shows the summary, every unit with rows and duration, and the failed one first with its error', () => {
    const text = renderToStaticMarkup(RunUnitsView({ units, loading: false, error: null }))
    expect(text).toContain('3 units: 1 done, 1 running, 1 failed')
    expect(text).toContain('120')
    expect(text).toContain('2.0 s')
    expect(text).toContain('timeout')
    expect(text.indexOf('lines')).toBeLessThan(text.indexOf('orders'))
  })
  it('says a run that kept no units kept none, and explains a chain that is gone instead of showing an error', () => {
    expect(renderToStaticMarkup(RunUnitsView({ units: { ...units, units: [], summary: { total: 0, done: 0, skipped: 0, failed: 0, running: 0 } }, loading: false, error: null }))).toContain('kept no units')
    const gone = renderToStaticMarkup(RunUnitsView({ units: undefined, loading: false, error: new ApiError(404, 'The attempt chain is not known', null) }))
    expect(gone).toContain('runner-run-units-gone')
    expect(gone).not.toContain('The attempt chain is not known')
  })
})
