/**
 * The run history of a replication the runner owns (plan 28c E13) when the runner could not be asked: what is listed is this server's own history, and the runs the runner ran are missing for the reason given.
 * Said above the list so that a history that lacks them is never mistaken for one that is complete; nothing when the runner answered (or does not own the replication).
 */
export function RunnerHistoryNotice({ unavailable }: { unavailable: string | null | undefined }) {
  if (!unavailable) return null
  return (
    <div className="banner warn" role="alert" data-testid="runner-history-unavailable">
      The runner could not be asked for the runs it ran, so this history is incomplete: {unavailable}
    </div>
  )
}
