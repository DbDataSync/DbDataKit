import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ErrorBanner } from '@dbdatakit/shell'
import { runnerApi, type OwnerBody, type TaskDecision, type TaskSchedule, type TaskStatus } from '../api'
import { decisionSummary, effectiveOwner, openOwnerForm, ownerBlock, ownerBody, ownerLabel, OWNERS, ownerText, parseInputs, type Owner, type OwnerForm } from '../ownership'
import { taskKey } from '../model'
import { when } from './format'

export interface TaskPanelViewProps {
  task: TaskStatus
  /** Whether the person is offered the controls: the runner's operator role, which the server checks again. */
  canAct: boolean
  schedule: TaskSchedule | undefined
  scheduleLoading: boolean
  decisions: TaskDecision[] | undefined
  /** Why the schedule could not be read, if it could not. */
  scheduleError: unknown
  form: OwnerForm
  inputsText: string
  busy: boolean
  /** The last move that went through, shown until the next try. */
  moved: string | null
  /** The last call that was refused or failed. */
  failure: unknown
  onForm: (form: OwnerForm) => void
  onInputs: (text: string) => void
  onMove: (body: OwnerBody) => void
  onRun: (inputs: Record<string, string>) => void
}

/** The task's details without their calls: who schedules it and since when, the decisions the runner would have made while observing, the move to another owner with its reason, and run now with inputs. Hook-free so a test can draw it and press its buttons. */
export function TaskPanelView(p: TaskPanelViewProps) {
  const current = effectiveOwner(p.schedule?.owner ?? p.task.owner)
  const block = ownerBlock(current, p.form)
  const body = ownerBody(current, p.form)
  const parsed = parseInputs(p.inputsText)
  const summary = p.decisions ? decisionSummary(p.decisions) : null
  const needsPosition = p.form.owner === 'runner' && current !== 'runner'
  const set = (change: Partial<OwnerForm>) => p.onForm({ ...p.form, ...change })
  return (
    <div style={{ gridColumn: '1 / -1', whiteSpace: 'normal' }} className="card" data-testid="runner-task-panel" data-task={taskKey(p.task)}>
      <div className="card-body">
        <ErrorBanner error={p.scheduleError ?? p.failure} />
        {p.scheduleLoading && <div className="empty" data-testid="runner-task-panel-loading">Loading…</div>}
        {p.schedule && (
          <div data-testid="runner-task-schedule">
            <div>Scheduled by <strong data-testid="runner-task-panel-owner" data-owner={current}>{ownerLabel(p.schedule.owner)}</strong>
              {p.schedule.owner_set_by ? <> since {when(p.schedule.owner_set_at)}, set by {p.schedule.owner_set_by}</> : <> (never moved: the default)</>}.</div>
            {p.schedule.reason && <div className="hint" data-testid="runner-task-reason">Reason: {p.schedule.reason}</div>}
            <div className="hint" data-testid="runner-task-position">
              {p.schedule.handled_up_to ? `Scheduled times up to ${when(p.schedule.handled_up_to)} are dealt with.` : 'No position yet: the first scheduled time has not been seen.'}
              {p.schedule.paused && ` Paused${p.schedule.paused_by ? ` by ${p.schedule.paused_by}` : ''}.`} {p.schedule.running} running, {p.schedule.queued} waiting.
            </div>
          </div>
        )}
        {current === 'observe' && (
          <div style={{ marginTop: 8 }} data-testid="runner-task-decisions">
            {p.decisions === undefined && <div className="hint">Reading the decisions…</div>}
            {summary && summary.runner + summary.old === 0 && <div className="hint" data-testid="runner-task-decisions-none">No decision has been recorded yet: the first scheduled time has not come.</div>}
            {summary && summary.runner + summary.old > 0 && (
              <div className="hint" data-testid="runner-task-decisions-summary" data-unmatched={summary.unmatched}>
                This runner would have queued {summary.runner} time(s), the publishing product queued {summary.old}; {summary.unmatched === 0 ? 'they agree.' : `${summary.unmatched} do not match (the Build server's schedule compare lists them).`}
              </div>
            )}
            {(p.decisions ?? []).slice(-5).map((d, i) => <div key={`${d.source}${d.at}${i}`} className="hint" data-testid="runner-task-decision" data-source={d.source}>{when(d.at)}: {d.source === 'runner' ? 'this runner' : 'the product'} {d.paused ? 'passed it over (paused)' : 'queued a run'}{d.missed > 0 ? `, ${d.missed} missed time(s) run once` : ''}</div>)}
          </div>
        )}

        <div style={{ marginTop: 14 }} data-testid="runner-task-move">
          <div className="hint" style={{ marginBottom: 6 }}>
            Moving a task changes who queues its runs. A task a Build server schedules is moved from the Build server&apos;s Monitoring row, which changes both sides in step; moving it to this runner here alone leaves the publishing product scheduling it too.
          </div>
          <div className="row" style={{ gap: 10, flexWrap: 'wrap' }}>
            <label className="row" style={{ gap: 6 }}>
              <span className="hint">Scheduled by</span>
              <select className="input" value={p.form.owner} onChange={(e) => set({ owner: e.target.value as Owner })} disabled={!p.canAct} data-testid="runner-task-owner-select">
                {OWNERS.map((o) => <option key={o} value={o}>{ownerText[o]}</option>)}
              </select>
            </label>
            <label className="row" style={{ gap: 6 }}>
              <span className="hint">Reason</span>
              <input className="input" value={p.form.reason} onChange={(e) => set({ reason: e.target.value })} disabled={!p.canAct} data-testid="runner-task-owner-reason" style={{ width: 220 }} />
            </label>
            {needsPosition && (
              <label className="row" style={{ gap: 6 }}>
                <span className="hint">Handled up to (UTC)</span>
                <input className="input mono" value={p.form.handledUpTo} placeholder="2026-11-08T12:00:00Z" onChange={(e) => set({ handledUpTo: e.target.value })} disabled={!p.canAct} data-testid="runner-task-owner-position" style={{ width: 200 }} />
              </label>
            )}
            <button type="button" className="btn" disabled={!p.canAct || body === null || p.busy} onClick={() => body && p.onMove(body)} data-testid="runner-task-owner-move">{p.busy ? 'Moving…' : 'Move'}</button>
          </div>
          {!p.canAct && <div className="hint" style={{ marginTop: 4 }} data-testid="runner-task-owner-why">Moving a task needs the operator role on this runner.</div>}
          {p.canAct && block !== null && <div className="hint" style={{ marginTop: 4 }} data-testid="runner-task-owner-why">{block}</div>}
          {p.moved && <div className="hint" style={{ marginTop: 4 }} data-testid="runner-task-owner-moved">{p.moved}</div>}
        </div>

        <div style={{ marginTop: 14 }} data-testid="runner-task-run-inputs">
          <label style={{ display: 'block' }}>
            <span className="hint">Run now with inputs, one <span className="mono">name=value</span> per line (empty: the task&apos;s defaults)</span>
            <textarea className="input mono" rows={2} value={p.inputsText} onChange={(e) => p.onInputs(e.target.value)} disabled={!p.canAct} data-testid="runner-task-inputs" style={{ width: '100%', marginTop: 4 }} />
          </label>
          <div className="row" style={{ gap: 10, marginTop: 6 }}>
            <button type="button" className="btn" disabled={!p.canAct || parsed.problem !== null || p.busy} onClick={() => p.onRun(parsed.inputs)} data-testid="runner-task-run-with-inputs">Run now with these inputs</button>
            {parsed.problem && <span className="hint" data-testid="runner-task-inputs-why">{parsed.problem}</span>}
            {!p.canAct && <span className="hint" data-testid="runner-task-inputs-why">Running a task needs the operator role on this runner.</span>}
          </div>
        </div>
      </div>
    </div>
  )
}

