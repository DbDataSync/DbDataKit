import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiError, ErrorBanner } from '@dbdatakit/shell'
import { runnerApi, type DeadLetterRecord, type DeadLetterStatus, type TaskStatus } from '../api'
import { taskKey } from '../model'
import { deadLetterReplayHint, deadLetterText } from '../operator'
import { when } from './format'

export interface DeadLettersViewProps {
  task: TaskStatus
  canAct: boolean
  status: DeadLetterStatus
  letters: DeadLetterRecord[] | undefined
  loading: boolean
  error: unknown
  busy: boolean
  /** What the last replay or discard did, shown until the next try. */
  done: string | null
  failure: unknown
  onStatus: (status: DeadLetterStatus) => void
  onReplay: (letter: DeadLetterRecord) => void
  onDiscard: (letter: DeadLetterRecord) => void
}

const STATUSES: DeadLetterStatus[] = ['open', 'replayed', 'discarded']

/**
 * The records an outside system refused (plan 28a R25): the open ones with why and how often, to send again (they go back to the task's inbox once and a run takes them) or to give up; the replayed and discarded ones as the record of what was
 * done. Hook-free so a test can draw it and press its buttons.
 */
export function DeadLettersView(p: DeadLettersViewProps) {
  const letters = p.letters ?? []
  return (
    <div style={{ gridColumn: '1 / -1', whiteSpace: 'normal' }} className="card" data-testid="runner-dead-letters" data-task={taskKey(p.task)}>
      <div className="card-body">
        <div className="row" style={{ gap: 10, alignItems: 'baseline' }}>
          <h3 className="card-title" style={{ marginTop: 0 }}>Dead letters</h3>
          {STATUSES.map((s) => <button key={s} type="button" className={s === p.status ? 'btn btn-sm btn-primary' : 'btn btn-sm'} aria-pressed={s === p.status} onClick={() => p.onStatus(s)} data-testid="runner-dead-letter-status" data-status={s}>{s}</button>)}
        </div>
        <ErrorBanner error={p.error ?? p.failure} />
        {p.loading && <div className="empty" data-testid="runner-dead-letters-loading">Loading…</div>}
        {p.letters && letters.length === 0 && <div className="hint" data-testid="runner-dead-letters-none">No {p.status} dead letter.</div>}
        {letters.map((letter) => (
          <div key={letter.id} style={{ marginTop: 8 }} data-testid="runner-dead-letter" data-id={letter.id}>
            <div className="row" style={{ gap: 10, flexWrap: 'wrap', alignItems: 'baseline' }}>
              <span className="mono" data-testid="runner-dead-letter-text">{deadLetterText(letter)}</span>
              <span className="hint">refused {letter.attempts} time{letter.attempts === 1 ? '' : 's'}, last {when(letter.last)}{letter.resolved ? `, ${letter.status} ${when(letter.resolved)}` : ''}</span>
              {letter.status === 'open' && (
                <>
                  <button type="button" className="btn btn-sm" disabled={!p.canAct || p.busy || !letter.has_body} title={deadLetterReplayHint(letter)} onClick={() => p.onReplay(letter)} data-testid="runner-dead-letter-replay">Replay</button>
                  <button type="button" className="btn btn-sm" disabled={!p.canAct || p.busy} onClick={() => p.onDiscard(letter)} data-testid="runner-dead-letter-discard">Discard</button>
                </>
              )}
            </div>
            {letter.status === 'open' && !letter.has_body && <div className="hint" data-testid="runner-dead-letter-nobody">{deadLetterReplayHint(letter)}</div>}
          </div>
        ))}
        {!p.canAct && <div className="hint" style={{ marginTop: 6 }} data-testid="runner-dead-letters-role">Replaying or discarding a dead letter needs the operator role on this runner.</div>}
        {p.done && <div className="hint" style={{ marginTop: 6 }} data-testid="runner-dead-letter-done">{p.done}</div>}
      </div>
    </div>
  )
}

/** The dead letters of one task, read from the runner while its details are open. */
export function DeadLettersCard({ task, canAct }: { task: TaskStatus; canAct: boolean }) {
  const client = useQueryClient()
  const key = { package: task.package, channel: task.channel, task: task.task }
  const [status, setStatus] = useState<DeadLetterStatus>('open')
  const [done, setDone] = useState<string | null>(null)
  const letters = useQuery({ queryKey: ['runner', 'dead-letters', taskKey(task), status], queryFn: () => runnerApi.deadLetters(key, status), refetchInterval: 10_000, enabled: canAct })
  const act = useMutation({
    mutationFn: ({ letter, replay }: { letter: DeadLetterRecord; replay: boolean }): Promise<unknown> => (replay ? runnerApi.replayDeadLetter(key, letter.id) : runnerApi.discardDeadLetter(key, letter.id)),
    onSuccess: (_, { replay }) => { setDone(replay ? 'Replayed: it is in the task\'s inbox and a run takes it.' : 'Discarded.'); client.invalidateQueries({ queryKey: ['runner'] }) },
    onError: (error) => { if (error instanceof ApiError && error.status === 409) client.invalidateQueries({ queryKey: ['runner'] }) },
  })
  return (
    <DeadLettersView
      task={task} canAct={canAct} status={status} letters={canAct ? letters.data : []} loading={canAct && letters.isLoading} error={letters.error} busy={act.isPending} done={done} failure={act.error}
      onStatus={(next) => { setStatus(next); setDone(null); act.reset() }} onReplay={(letter) => { setDone(null); act.mutate({ letter, replay: true }) }} onDiscard={(letter) => { setDone(null); act.mutate({ letter, replay: false }) }}
    />
  )
}
