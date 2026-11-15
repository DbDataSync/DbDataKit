/**
 * Run now while a run of the replication is already going starts nothing new (owner decision 2026-11-14): the screen shows the run that is going, and says so, with when it began.
 */
export function JoinedRunNotice({ since }: { since: string | null | undefined }) {
  if (!since) return null
  return (
    <div className="banner" role="status" data-testid="live-run-joined">
      Already running since {new Date(since).toLocaleString()}: nothing new was started, this is the run in progress.
    </div>
  )
}
