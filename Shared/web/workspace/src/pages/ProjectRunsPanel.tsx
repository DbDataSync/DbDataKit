import { useState } from 'react'
import { Link as ConsoleLink } from 'react-router-dom'
import { useMutation, useQuery } from '@tanstack/react-query'
import { ErrorBanner } from '@dbdatakit/shell'
import { workspaceApi, type BindingTestAnswer, type BuildDetail, type StagedRun, type StagedRunStarted } from '../api'
import { connectionsOf, replicationsOf } from '../syncSchemas'
import { followStagedRun } from './SyncProvisioningPanel'

/** The version a build of this draft last published (staged or served) on a Runner, with the tasks the Runner answered and how it was sent. */
export interface PublishedVersion { build: string; target: string; package: string; channel: string; version: string; mode: string; tasks: string[] }

/** The newest build, newest first, whose publish to a Runner worked and named a package (a preview is not one). */
export function publishedVersionOf(details: readonly BuildDetail[]): PublishedVersion | null {
  for (const d of details) {
    const record = [...d.publishes].reverse().find((p) => p.ok && !p.preview && p.answer?.package && p.answer.channel && p.answer.version != null)
    if (record?.answer) return { build: d.build.id, target: record.target, package: record.answer.package!, channel: record.answer.channel!, version: String(record.answer.version), mode: record.answer.mode ?? 'stage', tasks: record.answer.tasks ?? [] }
  }
  return null
}

/** The task a replication is on a Runner (`RunnerTaskNames.Of`): lower case, what a task name does not take made a dash, no leading dash or underscore, at most 64. */
export const syncTaskOf = (replication: string): string =>
  [...replication.toLowerCase()].map((c) => (/[a-z0-9_-]/.test(c) ? c : '-')).join('').replace(/^[-_]+/, '').slice(0, 64)

/** The table mappings of a replication of the draft, by name. */
export const mappingsOfReplication = (paths: readonly string[], replication: string): string[] =>
  paths.map((p) => new RegExp(`^config/replications/${replication.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}/table-mappings/([^/]+)\\.ya?ml$`, 'i').exec(p)?.[1]).filter((n): n is string => !!n).sort()

/** How a person asks a bulk load to be divided: the mapping's own default segmenting, the whole table, or an automatic range of a column in a number of buckets (the drain divides it against the source, plan 33 SC22l). */
export type Segmenting = { how: 'default' } | { how: 'full' } | { how: 'auto'; column: string; buckets: number }

/** Why a segmenting cannot be asked for, or null. */
export function segmentingProblem(s: Segmenting): string | null {
  if (s.how !== 'auto') return null
  if (s.column.trim() === '') return 'Name the column whose range is divided.'
  if (!Number.isInteger(s.buckets) || s.buckets < 2 || s.buckets > 1000) return 'The number of buckets is a whole number from 2 to 1000.'
  return null
}

/** The `requests` input of a drain for a person's bulk load (`DrainRequest`, `segments` as `SegmentSerializer` writes them; `[]` is the mapping's default segmenting). */
export function bulkLoadRequests(mapping: string, s: Segmenting): string {
  const segments = s.how === 'full' ? [{ mode: 'full' }] : s.how === 'auto' ? [{ mode: 'auto', column: s.column.trim(), bucketCount: s.buckets }] : []
  return JSON.stringify([{ kind: 'BulkLoad', mapping, segments: JSON.stringify(segments) }])
}

/** The console's addresses of a Runner's screens: its tasks, its runs of a package, one run. */
export const runnerScreens = (instance: string, pkg: string) => ({
  tasks: `/servers/${encodeURIComponent(instance)}/?package=${encodeURIComponent(pkg)}`,
  runs: `/servers/${encodeURIComponent(instance)}/runs?package=${encodeURIComponent(pkg)}`,
  run: (id: string) => `/servers/${encodeURIComponent(instance)}/runs?run=${encodeURIComponent(id)}`,
})

