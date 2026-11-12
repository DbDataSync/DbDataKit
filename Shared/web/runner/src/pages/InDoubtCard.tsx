import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiError, ErrorBanner } from '@dbdatakit/shell'
import { runnerApi, type InDoubtStep, type TaskStatus } from '../api'
import { taskKey } from '../model'
import { emptyStepForm, parseStepResult, stepBlock, stepLabel, type StepForm } from '../operator'
import { when } from './format'

export interface InDoubtViewProps {
  task: TaskStatus
  canAct: boolean
  steps: InDoubtStep[] | undefined
  loading: boolean
  error: unknown
  /** The step whose answer is being written, and the form. */
  open: InDoubtStep | null
  form: StepForm
  busy: boolean
  /** What the last answer did, shown until the next try. */
  answered: string | null
  failure: unknown
  onOpen: (step: InDoubtStep | null) => void
  onForm: (form: StepForm) => void
  onSend: (step: InDoubtStep, form: StepForm) => void
}

const sameStep = (a: InDoubtStep | null, b: InDoubtStep) => a !== null && a.chain === b.chain && a.name === b.name && a.key === b.key

/**
 * The steps an earlier attempt began and never finished (plan 28a R25): a task that sends or charges cannot know whether the work happened, so the run stops and a person says. They answer "it happened" with the
 * result (so the next attempt reuses it) or "it did not" (so it runs the step again), and may queue the run to go on. Hook-free so a test can draw it and press its buttons.
 */
export function InDoubtView(p: InDoubtViewProps) {
  const steps = p.steps ?? []
  return (
    <div style={{ gridColumn: '1 / -1', whiteSpace: 'normal' }} className="card" data-testid="runner-in-doubt" data-task={taskKey(p.task)}>
      <div className="card-body">
        <h3 className="card-title" style={{ marginTop: 0 }}>Steps in doubt</h3>
        <ErrorBanner error={p.error ?? p.failure} />
        {p.loading && <div className="empty" data-testid="runner-in-doubt-loading">Loading…</div>}
        {p.steps && steps.length === 0 && <div className="hint" data-testid="runner-in-doubt-none">No step of this task waits for an answer.</div>}
        {steps.map((step) => {
          const isOpen = sameStep(p.open, step)
          const block = isOpen ? stepBlock(p.form) : null
          return (
            <div key={`${step.chain}/${step.name}/${step.key}`} style={{ marginTop: 8 }} data-testid="runner-in-doubt-step" data-name={step.name} data-key={step.key}>
              <div className="row" style={{ gap: 10, flexWrap: 'wrap', alignItems: 'baseline' }}>
                <strong className="mono">{stepLabel(step)}</strong>
                <span className="hint">began in attempt {step.attempt}, {when(step.started)}, and no attempt said how it ended</span>
                <button type="button" className="btn btn-sm" aria-expanded={isOpen} disabled={!p.canAct} onClick={() => p.onOpen(isOpen ? null : step)} data-testid="runner-in-doubt-answer">{isOpen ? 'Close' : 'Answer'}</button>
              </div>
              {isOpen && (
                <div style={{ marginTop: 6 }} data-testid="runner-in-doubt-form">
                  <div className="hint">Check the outside system first. The answer is on the audit under your name; a wrong answer sends twice or never.</div>
                  <label className="row" style={{ gap: 6, marginTop: 4 }}>
                    <input type="radio" name={`happened-${step.name}`} checked={!p.form.happened} onChange={() => p.onForm({ ...p.form, happened: false })} data-testid="runner-in-doubt-not-happened" />
                    <span>It did not happen: run the step again</span>
                  </label>
                  <label className="row" style={{ gap: 6 }}>
                    <input type="radio" name={`happened-${step.name}`} checked={p.form.happened} onChange={() => p.onForm({ ...p.form, happened: true })} data-testid="runner-in-doubt-happened" />
                    <span>It happened: use this result, do not run it again</span>
                  </label>
                  {p.form.happened && (
                    <label style={{ display: 'block', marginTop: 4 }}>
                      <span className="hint">Result as JSON (empty: no value)</span>
                      <textarea className="input mono" rows={2} value={p.form.result} onChange={(e) => p.onForm({ ...p.form, result: e.target.value })} data-testid="runner-in-doubt-result" style={{ width: '100%', marginTop: 4 }} />
                    </label>
                  )}
                  <label className="row" style={{ gap: 6, marginTop: 4 }}>
                    <input type="checkbox" checked={p.form.retry} onChange={(e) => p.onForm({ ...p.form, retry: e.target.checked })} data-testid="runner-in-doubt-retry" />
                    <span>Queue a run of the task to go on</span>
                  </label>
                  <div className="row" style={{ gap: 10, marginTop: 6 }}>
                    <button type="button" className="btn" disabled={!p.canAct || block !== null || p.busy} onClick={() => p.onSend(step, p.form)} data-testid="runner-in-doubt-send">{p.busy ? 'Sending…' : 'Record the answer'}</button>
                    {block && <span className="hint" data-testid="runner-in-doubt-why">{block}</span>}
                  </div>
                </div>
              )}
            </div>
          )
        })}
        {!p.canAct && steps.length > 0 && <div className="hint" style={{ marginTop: 6 }} data-testid="runner-in-doubt-role">Answering a step needs the operator role on this runner.</div>}
        {p.answered && <div className="hint" style={{ marginTop: 6 }} data-testid="runner-in-doubt-answered">{p.answered}</div>}
      </div>
    </div>
  )
}

/** The in-doubt steps of one task, read from the runner while its details are open. */
export function InDoubtCard({ task, canAct }: { task: TaskStatus; canAct: boolean }) {
  const client = useQueryClient()
  const key = { package: task.package, channel: task.channel, task: task.task }
  const [open, setOpen] = useState<InDoubtStep | null>(null)
  const [form, setForm] = useState<StepForm>(emptyStepForm)
  const [answered, setAnswered] = useState<string | null>(null)
  const steps = useQuery({ queryKey: ['runner', 'in-doubt', taskKey(task)], queryFn: () => runnerApi.inDoubt(key), refetchInterval: 10_000, enabled: canAct })
  const send = useMutation({
    mutationFn: ({ step, answer }: { step: InDoubtStep; answer: StepForm }) => {
      const parsed = answer.happened ? parseStepResult(answer.result) : { ok: true as const, value: undefined }
      if (!parsed.ok) return Promise.reject(new Error(parsed.problem))
      return runnerApi.resolveStep(key, step, { happened: answer.happened, result: parsed.value, retry: answer.retry })
    },
    onSuccess: (answer, { step }) => {
      setAnswered(`${stepLabel(step)}: recorded as ${answer.happened ? 'happened' : 'not happened'}${answer.retry_queued ? `; run ${answer.run_id} queued` : ''}.`)
      setOpen(null)
      client.invalidateQueries({ queryKey: ['runner'] })
    },
    onError: (error) => { if (error instanceof ApiError && error.status === 409) client.invalidateQueries({ queryKey: ['runner'] }) },
  })
  return (
    <InDoubtView
      task={task} canAct={canAct} steps={canAct ? steps.data : []} loading={canAct && steps.isLoading} error={steps.error} open={open} form={form} busy={send.isPending} answered={answered} failure={send.error}
      onOpen={(step) => { setOpen(step); setForm(emptyStepForm); setAnswered(null); send.reset() }} onForm={setForm} onSend={(step, answer) => { setAnswered(null); send.mutate({ step, answer }) }}
    />
  )
}