/** The details of one task, open under its row (plan 33, UE06): the schedule and decisions are read from the runner while it is open. */
export function TaskPanel({ task, canAct }: { task: TaskStatus; canAct: boolean }) {
  const client = useQueryClient()
  const key = { package: task.package, channel: task.channel, task: task.task }
  const schedule = useQuery({ queryKey: ['runner', 'schedule', taskKey(task)], queryFn: () => runnerApi.schedule(key), refetchInterval: 5000 })
  const owner = effectiveOwner(schedule.data?.owner ?? task.owner)
  const decisions = useQuery({ queryKey: ['runner', 'decisions', taskKey(task)], queryFn: () => runnerApi.decisions(key), enabled: owner === 'observe', refetchInterval: 10_000 })
  const [form, setForm] = useState<OwnerForm | null>(null)
  const [inputs, setInputs] = useState('')
  const [moved, setMoved] = useState<string | null>(null)
  const move = useMutation({
    mutationFn: (body: OwnerBody) => runnerApi.setOwner(key, body),
    onSuccess: (answer) => { setMoved(`Now scheduled by ${ownerLabel(answer.owner).toLowerCase()}.`); setForm(null); client.invalidateQueries({ queryKey: ['runner'] }) },
  })
  const run = useMutation({
    mutationFn: (values: Record<string, string>) => runnerApi.run(key, values),
    onSuccess: (answer) => { setMoved(`Run ${answer.run_id} queued.`); client.invalidateQueries({ queryKey: ['runner'] }) },
  })
  return (
    <TaskPanelView
      task={task} canAct={canAct} schedule={schedule.data} scheduleLoading={schedule.isLoading} scheduleError={schedule.error} decisions={owner === 'observe' ? decisions.data : undefined}
      form={form ?? openOwnerForm(owner, schedule.data)} inputsText={inputs} busy={move.isPending || run.isPending} moved={moved} failure={move.error ?? run.error}
      onForm={(next) => { setForm(next); setMoved(null); move.reset() }} onInputs={(text) => { setInputs(text); run.reset() }}
      onMove={(body) => { setMoved(null); move.mutate(body) }} onRun={(values) => { setMoved(null); run.mutate(values) }}
    />
  )
}
