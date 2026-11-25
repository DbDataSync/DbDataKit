import { useState } from 'react'
import { useMutation, useQuery } from '@tanstack/react-query'
import { Link as ConsoleLink } from 'react-router-dom'
import { ErrorBanner, useModule } from '@dbdatakit/shell'
import {
  authoringReadApi, describedLine, newestPerChannel, setUpChannels, TEST_STEP_WORDS, testLine,
  type DescribeAnswer, type DescribedColumn, type DescribeRequest, type DraftTestAnswer, type NewestRun, type RunAnswer,
} from '../authoringReads'
import { workspaceServerOf, type ChannelList } from '../catalogRefresh'
import { developmentLoginApi, developmentLoginLine, developmentLoginWarning, developmentLoginWarnings, type DevelopmentLoginAnswer, type DevelopmentLoginRequest } from '../developmentLogin'
import { useDraftOrNull } from '../draft'
import { deployApi, useHostedServer, useRunnerVersion, type RunnerVersion } from '../deploys'
import { findingsSummary, PREFLIGHT_STEP, PREFLIGHT_TASK, PreflightFindings, preflightFindingsOf } from './PreflightCard'

// The Designer's shared pieces of authoring-time reads besides the channel picker and Refresh: Set up for development, Test, Describe and the newest-check
// card. Each asks the console as the signed-in person; none asks the Workspace server for more than whether the person may read the project.
// Design: docs/authoring-metadata.md#every-kind-uses-the-same-pieces

/**
 * **Set up for development**: the person types a development login for the connection once, for a channel other than production; the screen sends it to the
 * console with the draft, and the console stores the login in the Runner's secret store, puts the draft's connection on the Runner as a copy that names it,
 * and binds the slot in the channel. The login never goes to the Workspace server. `onStored` lets the picker ask the Runner again, which then offers the
 * channel. Outside a draft there is no connection file to copy, so nothing is offered.
 */
export function DevelopmentSetUp({ project, runner, runnerName, list, connection, onStored, canEdit }: {
  project: string; runner: string; runnerName: string; list: ChannelList | undefined; connection: string; onStored?: (answer: DevelopmentLoginAnswer) => void; canEdit: boolean
}) {
  const workspace = workspaceServerOf(useModule().apiBase)
  const draft = useDraftOrNull()
  const choices = setUpChannels(list)
  const [open, setOpen] = useState(false)
  const [channel, setChannel] = useState('')
  const [user, setUser] = useState('')
  const [password, setPassword] = useState('')
  const chosen = channel || choices[0] || ''
  const store = useMutation<DevelopmentLoginAnswer, Error, DevelopmentLoginRequest>({
    mutationFn: (body) => developmentLoginApi.setUp(workspace!, project, body),
    // the password leaves the form as soon as it is sent: it is kept nowhere on the screen
    onSuccess: (answer) => { setPassword(''); setOpen(false); onStored?.(answer) },
  })
  if (workspace === null || draft === null || choices.length === 0) return null
  const stored = store.data
  const warnings = stored ? developmentLoginWarnings(stored) : []
  return (
    <div style={{ marginTop: 6 }} data-testid="wsp-setup" data-connection={connection}>
      {!open && (
        <button type="button" className="btn btn-sm" disabled={!canEdit || runner === ''} onClick={() => { setOpen(true); store.reset() }} data-testid="wsp-setup-open">
          Set up {connection} for development
        </button>
      )}
      {open && (
        <form className="card" style={{ marginTop: 6 }} data-testid="wsp-setup-form" onSubmit={(e) => { e.preventDefault(); store.mutate({ runner, channel: chosen, connection, ...(user.trim() ? { user: user.trim() } : {}), password, draft: draft.id }) }}>
          <div className="card-body">
            <div className="hint" data-testid="wsp-setup-warning">{developmentLoginWarning(connection, chosen, runnerName)}</div>
            <div className="row" style={{ gap: 8, alignItems: 'center', flexWrap: 'wrap', marginTop: 6 }}>
              <label className="hint" htmlFor="wsp-setup-channel">Channel</label>
              <select id="wsp-setup-channel" className="input" value={chosen} onChange={(e) => setChannel(e.target.value)} data-testid="wsp-setup-channel">
                {choices.map((c) => <option key={c} value={c}>{c}</option>)}
              </select>
              <label className="hint" htmlFor="wsp-setup-user">User</label>
              <input id="wsp-setup-user" className="input" autoComplete="off" value={user} onChange={(e) => setUser(e.target.value)} data-testid="wsp-setup-user" />
              <label className="hint" htmlFor="wsp-setup-password">Password</label>
              <input id="wsp-setup-password" className="input" type="password" autoComplete="new-password" value={password} onChange={(e) => setPassword(e.target.value)} data-testid="wsp-setup-password" />
            </div>
            <div className="row" style={{ gap: 8, marginTop: 6 }}>
              <button type="submit" className="btn btn-primary btn-sm" disabled={store.isPending || password === '' || chosen === ''} data-testid="wsp-setup-save">Send the login to {runnerName}</button>
              <button type="button" className="btn btn-sm" onClick={() => { setOpen(false); setPassword('') }} data-testid="wsp-setup-cancel">Cancel</button>
            </div>
            {store.isError && <div data-testid="wsp-setup-refused"><ErrorBanner error={store.error} /></div>}
          </div>
        </form>
      )}
      {stored && !open && (
        <div className="hint" data-testid="wsp-setup-stored" data-channel={stored.channel} data-bound={stored.bound === true}>
          {developmentLoginLine(stored)}
          {warnings.map((w) => <div key={w} className="warn" data-testid="wsp-setup-warning-step">{w}</div>)}
        </div>
      )}
    </div>
  )
}

