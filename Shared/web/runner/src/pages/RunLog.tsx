import { useEffect, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { runnerApi } from '../api'
import { appendLines, isProblem, isWarning, lastId, lineText, shown, type RunLogLine } from '../logs'

export interface RunLogViewProps {
  lines: RunLogLine[]
  loading: boolean
  error: string | null
  /** The run is still going: the log is read again every few seconds. */
  live: boolean
  /** A page came back full, so the run has more lines than are shown. */
  more: boolean
  onMore: () => void
  onCopy: () => void
}

/** A run's output as the runner recorded it, without the fetching, so it can be rendered and pressed in a test. */
export function RunLogView({ lines, loading, error, live, more, onMore, onCopy }: RunLogViewProps) {
  return (
    <div data-testid="runner-run-log-section">
      <div className="row" style={{ justifyContent: 'space-between', margin: '12px 0 4px' }}>
        <span className="card-title">Output{live ? ' (running, read again every few seconds)' : ''}</span>
        {lines.length > 0 && <button type="button" className="btn" onClick={onCopy} data-testid="runner-run-log-copy">Copy</button>}
      </div>
      {error && <div className="banner error" data-testid="runner-run-log-error"><span>{error}</span></div>}
      {loading && lines.length === 0 && <div className="hint" data-testid="runner-run-log-loading">Reading the output…</div>}
      {!loading && !error && lines.length === 0 && <div className="empty" data-testid="runner-run-log-none">The run wrote no output{live ? ' yet' : ''}.</div>}
      {lines.length > 0 && (
        <pre className="mono" style={{ maxHeight: 280, overflow: 'auto', margin: 0, padding: 8, whiteSpace: 'pre-wrap' }} data-testid="runner-run-log" aria-label="Run output">
          {lines.map((line) => (
            <div key={line.id} data-testid="runner-run-log-line" data-level={line.level.toLowerCase()} className={isProblem(line.level) ? 'field-error' : isWarning(line.level) ? 'hint' : undefined}>
              {line.item ? `[${line.item}] ` : ''}{shown(line.message)}
            </div>
          ))}
        </pre>
      )}
      {more && <button type="button" className="btn" onClick={onMore} data-testid="runner-run-log-more" style={{ marginTop: 6 }}>Older and later lines…</button>}
    </div>
  )
}

const PAGE = 200

/** A run's output from the runner, a page at a time after the last line seen; read again every few seconds while the run is going. */
export function RunLog({ runId, live }: { runId: string; live: boolean }) {
  const [lines, setLines] = useState<RunLogLine[]>([])
  const [full, setFull] = useState(false)
  const [asked, setAsked] = useState(0)
  const page = useQuery({
    // the key moves only when the person asks for more; each read starts after the last line seen (the function is the latest one when the query is read again)
    queryKey: ['runner', 'run-log', runId, asked],
    queryFn: () => runnerApi.runLog(runId, lastId(lines), PAGE),
    refetchInterval: live ? 3000 : false,
  })
  useEffect(() => {
    if (!page.data) return
    setLines((shownLines) => appendLines(shownLines, page.data.lines))
    setFull(page.data.lines.length >= PAGE)
  }, [page.data])
  const failure = page.error instanceof Error ? page.error.message : page.error ? 'The output could not be read.' : null
  return (
    <RunLogView lines={lines} loading={page.isLoading} error={failure} live={live} more={full} onMore={() => setAsked((n) => n + 1)}
      onCopy={() => void navigator.clipboard?.writeText(lines.map(lineText).join('\n'))} />
  )
}
