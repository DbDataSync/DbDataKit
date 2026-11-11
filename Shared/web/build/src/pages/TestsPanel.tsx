import { useMutation } from '@tanstack/react-query'
import { ErrorBanner } from '@dbdatakit/shell'
import { buildApi } from '../api'
import type { CommandAnswer, TestsResult } from '../types'
import { Problems } from './Problems'

const wrap = { whiteSpace: 'normal', overflowWrap: 'anywhere' } as const

/** The answer of `project tests run`: the counts, and each test that did not pass with its violating rows. */
export function TestsAnswerView({ answer }: { answer: CommandAnswer<TestsResult> }) {
  const data = answer.document?.data
  if (!data) return <Problems answer={answer} />
  const { counts } = data
  const notPassed = data.tests.filter((t) => t.status !== 'pass')
  return (
    <div className="card" data-testid="build-tests-answer">
      <div className="card-body">
        <div data-testid="build-tests-counts" data-tone={counts.failed + counts.errored > 0 ? 'bad' : counts.warned > 0 ? 'warn' : 'ok'}>
          <strong>{counts.total === 0 ? 'This project has no tests.' : counts.failed + counts.errored === 0 ? 'Every test passed.' : 'Some tests did not pass.'}</strong>{' '}
          <span className="hint">{counts.total} run: {counts.passed} passed, {counts.failed} failed, {counts.warned} warned, {counts.errored} could not run</span>
        </div>
        {notPassed.map((t) => (
          <div key={t.name} style={{ marginTop: 10 }} data-testid={`build-test-${t.name}`}>
            <span className="mono">{t.name}</span> <span data-testid="build-test-status">{t.status}</span> <span className="hint">{t.kind}, {t.severity}, {t.violations} violation(s), {t.file}</span>
            {t.error && <div className="hint" style={wrap}>{t.error}</div>}
            {t.rows.length > 0 && (
              <div className="card flush" style={{ marginTop: 4, overflow: 'auto' }}>
                <div className="grid-head" style={{ gridTemplateColumns: `repeat(${Math.max(t.columns.length, 1)}, minmax(90px, 1fr))`, gap: 14 }}>{t.columns.map((c) => <span key={c}>{c}</span>)}</div>
                {t.rows.map((r, i) => <div key={i} className="grid-row" style={{ gridTemplateColumns: `repeat(${Math.max(t.columns.length, 1)}, minmax(90px, 1fr))`, gap: 14 }}>{t.columns.map((c) => <span key={c} className="mono">{String(r[c] ?? '∅')}</span>)}</div>)}
              </div>
            )}
          </div>
        ))}
      </div>
    </div>
  )
}

/** Running the project's tests from the Check tab (metadata rules and model tests; they read, so a viewer may). */
export function TestsPanel({ project }: { project: string }) {
  const run = useMutation({ mutationFn: () => buildApi.runTests(project, 5) })
  return (
    <div style={{ marginTop: 18 }} data-testid="build-tests">
      <div className="row" style={{ gap: 12, marginBottom: 14 }}>
        <button type="button" className="btn" disabled={run.isPending} onClick={() => run.mutate()} data-testid="build-tests-button">{run.isPending ? 'Running the tests…' : 'Run the project’s tests'}</button>
        <span className="hint">Runs the metadata rules and the model tests of the project (offline; no target is read). Shows the first violating rows of each test that did not pass.</span>
      </div>
      <ErrorBanner error={run.error} />
      {run.data && <TestsAnswerView answer={run.data} />}
    </div>
  )
}