/**
 * **Test** of a draft's connection in the picked channel: the Runner's binding test of the connection's slot, asked through the console as the signed-in
 * person, with no build. Each step is shown (the binding, the connection, the login, the connect), with the Runner's reason; no row and no value.
 */
export function BindingTest({ project, connection, runner, channel, role = 'default' }: { project: string; connection: string; runner: string; channel: string; role?: 'default' | 'read' | 'write' }) {
  const workspace = workspaceServerOf(useModule().apiBase)
  const test = useMutation<DraftTestAnswer, Error, void>({ mutationFn: () => authoringReadApi.test(workspace!, project, { runner, channel, connection, role }) })
  if (workspace === null) return null
  // an answer of another Runner, channel or connection than the one picked now is not shown
  const answer = test.data && test.data.runner === runner && test.data.channel === channel && test.data.connection === connection ? test.data : undefined
  return (
    <div style={{ marginTop: 6 }} data-testid="wsp-test" data-connection={connection} data-channel={channel}>
      <button type="button" className="btn btn-sm" disabled={runner === '' || channel === '' || test.isPending} onClick={() => test.mutate()} data-testid="wsp-test-go">
        Test {connection}{channel ? ` in ${channel}` : ''}
      </button>
      {test.isPending && <span className="hint" data-testid="wsp-test-waiting"> Testing on the Runner...</span>}
      {test.isError && <div data-testid="wsp-test-refused"><ErrorBanner error={test.error} /></div>}
      {answer && (
        <div className={`banner ${answer.ok ? '' : 'warn'}`} role="status" style={{ display: 'block', marginTop: 6 }} data-testid="wsp-test-result" data-ok={answer.ok} data-step={answer.step ?? ''}>
          <div data-testid="wsp-test-message">{testLine(answer)}</div>
          <ol style={{ margin: '4px 0', paddingLeft: 18 }}>
            {answer.steps.map((s) => (
              <li key={s.step} data-testid="wsp-test-step" data-step={s.step} data-state={s.state} className={s.state === 'failed' ? 'error' : s.state === 'skipped' ? 'hint' : ''}>
                {TEST_STEP_WORDS[s.step] ?? s.step}: {s.state === 'skipped' ? 'not tried' : s.state}
              </li>
            ))}
          </ol>
        </div>
      )}
    </div>
  )
}

/** How often the screen asks how a description stands once the console stopped waiting. */
const FOLLOW_MS = 2000

/**
 * **Describe columns** of a query: the Runner's built-in `describe` in the picked channel, with the login it binds, asked through the console as the signed-in
 * person. The query is described, never run: no row comes back. A description that takes longer than the console waits is followed by its run. The kind
 * decides where the columns go (a Sync mapping's column cache, a Build model's descriptor): **Use these columns** hands them to `onColumns`.
 */
