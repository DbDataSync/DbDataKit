import type { TaskRunRecord } from '../../api/types'

/**
 * The rows a run read, as its row in the history shows them (owner decision 2026-11-14). A drain on the runner reads in passes and its count is the sum of the passes that have finished, so while it runs the count
 * is still growing: the cell says so, and since when the run is going. Once the run ends the count is final and shown alone.
 */
export function RowsReadCell({ run }: { run: Pick<TaskRunRecord, 'runId' | 'status' | 'rowsRead' | 'startedAtUtc'> }) {
  if (run.status !== 'Running') return <span data-testid={`run-rows-read-${run.runId}`}>{run.rowsRead.toLocaleString()}</span>
  return (
    <span data-testid={`run-rows-read-${run.runId}`} title="The sum of the passes that have finished; it grows until the run ends">
      {run.rowsRead.toLocaleString()}{' '}
      <span className="faint" data-testid={`run-rows-read-running-${run.runId}`}>
        so far · running{run.startedAtUtc ? ` since ${new Date(run.startedAtUtc).toLocaleTimeString()}` : ''}
      </span>
    </span>
  )
}
