import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiError, ErrorBanner } from '@dbdatakit/shell'
import { runnerApi, type CheckpointChange, type TaskCheckpoint, type TaskStatus } from '../api'
import { describeChange, keyLabel, partsLine, rewindOptions } from '../checkpoints'
import { taskKey } from '../model'
import { when } from './format'

/** The item whose history is open: a checkpoint's name and the key it is kept under. */
export interface OpenHistory { name: string; key: string }

export interface CheckpointsViewProps {
  task: TaskStatus
  canAct: boolean
  checkpoints: TaskCheckpoint[] | undefined
  loading: boolean
  error: unknown
  open: OpenHistory | null
  history: CheckpointChange[] | undefined
  historyLoading: boolean
  busy: boolean
  /** The last rewind that went through, shown until the next try. */
  rewound: string | null
  failure: unknown
  onOpen: (open: OpenHistory | null) => void
  onRewind: (open: OpenHistory, historyId: number) => void
}

/**
 * Where a task got to in each outside source (plan 29a Q20): its checkpoints with the value of every part, the history of one, and for an operator the rewind to before one of its versions, which is a new version under their name and on the
 * audit. Hook-free so a test can draw it and press its buttons.
 */
export function CheckpointsView(p: CheckpointsViewProps) {
  const list = p.checkpoints ?? []
  const options = p.history ? rewindOptions(p.history) : []
  return (
    <div style={{ gridColumn: '1 / -1', whiteSpace: 'normal' }} className="card" data-testid="runner-checkpoints" data-task={taskKey(p.task)}>
      <div className="card-body">
        <h3 className="card-title" style={{ marginTop: 0 }}>Checkpoints</h3>
        <ErrorBanner error={p.error ?? p.failure} />
        {p.loading && <div className="empty" data-testid="runner-checkpoints-loading">Loading…</div>}
        {p.checkpoints && list.length === 0 && <div className="hint" data-testid="runner-checkpoints-none">This task keeps no checkpoint yet: its first successful run writes one.</div>}
        {list.map((checkpoint) => checkpoint.items.map((item) => {
          const isOpen = p.open?.name === checkpoint.name && p.open.key === item.key
          return (
            <div key={`${checkpoint.name}/${item.key}`} style={{ marginTop: 8 }} data-testid="runner-checkpoint" data-name={checkpoint.name} data-key={item.key}>
              <div className="row" style={{ gap: 10, flexWrap: 'wrap', alignItems: 'baseline' }}>
                <strong className="mono">{checkpoint.name}</strong>
                <span className="hint">{keyLabel(item.key)}</span>
                <span className="mono" data-testid="runner-checkpoint-value">{partsLine(item)}</span>
                <span className="hint">version {item.version}, {when(item.updated)}{item.updated_by ? ` by ${item.updated_by}` : ''}</span>
                <button type="button" className="btn btn-sm" aria-expanded={isOpen} onClick={() => p.onOpen(isOpen ? null : { name: checkpoint.name, key: item.key })} data-testid="runner-checkpoint-history">{isOpen ? 'Hide history' : 'History'}</button>
              </div>
              {isOpen && (
                <div style={{ marginTop: 6 }} data-testid="runner-checkpoint-changes">
                  {p.historyLoading && <div className="hint">Reading the history…</div>}
                  {p.history && p.history.length === 0 && <div className="hint">No change is recorded.</div>}
                  {(p.history ?? []).map((change) => <div key={change.id} className="hint" data-testid="runner-checkpoint-change" data-id={change.id}>{when(change.at)}: {describeChange(change)}</div>)}
                  {options.length > 0 && (
                    <div style={{ marginTop: 6 }} data-testid="runner-checkpoint-rewind">
                      <div className="hint">Going back writes a new version under your name and is audited. A run that is going now moves the checkpoint first: the rewind is then refused and nothing changes.</div>
                      {options.map((option) => (
                        <div key={option.id}>
                          <button type="button" className="btn btn-sm" style={{ marginTop: 4 }} disabled={!p.canAct || p.busy} onClick={() => p.onRewind({ name: checkpoint.name, key: item.key }, option.id)} data-testid="runner-checkpoint-rewind-to" data-id={option.id}>{option.label}</button>
                        </div>
                      ))}
                      {!p.canAct && <div className="hint" data-testid="runner-checkpoint-rewind-why">Rewinding a checkpoint needs the operator role on this runner.</div>}
                    </div>
                  )}
                </div>
              )}
            </div>
          )
        }))}
        {p.rewound && <div className="hint" style={{ marginTop: 6 }} data-testid="runner-checkpoint-rewound">{p.rewound}</div>}
      </div>
    </div>
  )
}

/** The checkpoints of one task, read from the runner while its details are open; the history is read when one is opened. */
export function CheckpointsCard({ task, canAct }: { task: TaskStatus; canAct: boolean }) {
  const client = useQueryClient()
  const key = { package: task.package, channel: task.channel, task: task.task }
  const [open, setOpen] = useState<OpenHistory | null>(null)
  const [rewound, setRewound] = useState<string | null>(null)
  const checkpoints = useQuery({ queryKey: ['runner', 'checkpoints', taskKey(task)], queryFn: () => runnerApi.checkpoints(key), refetchInterval: 10_000 })
  const history = useQuery({
    queryKey: ['runner', 'checkpoint-history', taskKey(task), open?.name, open?.key], queryFn: () => runnerApi.checkpointHistory(key, open!.name, open!.key), enabled: open !== null,
  })
  const rewind = useMutation({
    mutationFn: ({ target, id }: { target: OpenHistory; id: number }) => runnerApi.rewindCheckpoint(key, target.name, id, target.key),
    onSuccess: (answer, { target }) => { setRewound(`${target.name} (${keyLabel(target.key)}) is back, as version ${answer.version ?? '?'}.`); client.invalidateQueries({ queryKey: ['runner'] }) },
    onError: (error) => { if (error instanceof ApiError && error.status === 409) client.invalidateQueries({ queryKey: ['runner'] }) },
  })
  return (
    <CheckpointsView
      task={task} canAct={canAct} checkpoints={checkpoints.data} loading={checkpoints.isLoading} error={checkpoints.error} open={open} history={open ? history.data : undefined}
      historyLoading={open !== null && history.isLoading} busy={rewind.isPending} rewound={rewound} failure={rewind.error}
      onOpen={(next) => { setOpen(next); setRewound(null); rewind.reset() }} onRewind={(target, id) => { setRewound(null); rewind.mutate({ target, id }) }}
    />
  )
}