export function QueryDescribe({ project, connection, runner, channel, sql, onColumns, canEdit }: {
  project: string; connection: string; runner: string; channel: string; sql: string; onColumns?: (columns: DescribedColumn[]) => void; canEdit: boolean
}) {
  const workspace = workspaceServerOf(useModule().apiBase)
  const [asked, setAsked] = useState<DescribeRequest | null>(null)
  const [used, setUsed] = useState<string | null>(null)
  const start = useMutation<DescribeAnswer, Error, DescribeRequest>({ mutationFn: (body) => authoringReadApi.describe(workspace!, project, body), onMutate: (body) => { setAsked(body); setUsed(null) } })
  const running = start.data?.state === 'running' ? start.data : undefined
  const follow = useQuery({
    queryKey: ['console', 'describe', workspace, project, running?.run_id], queryFn: () => authoringReadApi.followDescribe(workspace!, project, running!.run_id, asked!),
    enabled: running !== undefined && asked !== null, refetchInterval: (q) => (q.state.data?.state === 'running' || q.state.data === undefined ? FOLLOW_MS : false), retry: false,
  })
  if (workspace === null) return null
  const answer = start.data?.state === 'done' ? start.data : follow.data?.state === 'done' ? follow.data : undefined
  const stillRunning = running !== undefined && follow.data?.state !== 'done' && !follow.isError
  return (
    <div style={{ marginTop: 6 }} data-testid="wsp-describe" data-connection={connection}>
      <button type="button" className="btn btn-sm" disabled={runner === '' || channel === '' || sql.trim() === '' || start.isPending || stillRunning}
        onClick={() => start.mutate({ runner, channel, connection, sql })} data-testid="wsp-describe-go">Describe columns</button>
      {start.isPending && <span className="hint" data-testid="wsp-describe-waiting"> Describing the query in {channel}...</span>}
      {stillRunning && running && (
        <div className="hint" data-testid="wsp-describe-running">
          The description takes longer than 10 seconds. It goes on as <ConsoleLink to={running.link} data-testid="wsp-describe-run-link">a run on {running.runner_name}</ConsoleLink>, and its columns are shown here when it ends.
        </div>
      )}
      {start.isError && <div data-testid="wsp-describe-failed"><ErrorBanner error={start.error} /></div>}
      {follow.isError && <div data-testid="wsp-describe-failed"><ErrorBanner error={follow.error} /></div>}
      {answer?.columns && (
        <div className="callout" style={{ marginTop: 6 }} data-testid="wsp-describe-result" data-run={answer.run_id}>
          <div data-testid="wsp-describe-summary">{describedLine(answer.columns)}</div>
          <table className="table" data-testid="wsp-describe-columns">
            <thead><tr><th>Column</th><th>Type</th><th>Null</th></tr></thead>
            <tbody>{answer.columns.map((c) => <tr key={c.name} data-testid="wsp-describe-column" data-column={c.name}><td className="mono">{c.name}</td><td className="mono">{c.type}</td><td>{c.nullable ? 'yes' : 'no'}</td></tr>)}</tbody>
          </table>
          {onColumns && (used === answer.run_id
            ? <div className="hint" data-testid="wsp-describe-used">The columns are in the draft as an unsaved edit.</div>
            : <button type="button" className="btn btn-primary btn-sm" disabled={!canEdit} onClick={() => { onColumns(answer.columns!); setUsed(answer.run_id) }} data-testid="wsp-describe-use">Use these columns</button>)}
        </div>
      )}
    </div>
  )
}

/** The newest run of a check in one channel: its time and outcome, what it found, and the run's link. */
export function NewestCheck({ version, task = PREFLIGHT_TASK, step = PREFLIGHT_STEP }: { version: RunnerVersion; task?: string; step?: string }) {
  const newest = useQuery<NewestRun, Error>({ queryKey: ['console', 'newest-run', version.deploy, task, step], queryFn: () => authoringReadApi.newest(version.deploy, task, step), retry: false })
  const run = newest.data?.run ?? null
  const findings = run ? preflightFindingsOf(run) : null
  const errors = findings?.filter((f) => f.severity === 'error').length ?? 0
  return (
    <div className="card" style={{ margin: '6px 0' }} data-testid="wsp-newest-check" data-channel={version.channel} data-runner={version.target} data-outcome={run?.outcome ?? 'none'} data-errors={errors}>
      <div className="card-body">
        <div><strong>{version.channel}</strong> <span className="hint">on {version.targetName}</span></div>
        <ErrorBanner error={newest.error} />
        {newest.isSuccess && !run && <div className="hint" data-testid="wsp-newest-check-none">No {task} has run in {version.channel} yet.</div>}
        {run && (
          <>
            <div className="hint" data-testid="wsp-newest-check-when">
              {task} of version {run.version ?? version.version}, {run.outcome}, {run.finished ? `finished ${run.finished}` : `started ${run.started}`}
              {newest.data?.link && <> <ConsoleLink to={newest.data.link} data-testid="wsp-newest-check-link">The run on {version.targetName}</ConsoleLink></>}
            </div>
            {run.failure && <div className="error" data-testid="wsp-newest-check-failure">{run.failure.message}</div>}
            {findings && <><div className={errors > 0 ? 'error' : 'hint'} data-testid="wsp-newest-check-summary">{findingsSummary(findings)}</div><PreflightFindings findings={findings} /></>}
          </>
        )}
      </div>
    </div>
  )
}

