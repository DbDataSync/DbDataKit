import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ErrorBanner, useIsAdmin } from '@dbdatakit/shell'
import { runnerApi, type RunnerInstance, type TaskStatus } from '../api'
import { capabilityLines, connectionsOfTask, isBad, isGone, outcomeText, taskKey, taskState, type TaskState } from '../model'
import { missingLabels } from '../labels'
import { InstanceLabels } from './InstanceLabels'
import { TaskPanel } from './TaskPanel'
import { effectiveOwner, ownerLabel } from '../ownership'
import { RunnerShell } from '../RunnerShell'
import { when } from './format'

const stateText: Record<TaskState, string> = { running: 'Running', queued: 'Queued', paused: 'Paused', waiting: 'Waiting', problem: 'Problem', idle: 'Idle' }

/** The group's instances as one line each: gone ones too, marked, since a runner that left is something to see. */
function InstancesCard({ instances, canEdit }: { instances: RunnerInstance[]; canEdit: boolean }) {
  const [editing, setEditing] = useState<string | null>(null)
  return (
    <div className="card flush" data-testid="runner-instances">
      <div className="grid-head" style={{ gridTemplateColumns: '0.7fr 1.2fr 0.8fr 2fr 2fr 0.6fr 1.3fr', gap: 14 }}>
        <span>State</span><span>Host</span><span>Version</span><span>Labels</span><span>Capabilities</span><span>Running</span><span>Last heartbeat</span>
      </div>
      {instances.map((i) => (
        <div key={i.id} className="grid-row auto" style={{ gridTemplateColumns: '0.7fr 1.2fr 0.8fr 2fr 2fr 0.6fr 1.3fr', gap: 14 }} data-testid="runner-instance" data-state={isGone(i) ? 'gone' : 'live'} data-instance={i.id}>
          <span data-testid="runner-instance-state"><span className={`dot ${isGone(i) ? 'dot-bad' : 'dot-ok'}`} /> {isGone(i) ? 'Gone' : 'Live'}</span>
          <span className="mono" title={i.id}>{i.host}{i.this ? ' (answering)' : ''}</span>
          <span>{i.version}</span>
          <span className="row" style={{ gap: 6, flexWrap: 'wrap' }} data-testid="runner-instance-labels">
            {i.labels.map((l) => <span key={l.label} className="chip" data-source={l.source} title={l.source === 'admin' ? 'set by an administrator' : 'reported by the machine'}>{l.label}{l.source === 'admin' ? ' *' : ''}</span>)}
            <button type="button" className="btn btn-sm" aria-expanded={editing === i.id} onClick={() => setEditing(editing === i.id ? null : i.id)} data-testid="runner-labels-edit">{canEdit ? 'Edit labels' : 'Labels'}</button>
          </span>
          <span data-testid="runner-instance-capabilities">{capabilityLines(i.capabilities).join(', ')}</span>
          <span>{i.running}</span>
          <span>{when(i.heartbeat)}</span>
          {editing === i.id && <InstanceLabels key={i.id} instance={i} canEdit={canEdit} onClose={() => setEditing(null)} />}
        </div>
      ))}
    </div>
  )
}

function TaskRow({ task, canAct, busy, act, connections, missing, open, onToggle }: { task: TaskStatus; canAct: boolean; busy: boolean; missing: string[]; connections: { name: string; kind: string; role: string }[]; act: (what: 'run' | 'pause' | 'resume', task: TaskStatus) => void; open: boolean; onToggle: () => void }) {
  const state = taskState(task)
  const last = task.last_run
  return (
    <div className="grid-row auto" style={{ gridTemplateColumns: '1.3fr 0.6fr 1fr 0.6fr 1.3fr 1.6fr 0.8fr 1.6fr', gap: 14 }} data-testid="runner-task" data-task={taskKey(task)} data-state={state}>
      <span className="mono">{task.package}</span>
      <span>{task.channel}</span>
      <span className="mono">{task.task}</span>
      <span>{task.version ?? ''}</span>
      <span>{task.paused ? 'Paused' : when(task.next_due) || '-'}</span>
      <span data-testid="runner-task-last">
        {last ? <span className={isBad(last.outcome) ? 'field-error' : undefined}>{outcomeText(last.outcome)} ({when(last.started)})</span> : 'No run yet'}
      </span>
      <span data-testid="runner-task-state">{stateText[state]}</span>
      <span className="row" style={{ gap: 8 }}>
        {canAct && <>
          <button type="button" className="btn btn-sm" disabled={busy} onClick={() => act('run', task)} data-testid="runner-task-run">Run now</button>
          <button type="button" className="btn btn-sm" disabled={busy} onClick={() => act(task.paused ? 'resume' : 'pause', task)} data-testid="runner-task-toggle">{task.paused ? 'Resume' : 'Pause'}</button>
        </>}
        <button type="button" className="btn btn-sm" onClick={onToggle} aria-expanded={open} data-testid="runner-task-details">{open ? 'Hide' : 'Details'}</button>
      </span>
      <span style={{ gridColumn: '1 / -1' }} className="hint" data-testid="runner-task-owner" data-owner={effectiveOwner(task.owner)}>
        Scheduled by {ownerLabel(task.owner).toLowerCase()}{effectiveOwner(task.owner) !== 'runner' ? (effectiveOwner(task.owner) === 'observe' ? ': it records what it would queue and queues nothing.' : ': this runner does not queue it until it is moved.') : '.'}
      </span>
      {connections.length > 0 && (
        <span style={{ gridColumn: '1 / -1' }} className="hint" data-testid="runner-task-connections">
          Connects to {connections.map((c, i) => <span key={c.name} data-testid="runner-task-connection" data-connection={c.name}>{i > 0 ? ', ' : ''}<span className="mono">{c.name}</span> ({c.kind}, {c.role} login)</span>)}
        </span>
      )}
      {missing.length > 0 && (
        <span style={{ gridColumn: '1 / -1' }} className="hint" data-testid="runner-task-needs-label">
          Requires {missing.map((l, i) => <span key={l} className="mono" data-testid="runner-task-needs-label-name">{i > 0 ? ', ' : ''}{l}</span>)}: no live runner has {missing.length === 1 ? 'that label' : 'those labels'}. Add {missing.length === 1 ? 'it' : 'them'} to a runner in the list above (Edit labels).
        </span>
      )}
      {open && <TaskPanel task={task} canAct={canAct} />}
      {(task.waiting_reason || task.problem || task.paused_by) && (
        <span style={{ gridColumn: '1 / -1' }} className="hint" data-testid="runner-task-note">
          {task.waiting_reason && <span data-testid="runner-task-waiting">{task.waiting_reason} </span>}
          {task.problem && <span data-testid="runner-task-problem">{task.problem} </span>}
          {task.paused && task.paused_by && <span>Paused by {task.paused_by}.</span>}
        </span>
      )}
    </div>
  )
}

