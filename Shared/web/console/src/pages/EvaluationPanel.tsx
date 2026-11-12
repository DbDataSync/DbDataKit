import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ErrorBanner, useIsAdmin } from '@dbdatakit/shell'
import { checkLine, evaluationApi, moneyWords, parseMinimum, promotionLine, runLine, scoreWords, scoresOverTime } from '../evaluation'

/**
 * The evaluation set of an agent (plan 27a AG24): its cases, a run started by hand, the score per case and over time, the judgement shown as a judgement, what a run cost and what the agent was given, and the minimum score before a promotion as a
 * recorded choice with the refusal a promotion would be given. A run is a record: it never gates a pull request.
 */
export function EvaluationPanel({ name }: { name: string }) {
  const client = useQueryClient()
  const isAdmin = useIsAdmin()
  const [open, setOpen] = useState<string | null>(null)
  const [minimum, setMinimum] = useState('')
  const state = useQuery({ queryKey: ['agents', 'evaluation', name], queryFn: () => evaluationApi.state(name), refetchInterval: (q) => (q.state.data?.running ? 1500 : false) })
  const detail = useQuery({ queryKey: ['agents', 'evaluation', name, 'run', open], queryFn: () => evaluationApi.detail(name, open!), enabled: open !== null })
  const run = useMutation({ mutationFn: () => evaluationApi.run(name), onSuccess: async () => { await client.invalidateQueries({ queryKey: ['agents', 'evaluation', name] }) } })
  const setMin = useMutation({
    mutationFn: (value: number | null) => evaluationApi.setMinimum(name, value),
    onSuccess: async () => { await client.invalidateQueries({ queryKey: ['agents', 'evaluation', name] }) },
  })
  const s = state.data
  const parsed = parseMinimum(minimum)
  return (
    <div className="card" style={{ marginTop: 16 }}><div className="card-body" data-testid="evaluation">
      <h3>Evaluation</h3>
      <ErrorBanner error={state.error ?? run.error ?? setMin.error} />
      {s && (
        <>
          <div className="hint">The cases are the files in <span className="mono">agents/{name}/tests/evaluation/</span>. A run asks the agent each task on the model bound to its slot, checks what the suite can decide, and asks the judge model only about a written rubric. It is a record, never a gate of a pull request.</div>
          {s.cases.length === 0 && <div className="empty" data-testid="evaluation-no-cases">No case yet.</div>}
          {s.diagnostics.map((d, i) => <div key={i} className="banner error" data-testid="evaluation-diagnostic"><span className="mono">{d.code}</span> {d.file} line {d.line}: {d.message}{d.fix ? ` ${d.fix}` : ''}</div>)}
          <ul data-testid="evaluation-cases">{s.cases.map((c) => <li key={c.name} data-testid="evaluation-case" data-case={c.name}><strong>{c.name}</strong>: {c.task} <span className="hint">({c.checks.length} check{c.checks.length === 1 ? '' : 's'}{c.hasRubric ? ', one judged' : ''}; passes at {scoreWords(c.passAt)})</span></li>)}</ul>
          {!s.judgeBound && s.cases.some((c) => c.hasRubric) && <div className="hint" data-testid="evaluation-no-judge">No judge model is bound to the slot <span className="mono">judge</span>: a rubric is not judged and is left out of the score.</div>}
          {isAdmin && (
            <div style={{ margin: '8px 0' }}>
              <button type="button" className="btn btn-sm btn-primary" disabled={s.cases.length === 0 || s.running !== null || run.isPending} onClick={() => run.mutate()} data-testid="evaluation-run">Run the set</button>{' '}
              {s.running && <span className="hint" data-testid="evaluation-running">Running: {s.running.done} of {s.running.total} cases.</span>}
            </div>
          )}

          <h4>Runs</h4>
          {s.runs.length === 0 && <div className="hint" data-testid="evaluation-no-runs">Not run yet.</div>}
          {s.runs.map((r) => (
            <div key={r.id} style={{ margin: '4px 0' }} data-testid="evaluation-run-row" data-score={r.score}>
              <button type="button" className={`btn btn-sm${open === r.id ? ' btn-primary' : ''}`} onClick={() => setOpen(r.id)} data-testid="evaluation-run-open">{scoreWords(r.score)}</button>{' '}
              <span data-testid="evaluation-run-line">{runLine(r)}</span>
              <div className="hint">{new Date(r.startedAt).toLocaleString()}{r.knowledgeVersions && r.knowledgeVersions.length > 0 ? `. It was given: ${r.knowledgeVersions.join(', ')}.` : ''}</div>
            </div>
          ))}
          {s.runs.length > 1 && <div className="hint" data-testid="evaluation-over-time">Over time: {scoresOverTime(s.runs)}</div>}
          {detail.data && (
            <div data-testid="evaluation-detail" data-run={detail.data.run.id}>
              {detail.data.cases.map((c) => (
                <div key={c.name} className="card" style={{ margin: '8px 0' }} data-testid="evaluation-case-result" data-case={c.name} data-passed={c.passed}><div className="card-body">
                  <strong>{c.name}</strong> <span data-testid="evaluation-case-score">{scoreWords(c.score)}</span> {c.passed ? 'passed' : 'did not pass'} <span className="hint">({c.inputTokens + c.outputTokens} tokens, {moneyWords(c.cost)})</span>
                  {c.problem && <div className="banner error" data-testid="evaluation-case-problem">{c.problem}</div>}
                  {c.checks.map((k, i) => <div key={i} data-testid="evaluation-check" data-method={k.method} data-decided={k.score !== null}><span className="mono">{k.detail}</span> — {checkLine(k)}</div>)}
                  {c.answer && <details><summary>The answer</summary><pre style={{ whiteSpace: 'pre-wrap' }}>{c.answer}</pre></details>}
                </div></div>
              ))}
            </div>
          )}

          <h4>Before a promotion</h4>
          <div data-testid="evaluation-promotion" data-refused={s.promotion.refusal !== null}>{promotionLine(s)}</div>
          {s.minimum && <div className="hint" data-testid="evaluation-minimum">Minimum {scoreWords(s.minimum.minimum)} for {s.channel}, set by {s.minimum.setBy} on {new Date(s.minimum.setAt).toLocaleDateString()}.</div>}
          {isAdmin && (
            <div style={{ marginTop: 6 }}>
              <input value={minimum} onChange={(e) => setMinimum(e.target.value)} placeholder="minimum score, 0 to 100 (blank for none)" aria-label="Minimum score" data-testid="evaluation-minimum-input" style={{ width: 280 }} />{' '}
              <button type="button" className="btn btn-sm" disabled={parsed === null || setMin.isPending} onClick={() => parsed && setMin.mutate(parsed.value)} data-testid="evaluation-minimum-set">Record the minimum</button>
            </div>
          )}
        </>
      )}
    </div></div>
  )
}
