import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ErrorBanner, useIsAdmin } from '@dbdatakit/shell'
import { runnerApi, type RunnerInstance, type TaskStatus } from '../api'
import { capabilityLines, isBad, isGone, outcomeText, taskKey, taskState, type TaskState } from '../model'
import { RunnerShell } from '../RunnerShell'
import { when } from './format'

const stateText: Record<TaskState, string> = { running: 'Running', queued: 'Queued', paused: 'Paused', waiting: 'Waiting', problem: 'Problem', idle: 'Idle' }

/** The group's instances as one line each: gone ones too, marked, since a runner that left is something to see. */
function InstancesCard({ instances }: { instances: RunnerInstance[] }) {
  return (
    <div className="card flush" data-testid="runner-instances">
      <div className="grid-head" style={{ gridTemplateColumns: '0.7fr 1.2fr 0.8fr 2fr 2fr 0.6fr 1.3fr', gap: 14 }}>
        <span>State</span><span>Host</span><span>Version</span><span>Labels</span><span>Capabilities</span><span>Running</span><span>Last heartbeat</span>
      </div>
      {instances.map((i) => (
        <div key={i.id} className="grid-row" style={{ gridTemplateColumns: '0.7fr 1.2fr 0.8fr 2fr 2fr 0.6fr 1.3fr', gap: 14 }} data-testid="runner-instance" data-state={isGone(i) ? 'gone' : 'live'} data-instance={i.id}>
          <span data-testid="runner-instance-state"><span className={`dot ${isGone(i) ? 'dot-bad' : 'dot-ok'}`} /> {isGone(i) ? 'Gone' : 'Live'}</span>
          <span className="mono" title={i.id}>{i.host}{i.this ? ' (answering)' : ''}</span>
          <span>{i.version}</span>
          <span className="row" style={{ gap: 6, flexWrap: 'wrap' }} data-testid="runner-instance-labels">
            {i.labels.map((l) => <span key={l.label} className="chip" data-source={l.source} title={l.source === 'admin' ? 'set by an administrator' : 'reported by the machine'}>{l.label}{l.source === 'admin' ? ' *' : ''}</span>)}
          </span>
          <span data-testid="runner-instance-capabilities">{capabilityLines(i.capabilities).join(', ')}</span>
          <span>{i.running}</span>
          <span>{when(i.heartbeat)}</span>
        </div>
      ))}
    </div>
  )
}

function TaskRow({ task, canAct, busy, act }: { task: TaskStatus; canAct: boolean; busy: boolean; act: (what: 'run' | 'pause' | 'resume', task: TaskStatus) => void }) {
  const state = taskState(task)
  const last = task.last_run
  return (
    <div className="grid-row" style={{ gridTemplateColumns: '1.3fr 0.6fr 1fr 0.6fr 1.3fr 1.6fr 0.8fr 1.6fr', gap: 14 }} data-testid="runner-task" data-task={taskKey(task)} data-state={state}>
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
      </span>
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
  const act = useMutation({
    mutationFn: ({ what, task }: { what: 'run' | 'pause' | 'resume'; task: TaskStatus }) => runnerApi[what](task),
    onSuccess: () => client.invalidateQueries({ queryKey: ['runner'] }),
  })
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
        {problems.map((p) => <div key={`${p.subject}${p.at}`} className="empty" data-testid="runner-problem">{`${p.subject}: ${p.message}`}</div>)}
        <InstancesCard instances={instances.data ?? status.data?.instances ?? []} />

        <div className="page-head" style={{ marginTop: 24 }}><h2 className="page-title">Tasks</h2></div>
        {status.isSuccess && tasks.length === 0 && <div className="empty" data-testid="runner-no-tasks">No package is served yet. Publish one to this runner.</div>}
        {tasks.length > 0 && (
          <div className="card flush" data-testid="runner-tasks">
            <div className="grid-head" style={{ gridTemplateColumns: '1.3fr 0.6fr 1fr 0.6fr 1.3fr 1.6fr 0.8fr 1.6fr', gap: 14 }}>
              <span>Package</span><span>Channel</span><span>Task</span><span>Version</span><span>Next due</span><span>Last run</span><span>State</span><span />
            </div>
            {tasks.map((t) => <TaskRow key={taskKey(t)} task={t} canAct={canAct} busy={act.isPending} act={(what, task) => act.mutate({ what, task })} />)}
          </div>
        )}
      </div>
    </RunnerShell>
  )
}