/**
 * The runner's first screen: the group's instances (what each is, what it enforces, whether it is live or gone) and the tasks of every served package, with run now, pause and resume for an operator. The runner decides who may act, from the
 * person's role in the token the console signs; a viewer is not offered the buttons.
 */
export function TasksPage() {
  const client = useQueryClient()
  const canAct = useIsAdmin()
  const status = useQuery({ queryKey: ['runner', 'status'], queryFn: runnerApi.status, refetchInterval: 5000 })
  const instances = useQuery({ queryKey: ['runner', 'instances'], queryFn: runnerApi.instances, refetchInterval: 5000 })
  // the connections each task uses (plan 28c E23); a runner too old to answer leaves the line out
  const connections = useQuery({ queryKey: ['runner', 'connections'], queryFn: runnerApi.connections, refetchInterval: 15000, retry: false })
  const act = useMutation({
    mutationFn: ({ what, task }: { what: 'run' | 'pause' | 'resume'; task: TaskStatus }) => (what === 'run' ? runnerApi.run(task) : runnerApi[what](task)),
    onSuccess: () => client.invalidateQueries({ queryKey: ['runner'] }),
  })
  const [openTask, setOpenTask] = useState<string | null>(null)
  const tasks = status.data?.tasks ?? []
  const problems = status.data?.problems ?? []

  return (
    <RunnerShell crumbs={[{ label: 'Tasks', heading: true }]}>
      <div className="pane" data-testid="runner-tasks-page">
        <div className="page-head">
          <h1 className="page-title">Runners</h1>
          <span className="page-note" data-testid="runner-group">{status.data ? `Group ${status.data.group}, version ${status.data.version}, state on ${status.data.state_engine}` : ''}</span>
        </div>
        <ErrorBanner error={status.error ?? instances.error ?? act.error} />
        {status.data?.console_runner?.production_warning && <div className="empty" role="alert" data-testid="runner-production-warning">{status.data.console_runner.production_warning}</div>}
        {problems.map((p) => <div key={`${p.subject}${p.at}`} className="empty" data-testid="runner-problem">{`${p.subject}: ${p.message}`}</div>)}
        <InstancesCard instances={instances.data ?? status.data?.instances ?? []} canEdit={canAct} />

        <div className="page-head" style={{ marginTop: 24 }}><h2 className="page-title">Tasks</h2></div>
        {status.isSuccess && tasks.length === 0 && <div className="empty" data-testid="runner-no-tasks">No package is served yet. Publish one to this runner.</div>}
        {tasks.length > 0 && (
          <div className="card flush" data-testid="runner-tasks">
            <div className="grid-head" style={{ gridTemplateColumns: '1.3fr 0.6fr 1fr 0.6fr 1.3fr 1.6fr 0.8fr 1.6fr', gap: 14 }}>
              <span>Package</span><span>Channel</span><span>Task</span><span>Version</span><span>Next due</span><span>Last run</span><span>State</span><span />
            </div>
            {tasks.map((t) => <TaskRow key={taskKey(t)} task={t} connections={connectionsOfTask(connections.data ?? [], t)} missing={missingLabels(t.required_labels ?? [], instances.data ?? status.data?.instances ?? [])} canAct={canAct} busy={act.isPending} act={(what, task) => act.mutate({ what, task })} open={openTask === taskKey(t)} onToggle={() => setOpenTask(openTask === taskKey(t) ? null : taskKey(t))} />)}
          </div>
        )}
      </div>
    </RunnerShell>
  )
}
