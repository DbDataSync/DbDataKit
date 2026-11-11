import { isValidElement, type ReactElement, type ReactNode } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { ApiError } from '@dbdatakit/shell'
import { choiceKey, type RefreshChoice } from '../refresh'
import type { CommandAnswer, RefreshResult } from '../types'
import { RefreshAnswerView, RefreshView, type RefreshViewProps } from './RefreshPanel'

const plans = [{ connection: 'warehouse', name: 'nightly', title: 'Nightly', members: 2, settings: { schedule: '0 3 * * *' } }]
const chosen: RefreshChoice = { connection: 'warehouse', plan: 'nightly', selectors: '', onlyFailed: false, inputs: '', dryRun: false }

function props(over: Partial<RefreshViewProps> = {}): RefreshViewProps {
  return { plans, loading: false, listError: null, allowed: true, choice: chosen, dry: null, busy: null, answer: undefined, failure: null, onChoice: () => {}, onDryRun: () => {}, onRun: () => {}, ...over }
}
const html = (over?: Partial<RefreshViewProps>) => renderToStaticMarkup(RefreshView(props(over)))

function find(node: ReactNode, testId: string): ReactElement<Record<string, unknown>> | undefined {
  if (Array.isArray(node)) { for (const child of node) { const found = find(child, testId); if (found) return found } return undefined }
  if (!isValidElement(node)) return undefined
  const element = node as ReactElement<Record<string, unknown>>
  if (element.props['data-testid'] === testId) return element
  if (typeof element.type === 'function') return find((element.type as (p: unknown) => ReactNode)(element.props), testId)
  return find(element.props.children as ReactNode, testId)
}
const disabled = (over: Partial<RefreshViewProps>, id: string) => find(RefreshView(props(over)), id)!.props.disabled

const answer = (exit: number, data: RefreshResult): CommandAnswer<RefreshResult> => ({ exit, document: { ok: exit === 0, data }, text: null })

describe('the refresh form', () => {
  it('lists the plans and says what a run does and who may do it', () => {
    const text = html()
    expect(text).toContain('data-testid="build-refresh-plan"')
    expect(text).toContain('warehouse / nightly (2 loads)')
    expect(text).toContain('operator role')
    expect(text).toContain('Nightly: 2 loads, scheduled 0 3 * * *')
  })
  it('shows loading, no plans, and a list that failed', () => {
    expect(html({ plans: undefined, loading: true })).toContain('Loading')
    expect(html({ plans: [] })).toContain('data-testid="build-refresh-none"')
    expect(html({ plans: undefined, listError: new ApiError(500, 'The project has no compiled plans.', null) })).toContain('The project has no compiled plans.')
  })
  it('keeps the run off until a dry run of this choice passed, and says why', () => {
    expect(disabled({}, 'build-refresh-dry-run')).toBe(false)
    expect(disabled({}, 'build-refresh-run')).toBe(true)
    expect(html()).toContain('Run a dry run of this choice first')
    expect(disabled({ dry: { key: choiceKey(chosen), ok: true } }, 'build-refresh-run')).toBe(false)
    expect(disabled({ dry: { key: choiceKey({ ...chosen, selectors: 'x' }), ok: true } }, 'build-refresh-run')).toBe(true)
    expect(html({ dry: { key: choiceKey(chosen), ok: false } })).toContain('did not pass')
  })
  it('turns both buttons off for someone without the role, with the reason, and while a run is under way', () => {
    expect(disabled({ allowed: false }, 'build-refresh-dry-run')).toBe(true)
    expect(html({ allowed: false })).toContain('needs the operator role')
    expect(disabled({ busy: 'run' }, 'build-refresh-dry-run')).toBe(true)
    expect(html({ busy: 'run' })).toContain('build-refresh-running')
    expect(disabled({ choice: { ...chosen, plan: '' } }, 'build-refresh-dry-run')).toBe(true)
  })
  it('shows a refusal in the server words', () => {
    expect(html({ failure: new ApiError(403, '`connection refresh` needs the operator role.', null) })).toContain('Refused: `connection refresh` needs the operator role.')
  })
})

describe('the answer of a refresh', () => {
  const run = answer(1, {
    outcome: 'completed_with_failures',
    results: [{ model: 'a', member: 'load', load: 'load', state: 'done', rows: 12, duration_ms: 40 }, { model: 'b', member: 'load', load: 'load', state: 'failed', reason: 'check failed' }],
    checks: {
      run_id: 'r1', results_in: '/srv', evaluated: 2, passed: 1, failed: 1, errors: 0, blocking: 1, notes: ['The checks of c were not evaluated: the connection is DuckDB.'],
      results: [{ model: 'a', check: 'rows', kind: 'row_count', severity: 'warn', status: 'pass', measured: 12, threshold: 1, sample_keys: [], blocking: false },
        { model: 'a', check: 'fresh', kind: 'freshness', severity: 'block', status: 'fail', measured: 30, threshold: 24, sample_keys: ['k1', 'k2'], message: 'too old', blocking: true }],
    },
  })
  it('shows how it ended, each load, and the checks that ran afterwards', () => {
    const text = renderToStaticMarkup(RefreshAnswerView({ answer: run, running: false }) as ReactElement)
    expect(text).toContain('The refresh completed with failures.')
    expect(text).toContain('1 done, 1 failed, 0 skipped, 0 not run')
    expect(text).toContain('data-testid="build-refresh-load-b.load"')
    expect(text).toContain('check failed')
    expect(text).toContain('2 evaluated: 1 passed, 1 failed, 0 could not run, 1 block their dependents')
    expect(text).toContain('Recorded under run r1')
    expect(text).toContain('fail (blocks)')
    expect(text).toContain('30 / 24')
    expect(text).toContain('for example k1 | k2')
    expect(text).toContain('the connection is DuckDB')
  })
  it('says when checks could not be recorded, and when none ran', () => {
    const broken = answer(1, { outcome: 'completed', checks: { run_id: 'r', results_in: '/s', evaluated: 0, passed: 0, failed: 0, errors: 0, blocking: 0, notes: [], error: 'the state database is locked', results: [] } })
    expect(renderToStaticMarkup(RefreshAnswerView({ answer: broken, running: false }) as ReactElement)).toContain('The checks could not be recorded: the state database is locked')
    expect(renderToStaticMarkup(RefreshAnswerView({ answer: answer(0, { outcome: 'completed', results: [] }), running: false }) as ReactElement)).toContain('build-refresh-no-checks')
  })
  it('shows what a dry run resolved and that nothing was executed, and no checks note', () => {
    const dry = answer(0, { dry_run: true, resolved_inputs: [{ model: 'a', operation: 'load', member: 'load', inputs: [{ name: 'since', type: 'date', source: 'flag', value: '2026-01-01' }] }] })
    const text = renderToStaticMarkup(RefreshAnswerView({ answer: dry, running: false }) as ReactElement)
    expect(text).toContain('Nothing was executed')
    expect(text).toContain('since = 2026-01-01 (flag)')
    expect(text).not.toContain('build-refresh-no-checks')
  })
  it('shows the command problems when there is no document', () => {
    const text = renderToStaticMarkup(RefreshAnswerView({ answer: { exit: 2, document: null, text: 'There is no plan called nightly.' }, running: false }) as ReactElement)
    expect(text).toContain('There is no plan called nightly.')
  })
})