/** The slot a Sync connection is in its package on a Runner (`ReplicationSetPackage.SlotNameOf`): lower case, what is not a letter, digit or `_` made `_`, `c_` before a name that does not start with a letter, at most 64. */
export function syncSlotOf(connection: string): string {
  let name = connection.toLowerCase().replace(/[^a-z0-9_]/g, '_')
  if (name.length === 0 || !/^[a-z]/.test(name)) name = 'c_' + name
  return name.slice(0, 64)
}

/** A connection's line on the Runs tab: Test tries the login the published channel binds to its slot, on the Runner (plan 33 SC22h), and says how far it got. */
function ConnectionTestRow({ connection, onTest, busy, answer, error }: { connection: string; onTest: () => void; busy: boolean; answer?: BindingTestAnswer; error?: string }) {
  return (
    <div className="grid-row" style={{ gap: 8, alignItems: 'center', flexWrap: 'wrap' }} data-testid="wsp-runs-connection" data-name={connection}>
      <span className="mono" style={{ minWidth: 120 }}>{connection}</span>
      <span className="hint mono">slot {syncSlotOf(connection)}</span>
      <button type="button" className="btn btn-sm" disabled={busy} data-testid="wsp-runs-test-connection" onClick={onTest}>Test</button>
      {answer && <span className={answer.ok ? 'hint' : 'error'} data-testid="wsp-runs-test-result" data-ok={answer.ok} data-step={answer.step}>{answer.ok ? 'Works' : `Stopped at ${answer.step}`}: {answer.message}</span>}
      {error && <span className="error" data-testid="wsp-runs-test-error">{error}</span>}
    </div>
  )
}

/** A pass a drain recorded (`PassRecord`, the step `pass-record`): what the run did to each mapping. */
interface PassStep { mapping?: string; kind?: string; segment?: string | null; status?: string; rowsRead?: number; rowsWritten?: number; summary?: string | null; failureKind?: string | null }
export const passesOf = (run: StagedRun): PassStep[] => (run.steps ?? []).map((s) => (s.result ?? {}) as PassStep).filter((p) => !!p.mapping)

interface Started { started: StagedRunStarted; run: StagedRun }

function RunLine({ result, instance }: { result: Started; instance: string | null }) {
  const { run, started } = result
  return (
    <div className="card flush" style={{ marginTop: 8, padding: '6px 10px' }} data-testid="wsp-runs-result" data-outcome={run.outcome}>
      <div>
        Run <span className="mono">{run.id}</span> of {started.task}: <span className={run.outcome === 'succeeded' ? '' : 'error'}>{run.finished ? run.outcome : `${run.outcome}, not ended yet`}</span>
        {instance && <> (<ConsoleLink to={runnerScreens(instance, started.package).run(run.id)} data-testid="wsp-runs-run-link">the run and its log on {started.target}</ConsoleLink>)</>}
      </div>
      {run.failure && <div className="error" data-testid="wsp-runs-failure">{run.failure.message}</div>}
      {passesOf(run).map((p, i) => (
        <div key={i} className={p.status === 'Succeeded' ? 'hint' : 'error'} data-testid="wsp-runs-pass">
          {p.mapping}{p.segment ? ` (${p.segment})` : ''}: {p.kind} {p.status}, {p.rowsRead ?? 0} read, {p.rowsWritten ?? 0} written{p.failureKind ? ` (${p.failureKind})` : ''}{p.summary ? `: ${p.summary}` : ''}
        </div>
      ))}
    </div>
  )
}

