// What a kind's Designer reads through the frame rather than asking a server itself: the draft's check, where the draft stands against what a
// deploy last put on a Runner, and a Sync replication's provisioning plan with its apply. The calls are the frame's (the Workspace server through
// the console's relay, and the console's own deploy and run calls), so a Designer package needs no client of its own for them.
import { useMutation, useQuery } from '@tanstack/react-query'
import { ApiError } from '@dbdatakit/shell'
import { workspaceApi, type Commit, type StagedRun } from './api'
import { deployApi, useRunnerVersion, type RunnerVersion } from './deploys'
import { findingsKey } from './draft'

/** The draft's check, the same query the chrome's count and the Check tab read (so one answer serves all three). */
export function useDraftCheck(project: string, id: string) {
  return useQuery({ queryKey: findingsKey(project, id), queryFn: () => workspaceApi.check(project, id), staleTime: 30_000, retry: false })
}

/** The error findings of a check about files under a folder (`config/replications/orders/`), however many lines each names. */
export function errorsUnder(diagnostics: readonly { severity: string; file: string | null }[] | undefined, folder: string): number {
  return (diagnostics ?? []).filter((d) => d.severity === 'error' && d.file !== null && d.file.replace(/\\/g, '/').startsWith(folder)).length
}

/** Where a draft stands against the newest deploy of one of its builds to a Runner. */
export interface DraftPublication {
  /** What that deploy put on a Runner; null when no build of the draft was deployed to one. */
  version: RunnerVersion | null
  /** The commit the deployed build was made from; null when the build does not say (it was made from files not committed) or is not known yet. */
  buildCommit: string | null
  /** The draft's commits after that commit; null when the commit is not known or not in the draft's history. */
  commitsSince: number | null
}

const sameCommit = (a: string, b: string) => a.length > 0 && b.length > 0 && (a.startsWith(b) || b.startsWith(a))

/** How many commits of a history (newest first) come after `commit`; null when it is not in the history. */
export function commitsAfter(history: readonly Commit[], commit: string | null): number | null {
  if (!commit) return null
  const at = history.findIndex((c) => sameCommit(c.sha, commit))
  return at < 0 ? null : at
}

/**
 * The newest deploy of a build of this draft to a Runner (the console's record), the commit that build was made from and the draft's history, put
 * together: how many commits the draft has after the one that was deployed. Nothing is asked of a Runner.
 */
export function useDraftPublication(project: string, id: string): { data: DraftPublication | undefined; isLoading: boolean; error: unknown } {
  const version = useRunnerVersion(project, id)
  const build = version.data?.build
  const detail = useQuery({ queryKey: ['workspace', 'build', project, id, build], queryFn: () => workspaceApi.build(project, id, build!), enabled: !!build, retry: false })
  const history = useQuery({ queryKey: ['workspace', 'history', project, id], queryFn: () => workspaceApi.history(project, id), enabled: !!build, retry: false })
  if (version.isLoading) return { data: undefined, isLoading: true, error: null }
  if (version.error) return { data: undefined, isLoading: false, error: version.error }
  if (!version.data) return { data: { version: null, buildCommit: null, commitsSince: null }, isLoading: false, error: null }
  const buildCommit = detail.data?.build.commit ?? detail.data?.result?.commit ?? null
  return {
    data: { version: version.data, buildCommit, commitsSince: history.data ? commitsAfter(history.data, buildCommit) : null },
    isLoading: detail.isLoading || history.isLoading,
    error: detail.error ?? history.error,
  }
}

/** The step the Sync task `provision` records its report as. */
export const AppliedStep = 'provision-applied'

/**
 * Reads a run back until it ended (each `intervalMs`, at most `attempts` times); the last read when it has not ended by then. A run the Runner has queued but not started is not on its record yet: a read answered 404 is tried
 * again until the run is seen, `queuedAttempts` times at most.
 */
export async function followStagedRun(read: () => Promise<StagedRun>, intervalMs = 1000, attempts = 600, queuedAttempts = 60): Promise<StagedRun> {
  let run: StagedRun | null = null
  for (let i = 0; run === null; i++) {
    try { run = await read() }
    catch (e) {
      if (!(e instanceof ApiError && e.status === 404) || i + 1 >= queuedAttempts) throw e
      await new Promise((r) => setTimeout(r, intervalMs))
    }
  }
  for (let i = 1; i < attempts && !run.finished; i++) {
    await new Promise((r) => setTimeout(r, intervalMs))
    run = await read()
  }
  return run
}

/** The query key of a replication's provisioning plan; a save of the draft asks it again. */
export const provisioningPlanKey = (project: string, id: string, replication?: string) =>
  ['workspace', 'provisioning-plan', project, id, ...(replication === undefined ? [] : [replication])]

/** The unsaved edits a plan is asked with, and a digest of them that names the answer. */
export interface PlanEdits { unsaved: Record<string, string | null>; digest: string }

/**
 * A replication's provisioning plan, made by the Workspace offline from the draft's files and catalog cache (no connection is opened), with the
 * unsaved edits given over them. While the plan of new edits is made, the plan of the previous ones stays shown; a plan no longer wanted is cancelled.
 */
export function useProvisioningPlan(project: string, id: string, replication: string, edits: PlanEdits = { unsaved: {}, digest: '' }) {
  const of = provisioningPlanKey(project, id, replication)
  return useQuery({
    queryKey: [...of, edits.digest],
    queryFn: ({ signal }) => workspaceApi.provisioningPlan(project, id, replication, edits.unsaved, signal),
    // the plan of the same replication with other edits, never another replication's
    placeholderData: (previous, query) => (query && of.every((part, i) => query.queryKey[i] === part) ? previous : undefined),
    retry: false,
  })
}

/** What applying one mapping's side asks: the replication, the mapping and the side's action (`createTargetTable`, ...). */
export interface ProvisioningApply { replication: string; mapping: string; action: string }

/**
 * Applies one mapping's side of a plan: the set's manual task `provision` of the version the console's newest deploy of a build of this draft put
 * on a Runner, started through the console as the signed-in person and followed until it ends (Design: docs/publishing.md#runs-of-a-deployed-build).
 * The statements run there under the logins the Runner's channel binds; the Workspace server holds none.
 */
export function useProvisioningApply(project: string, id: string) {
  const staged = useRunnerVersion(project, id)
  const apply = useMutation({
    mutationFn: async ({ replication, mapping, action }: ProvisioningApply) => {
      const at = staged.data
      if (!at) throw new Error('No build of this draft is staged on a Runner. Build it and publish it to a Runner (staged, not served); then apply.')
      const started = await deployApi.run(at.deploy, 'provision', { replication, mapping, action, steps: '', repo_dir: 'repo' })
      return followStagedRun(() => deployApi.runOf(at.deploy, started.run.run_id, AppliedStep))
    },
  })
  return { staged, apply }
}