/**
 * The newest-check card: per channel this draft was published to on a Runner, the newest run of its check (the package's `preflight`), read through the
 * console. Nothing of it is committed: a check describes a login at a moment, not the project.
 */
export function NewestChecks({ project, id, task = PREFLIGHT_TASK, step = PREFLIGHT_STEP }: { project: string; id: string; task?: string; step?: string }) {
  const server = useHostedServer()
  const deploys = useQuery({
    queryKey: ['console', 'deploys', server, project, id, 'per-channel'],
    queryFn: async () => newestPerChannel(await deployApi.deploys({ workspaceServer: server!, project, workspace: id })), enabled: server !== null, retry: false,
  })
  const channels = (deploys.data ?? []).filter((v) => v.tasks.length === 0 || v.tasks.includes(task))
  if (server === null) return null
  return (
    <div style={{ marginTop: 8 }} data-testid="wsp-newest-checks" data-task={task}>
      <div className="hint">The newest {task} of each channel this draft was published to, with the logins that channel binds.</div>
      <ErrorBanner error={deploys.error} />
      {deploys.isSuccess && channels.length === 0 && <div className="hint" data-testid="wsp-newest-checks-none">No channel of a Runner has a version of this draft with a {task}.</div>}
      {channels.map((v) => <NewestCheck key={`${v.target}/${v.channel}`} version={v} task={task} step={step} />)}
    </div>
  )
}

/** What a screen asks of a run whose answer it waits for: the task and its inputs. */
export interface AnswerAsk { task: string; inputs: Record<string, string> }

/**
 * A run of a manual task a person waits for (the set's `preview`, `try-strategy`), on the version the newest deploy of this draft put on a Runner, started through
 * the console as the signed-in person; its answer comes back in the call, or, when the run takes longer than the console waits, from following the run. The answer
 * is kept in this screen's memory only: the console passes it on and neither it, the Runner nor the Workspace server keeps it.
 * Design: docs/authoring-metadata.md#rows-and-trials-are-answered-never-kept
 */
export function useRunAnswer<T>(project: string, id: string) {
  const published = useRunnerVersion(project, id)
  const version = published.data ?? null
  const start = useMutation<RunAnswer<T>, Error, AnswerAsk>({
    mutationFn: ({ task, inputs }) => {
      if (!version) throw new Error('No build of this draft is published to a Runner yet: build it and publish it on the Builds tab; the preview reads what that version has.')
      return authoringReadApi.answer<T>(version.deploy, task, inputs)
    },
  })
  const running = start.data?.state === 'running' && version ? start.data : undefined
  const follow = useQuery<RunAnswer<T>, Error>({
    queryKey: ['console', 'run-answer', version?.deploy, running?.run_id], queryFn: () => authoringReadApi.followAnswer<T>(version!.deploy, running!.run_id),
    enabled: running !== undefined, refetchInterval: (q) => (q.state.data?.state === 'running' || q.state.data === undefined ? FOLLOW_MS : false), retry: false,
  })
  const done = start.data?.state === 'done' ? start.data : follow.data?.state === 'done' ? follow.data : undefined
  return {
    /** The version the run reads: the newest deploy of this draft to a Runner; null when none. */
    version, loading: published.isLoading,
    ask: (asked: AnswerAsk) => start.mutate(asked),
    reset: () => start.reset(),
    pending: start.isPending,
    /** Still going after the console stopped waiting: the run and its link. */
    running: running !== undefined && follow.data?.state !== 'done' && !follow.isError ? running : undefined,
    answer: done?.answer, run: done,
    error: start.error ?? follow.error ?? null,
  }
}

/** Where a run a person waits for stands, in words: what it reads, the production warning, waiting, still going (with the run's link) or the reason it failed. */
export function RunAnswerStatus({ version, pending, running, error, testId }: {
  version: RunnerVersion | null; pending: boolean; running: RunAnswer<unknown> | undefined; error: Error | null; testId: string
}) {
  return (
    <>
      {version && version.channel === 'prod' && (
        <div className="hint warn" data-testid={`${testId}-production`}>This reads rows of production ({version.channel} on {version.targetName}) with the login production binds. The rows are shown here and kept nowhere.</div>
      )}
      {pending && <span className="hint" data-testid={`${testId}-waiting`}> Reading on {version?.targetName ?? 'the Runner'}...</span>}
      {running && (
        <div className="hint" data-testid={`${testId}-running`}>
          It takes longer than 10 seconds. It goes on as <ConsoleLink to={running.link} data-testid={`${testId}-run-link`}>a run on {running.runner_name}</ConsoleLink>, and its answer is shown here when it ends.
        </div>
      )}
      {error && <div data-testid={`${testId}-failed`}><ErrorBanner error={error} /></div>}
    </>
  )
}