/** A replication's line: run now, and a bulk load of one of its mappings as asked, divided by the drain. */
function ReplicationRow({ replication, mappings, busy, onRun }: { replication: string; mappings: string[]; busy: boolean; onRun: (task: string, inputs: Record<string, string>) => void }) {
  const [mapping, setMapping] = useState('')
  const [how, setHow] = useState<'default' | 'full' | 'auto'>('default')
  const [column, setColumn] = useState('')
  const [buckets, setBuckets] = useState('8')
  const chosen = mapping !== '' && mappings.includes(mapping) ? mapping : (mappings[0] ?? '')
  const segmenting: Segmenting = how === 'auto' ? { how, column, buckets: Number(buckets) } : { how }
  const problem = chosen === '' ? 'The replication has no table mapping.' : segmentingProblem(segmenting)
  const task = syncTaskOf(replication)
  return (
    <div className="grid-row" style={{ gap: 8, flexWrap: 'wrap', alignItems: 'center' }} data-testid="wsp-runs-replication" data-task={task}>
      <span className="mono" style={{ minWidth: 120 }}>{replication}</span>
      <button type="button" className="btn btn-sm" disabled={busy} data-testid="wsp-runs-run-now" onClick={() => onRun(task, { run_all: 'true' })}>Run now</button>
      <span className="hint">Bulk load</span>
      <select className="input mono" aria-label="Mapping" value={chosen} onChange={(e) => setMapping(e.target.value)} data-testid="wsp-runs-mapping">{mappings.map((m) => <option key={m} value={m}>{m}</option>)}</select>
      <select className="input" aria-label="Segmenting" value={how} onChange={(e) => setHow(e.target.value as 'default' | 'full' | 'auto')} data-testid="wsp-runs-segmenting">
        <option value="default">its default segmenting</option>
        <option value="full">the whole table</option>
        <option value="auto">a column's range, divided</option>
      </select>
      {how === 'auto' && <>
        <input className="input mono" style={{ width: 110 }} aria-label="Column" placeholder="column" value={column} onChange={(e) => setColumn(e.target.value)} data-testid="wsp-runs-column" />
        <input className="input mono" style={{ width: 60 }} aria-label="Buckets" value={buckets} onChange={(e) => setBuckets(e.target.value)} data-testid="wsp-runs-buckets" />
      </>}
      <button type="button" className="btn btn-sm" disabled={busy || problem !== null} data-testid="wsp-runs-bulk-load" onClick={() => onRun(task, { requests: bulkLoadRequests(chosen, segmenting) })}>Load</button>
      {problem && <span className="hint" data-testid="wsp-runs-why">{problem}</span>}
    </div>
  )
}

/**
 * The Runs tab of a Build or Sync project's workspace (plan 33 SC20o, SC22l): the runs of what this draft published, in the project's page. The version a build of this draft last sent to a Runner names the package and the channel; each of its tasks
 * runs now from here as the channel serves it (a replication's drain, a refresh), for the signed-in person with the logins bound on the Runner, and a replication's bulk load is asked as a person thinks of it (the default segmenting, the whole
 * table or a column's range in buckets), which the drain divides against the source. The run comes back here with what it did; its log, the schedule, pause and resume, cancel and the history are the Runner's screens, linked from here (and
 * the Runner's screens link back to this workspace from the version's note). Nothing runs on this server: it holds no login.
 */
export function ProjectRunsPanel({ project, id, kind, paths }: { project: string; id: string; kind: 'build' | 'sync'; paths: readonly string[] }) {
  const published = useQuery({
    queryKey: ['workspace', project, id, 'published-version'],
    queryFn: async () => {
      const succeeded = (await workspaceApi.builds(project, id)).filter((b) => b.status === 'succeeded').slice(0, 5)
      for (const b of succeeded) {
        const found = publishedVersionOf([await workspaceApi.build(project, id, b.id)])
        if (found) return found
      }
      return null
    },
  })
  const targets = useQuery({ queryKey: ['workspace', 'publish-targets', project], queryFn: () => workspaceApi.publishTargets(project) })
  const at = published.data ?? null
  const instance = at ? (targets.data ?? []).find((t) => t.name === at.target)?.instance ?? null : null
  const start = useMutation({
    mutationFn: async ({ task, inputs }: { task: string; inputs: Record<string, string> }): Promise<Started> => {
      if (!at) throw new Error('No build of this draft is published to a Runner. Build it and publish it to a Runner (Builds tab); then its tasks run from here.')
      const started = (await workspaceApi.runServed(project, id, at.build, task, inputs)).run
      return { started, run: await followStagedRun(() => workspaceApi.stagedRun(project, id, at.build, started.run_id, kind === 'sync' ? 'pass-record' : undefined)) }
    },
  })
  const screens = at && instance ? runnerScreens(instance, at.package) : null
  const [tests, setTests] = useState<Record<string, { answer?: BindingTestAnswer; error?: string }>>({})
  const test = useMutation({
    mutationFn: async (connection: string): Promise<{ connection: string; answer?: BindingTestAnswer; error?: string }> => {
      if (!at) throw new Error('Nothing is published yet.')
      try { return { connection, answer: await workspaceApi.bindingTest(project, id, at.build, syncSlotOf(connection)) } }
      catch (e) { return { connection, error: e instanceof Error ? e.message : String(e) } }
    },
    onSuccess: ({ connection, ...result }) => setTests((t) => ({ ...t, [connection]: result })),
  })
  const connections = kind === 'sync' ? connectionsOf(paths) : []
  const replications = replicationsOf(paths).filter((r) => !at || at.tasks.length === 0 || at.tasks.includes(syncTaskOf(r)))
  const tasks = kind === 'sync' ? [] : (at?.tasks ?? [])
  return (
    <div data-testid="wsp-runs">
      <div className="hint" style={{ marginBottom: 6 }}>
        {kind === 'sync'
          ? 'The replications of the set this draft published, run on the Runner as its channel serves them. A replication whose target table does not exist yet refuses its first pass, naming the table: apply the setup on the Provisioning tab, then Run now.'
          : 'The tasks of the package this draft published, run on the Runner as its channel serves them. The project\'s tests, the status of a target, compare and initialise read or write a database and have no Runner task yet (plan 33 SC20p).'}
      </div>
      {published.isLoading ? null : at
        ? (
          <div className="hint" data-testid="wsp-runs-version">
            {at.package} version {at.version} on {at.channel} of {at.target} ({at.mode === 'stage' ? 'staged: its tasks run once the version is served' : 'served'}).
            {screens && <> <ConsoleLink to={screens.tasks} data-testid="wsp-runs-tasks-link">Its tasks on the Runner</ConsoleLink> (schedule, pause, resume) and <ConsoleLink to={screens.runs} data-testid="wsp-runs-history-link">its runs</ConsoleLink> (history, logs, cancel).</>}
          </div>
        )
        : <div className="hint" data-testid="wsp-runs-none">No build of this draft is published to a Runner yet: build it and publish it on the Builds tab, then its runs are here.</div>}
      <ErrorBanner error={published.error ?? start.error} />
      {kind === 'sync' && connections.length > 0 && (
        <div style={{ margin: '6px 0' }} data-testid="wsp-runs-connections">
          <div className="hint">Connections: Test tries, on the Runner, the login the published channel binds to each one's slot, the way a run would ({at ? 'the pairing needs the Runner\'s admin role' : 'after publish: nothing is bound before the set is published'}).</div>
          {at && connections.map((c) => <ConnectionTestRow key={c} connection={c} busy={test.isPending} onTest={() => test.mutate(c)} answer={tests[c]?.answer} error={tests[c]?.error} />)}
        </div>
      )}
      {at && kind === 'sync' && replications.length === 0 && <div className="hint" data-testid="wsp-runs-no-replication">No replication of this draft is in the published version.</div>}
      {at && kind === 'sync' && replications.map((r) => <ReplicationRow key={r} replication={r} mappings={mappingsOfReplication(paths, r)} busy={start.isPending} onRun={(task, inputs) => start.mutate({ task, inputs })} />)}
      {at && tasks.map((t) => (
        <div key={t} className="grid-row" style={{ gap: 8, alignItems: 'center' }} data-testid="wsp-runs-task" data-task={t}>
          <span className="mono" style={{ minWidth: 160 }}>{t}</span>
          <button type="button" className="btn btn-sm" disabled={start.isPending} data-testid="wsp-runs-run-now" onClick={() => start.mutate({ task: t, inputs: {} })}>Run now</button>
        </div>
      ))}
      {start.isPending && <div className="hint" data-testid="wsp-runs-running">Running on the Runner...</div>}
      {start.data && <RunLine result={start.data} instance={start.data.started.instance ?? instance} />}
    </div>
  )
}
