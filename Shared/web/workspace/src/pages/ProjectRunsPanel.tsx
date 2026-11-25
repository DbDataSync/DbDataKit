import { useState } from 'react'
import { Link as ConsoleLink } from 'react-router-dom'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { ErrorBanner } from '@dbdatakit/shell'
import type { BindingTestAnswer, StagedRun, StagedRunStarted } from '../api'
import { deployApi, useRunnerVersion } from '../deploys'
import { connectionsOf, replicationsOf } from '../syncSchemas'
import { followStagedRun } from './SyncProvisioningPanel'
import { NewestChecks } from './AuthoringReads'
import { findingsSummary, PREFLIGHT_STEP, PREFLIGHT_TASK, PreflightFindings, preflightFindingsOf } from './PreflightCard'

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

/** The database-facing views of a Build project, read-only manual tasks of its package (plan 33 SC20p): each runs the command of its name on the Runner and records its JSON document as the step `view-result`. */
export const BUILD_VIEWS = ['tests', 'status', 'compare'] as const
export type BuildView = typeof BUILD_VIEWS[number]
export const isBuildView = (task: string): task is BuildView => (BUILD_VIEWS as readonly string[]).includes(task)

/** The inputs a view takes, as the package declares them (`BuildViews.InputsOf`); the first are required where marked. */
export const VIEW_INPUTS: Record<BuildView, { name: string; label: string; required?: boolean; check?: boolean }[]> = {
  tests: [{ name: 'tests', label: 'tests (comma separated; blank: all)' }, { name: 'tags', label: 'tags' }, { name: 'kind', label: 'kind (metadata, model)' }],
  status: [{ name: 'connection', label: 'connection', required: true }, { name: 'plan', label: 'refresh plan (optional)' }],
  compare: [
    { name: 'connection', label: 'connection', required: true }, { name: 'table', label: 'schema.table', required: true }, { name: 'against', label: 'against schema.table' },
    { name: 'against_schema', label: 'against schema' }, { name: 'against_connection', label: 'against connection' }, { name: 'key', label: 'key columns' }, { name: 'show_values', label: 'read values', check: true },
  ],
}

/** Why a view cannot be started with these inputs, or null. */
export function viewProblem(view: BuildView, inputs: Record<string, string>): string | null {
  const missing = VIEW_INPUTS[view].filter((i) => i.required && !(inputs[i.name] ?? '').trim()).map((i) => i.label)
  if (missing.length > 0) return `Fill in: ${missing.join(', ')}.`
  if (view === 'compare' && !inputs.against?.trim() && !inputs.against_schema?.trim() && !inputs.against_connection?.trim()) return 'Say what it is compared with: another table, schema or connection.'
  return null
}

/** The document a view recorded (`view-result`): the command's JSON, or its head when it was too large. */
export interface ViewResult { view?: string; exit_code?: number; document?: unknown; truncated?: boolean; bytes?: number; head?: string }
export const viewResultOf = (run: StagedRun): ViewResult | null => ((run.steps ?? []).map((s) => s.result as ViewResult | null).filter((r): r is ViewResult => !!r && typeof r === 'object' && 'view' in r).pop() ?? null)

/** A view's line: its inputs and Run; the command runs on the Runner with the bound read logins. */
function BuildViewRow({ view, connections, busy, onRun }: { view: BuildView; connections: string[]; busy: boolean; onRun: (inputs: Record<string, string>) => void }) {
  const [inputs, setInputs] = useState<Record<string, string>>(view === 'tests' ? {} : { connection: connections[0] ?? '' })
  const problem = viewProblem(view, inputs)
  return (
    <div className="grid-row" style={{ gap: 8, flexWrap: 'wrap', alignItems: 'center' }} data-testid="wsp-runs-view" data-task={view}>
      <span className="mono" style={{ minWidth: 80 }}>{view}</span>
      {VIEW_INPUTS[view].map((i) => i.check
        ? <label key={i.name} className="hint"><input type="checkbox" checked={inputs[i.name] === 'true'} onChange={(e) => setInputs({ ...inputs, [i.name]: e.target.checked ? 'true' : 'false' })} data-testid={`wsp-runs-view-${i.name}`} /> {i.label}</label>
        : <input key={i.name} className="input mono" style={{ width: 150 }} aria-label={i.label} placeholder={i.label} value={inputs[i.name] ?? ''} onChange={(e) => setInputs({ ...inputs, [i.name]: e.target.value })} data-testid={`wsp-runs-view-${i.name}`} />)}
      <button type="button" className="btn btn-sm" disabled={busy || problem !== null} data-testid="wsp-runs-view-run" onClick={() => onRun(Object.fromEntries(Object.entries(inputs).filter(([, v]) => v.trim() !== '')))}>Run</button>
      {problem && <span className="hint" data-testid="wsp-runs-why">{problem}</span>}
    </div>
  )
}

/** The connections of a Build draft (`connections/<name>.yaml`). */
export const buildConnectionsOf = (paths: readonly string[]): string[] =>
  paths.map((p) => /^connections\/([^/]+)\.ya?ml$/i.exec(p)?.[1]).filter((n): n is string => !!n).sort()

/** The task that evaluates a package's checks again (plan 35 I3a, `QualityCheckTask`): read only, its results recorded on the Runner, a block cleared when the checks pass; the step `check-result` holds its document. */
export const CHECK_TASK = 'check'
export const CHECK_STEP = 'check-result'

/** One check of a re-check's document. */
export interface CheckLine { dataset: string; name?: string; check: string; kind?: string; severity?: string; status: 'pass' | 'fail' | 'error'; measured?: number | null; threshold?: number | null; sample_keys?: string[]; message?: string | null; blocking?: boolean }

/** The document the `check` task recorded. */
export interface CheckResult { task: 'check'; dataset?: string | null; datasets?: number; evaluated: number; passed: number; failed: number; errors: number; blocking: number; notes?: string[]; results?: CheckLine[]; truncated?: boolean }
export const checkResultOf = (run: StagedRun): CheckResult | null =>
  ((run.steps ?? []).map((s) => s.result as CheckResult | null).filter((r): r is CheckResult => !!r && typeof r === 'object' && (r as { task?: unknown }).task === CHECK_TASK).pop() ?? null)

/** The line under a re-check: the counts and whether anything still blocks. */
export function checkSummary(r: CheckResult): string {
  if (r.evaluated === 0) return 'No check was evaluated.'
  const counts = `${r.evaluated} check${r.evaluated === 1 ? '' : 's'}: ${r.passed} passed, ${r.failed} failed, ${r.errors} could not run.`
  return r.blocking > 0
    ? `${counts} ${r.blocking} still block${r.blocking === 1 ? 's' : ''} what depends on ${r.blocking === 1 ? 'its dataset' : 'their datasets'}: fix the data, then re-check again.`
    : `${counts} Nothing blocks: the next refresh loads what was waiting.`
}

/** The re-check's line: an optional dataset (a model's name; blank: every model with checks) and Re-check. */
function CheckRow({ busy, onRun }: { busy: boolean; onRun: (inputs: Record<string, string>) => void }) {
  const [dataset, setDataset] = useState('')
  return (
    <div className="grid-row" style={{ gap: 8, flexWrap: 'wrap', alignItems: 'center' }} data-testid="wsp-runs-check">
      <span className="mono" style={{ minWidth: 80 }}>{CHECK_TASK}</span>
      <input className="input mono" style={{ width: 220 }} aria-label="dataset" placeholder="model (blank: every model with checks)" value={dataset} onChange={(e) => setDataset(e.target.value)} data-testid="wsp-runs-check-dataset" />
      <button type="button" className="btn btn-sm" disabled={busy} data-testid="wsp-runs-recheck" onClick={() => onRun(dataset.trim() ? { dataset: dataset.trim() } : {})}>Re-check</button>
      <span className="hint">Evaluates the checks again, read only, and clears a block when they pass; nothing is loaded.</span>
    </div>
  )
}

/** The preflight's line: Re-check runs it on the version the channel serves, read only, and its findings come back here. */
function PreflightRow({ busy, onRun }: { busy: boolean; onRun: () => void }) {
  return (
    <div className="grid-row" style={{ gap: 8, flexWrap: 'wrap', alignItems: 'center' }} data-testid="wsp-runs-preflight">
      <span className="mono" style={{ minWidth: 80 }}>{PREFLIGHT_TASK}</span>
      <button type="button" className="btn btn-sm" disabled={busy} data-testid="wsp-runs-preflight-recheck" onClick={onRun}>Re-check</button>
      <span className="hint">Checks, read only, what would stand in the way of the published version on its databases; the console runs it before it serves a version in production.</span>
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
      {viewResultOf(run) && (
        <details open data-testid="wsp-runs-view-result">
          <summary className="hint">{viewResultOf(run)!.truncated ? `The document of ${viewResultOf(run)!.view} was ${viewResultOf(run)!.bytes} bytes; its head is kept` : `The document of ${viewResultOf(run)!.view} (exit code ${viewResultOf(run)!.exit_code})`}</summary>
          <pre className="mono" style={{ maxHeight: 360, overflow: 'auto', whiteSpace: 'pre-wrap' }}>{viewResultOf(run)!.truncated ? viewResultOf(run)!.head : JSON.stringify(viewResultOf(run)!.document, null, 2)}</pre>
        </details>
      )}
      {checkResultOf(run) && (
        <div data-testid="wsp-runs-check-result" data-blocking={checkResultOf(run)!.blocking}>
          <div className={checkResultOf(run)!.blocking > 0 ? 'error' : 'hint'} data-testid="wsp-runs-check-summary">{checkSummary(checkResultOf(run)!)}</div>
          {(checkResultOf(run)!.notes ?? []).map((n, i) => <div key={i} className="hint">{n}</div>)}
          {(checkResultOf(run)!.results ?? []).length > 0 && (
            <table className="table">
              <thead><tr><th>Dataset</th><th>Check</th><th>Result</th><th>Measure</th><th>Blocks</th></tr></thead>
              <tbody>
                {checkResultOf(run)!.results!.map((c, i) => (
                  <tr key={i} data-testid="wsp-runs-check-line" data-status={c.status}>
                    <td className="mono">{c.name ?? c.dataset}</td>
                    <td className="mono">{c.check}{c.severity ? ` (${c.severity})` : ''}</td>
                    <td className={c.status === 'pass' ? '' : 'error'}>{c.status}{c.message ? `: ${c.message}` : ''}</td>
                    <td>{c.measured ?? '-'}{c.threshold != null ? ` (allowed ${c.threshold})` : ''}{c.sample_keys && c.sample_keys.length > 0 ? `; for example ${c.sample_keys.slice(0, 3).join(' | ')}` : ''}</td>
                    <td>{c.blocking ? 'yes' : 'no'}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
          {checkResultOf(run)!.truncated && <div className="hint">The document was cut to fit; the Quality page has every result.</div>}
        </div>
      )}
      {preflightFindingsOf(run) && (
        <div data-testid="wsp-runs-preflight-result" data-errors={preflightFindingsOf(run)!.filter((f) => f.severity === 'error').length}>
          <div className={preflightFindingsOf(run)!.some((f) => f.severity === 'error') ? 'error' : 'hint'} data-testid="wsp-runs-preflight-summary">{findingsSummary(preflightFindingsOf(run)!)}</div>
          <PreflightFindings findings={preflightFindingsOf(run)!} />
        </div>
      )}
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
 * the Runner's screens link back to this workspace from the version's note). The version is the console's newest deploy of a build of this draft to a Runner, and every run, read and test goes through the console as the signed-in
 * person (Design: docs/publishing.md#runs-of-a-deployed-build): the Workspace server holds no login and calls no Runner.
 */
export function ProjectRunsPanel({ project, id, kind, paths }: { project: string; id: string; kind: 'build' | 'sync'; paths: readonly string[] }) {
  const published = useRunnerVersion(project, id)
  const at = published.data ?? null
  const instance = at?.target ?? null
  const queries = useQueryClient()
  const start = useMutation({
    mutationFn: async ({ task, inputs }: { task: string; inputs: Record<string, string> }): Promise<Started> => {
      if (!at) throw new Error('No build of this draft is published to a Runner. Build it and publish it to a Runner (Builds tab); then its tasks run from here.')
      const started = (await deployApi.run(at.deploy, task, inputs, true)).run
      const step = task === PREFLIGHT_TASK ? PREFLIGHT_STEP : kind === 'sync' ? 'pass-record' : isBuildView(task) ? 'view-result' : task === CHECK_TASK ? CHECK_STEP : undefined
      return { started, run: await followStagedRun(() => deployApi.runOf(at.deploy, started.run_id, step)) }
    },
    // a Re-check is the newest check of its channel now
    onSuccess: (_, { task }) => { if (task === PREFLIGHT_TASK) void queries.invalidateQueries({ queryKey: ['console', 'newest-run'] }) },
  })
  const screens = at && instance ? runnerScreens(instance, at.package) : null
  const [tests, setTests] = useState<Record<string, { answer?: BindingTestAnswer; error?: string }>>({})
  const test = useMutation({
    mutationFn: async (connection: string): Promise<{ connection: string; answer?: BindingTestAnswer; error?: string }> => {
      if (!at) throw new Error('Nothing is published yet.')
      try { return { connection, answer: await deployApi.bindingTest(at.deploy, syncSlotOf(connection)) } }
      catch (e) { return { connection, error: e instanceof Error ? e.message : String(e) } }
    },
    onSuccess: ({ connection, ...result }) => setTests((t) => ({ ...t, [connection]: result })),
  })
  const connections = kind === 'sync' ? connectionsOf(paths) : []
  const replications = replicationsOf(paths).filter((r) => !at || at.tasks.length === 0 || at.tasks.includes(syncTaskOf(r)))
  const tasks = kind === 'sync' ? [] : (at?.tasks ?? []).filter((t) => !isBuildView(t) && t !== CHECK_TASK && t !== PREFLIGHT_TASK)
  const preflight = (at?.tasks ?? []).includes(PREFLIGHT_TASK)
  const recheck = kind !== 'sync' && (at?.tasks ?? []).includes(CHECK_TASK)
  const views = kind === 'sync' ? [] : (at?.tasks ?? []).filter(isBuildView)
  return (
    <div data-testid="wsp-runs">
      <div className="hint" style={{ marginBottom: 6 }}>
        {kind === 'sync'
          ? 'The replications of the set this draft published, run on the Runner as its channel serves them. A replication whose target table does not exist yet refuses its first pass, naming the table: apply the setup on the Provisioning tab, then Run now.'
          : 'The tasks of the package this draft published, run on the Runner as its channel serves them. The project\'s tests, the status of a connection and compare run there too, read only, with the logins bound on the Runner, and their documents come back here. Re-check evaluates the checks of the project\'s models again (all, or one) and clears a block when they pass, without a refresh. Initialise and Build\'s operations (apply a plan, acknowledge, seed) are the operations package\'s, which this server does not build yet.'}
      </div>
      {published.isLoading ? null : at
        ? (
          <div className="hint" data-testid="wsp-runs-version">
            {at.package} version {at.version} on {at.channel} of {at.targetName} ({at.mode === 'stage' ? 'staged: its tasks run once the version is served' : 'served'}).
            {screens && <> <ConsoleLink to={screens.tasks} data-testid="wsp-runs-tasks-link">Its tasks on the Runner</ConsoleLink> (schedule, pause, resume) and <ConsoleLink to={screens.runs} data-testid="wsp-runs-history-link">its runs</ConsoleLink> (history, logs, cancel).</>}
          </div>
        )
        : <div className="hint" data-testid="wsp-runs-none">No build of this draft is published to a Runner yet: build it and publish it on the Builds tab, then its runs are here.</div>}
      <ErrorBanner error={published.error ?? start.error} />
      {kind === 'sync' && connections.length > 0 && (
        <div style={{ margin: '6px 0' }} data-testid="wsp-runs-connections">
          <div className="hint">Connections: Test tries, on the Runner, the login the published channel binds to each one's slot, the way a run would ({at ? 'it needs the administrator role on the Runner' : 'after publish: nothing is bound before the set is published'}).</div>
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
      {at && views.length > 0 && <div className="hint" style={{ marginTop: 6 }}>Views, read only:</div>}
      {at && views.map((v) => <BuildViewRow key={v} view={v} connections={buildConnectionsOf(paths)} busy={start.isPending} onRun={(inputs) => start.mutate({ task: v, inputs })} />)}
      {at && recheck && <CheckRow busy={start.isPending} onRun={(inputs) => start.mutate({ task: CHECK_TASK, inputs })} />}
      {at && preflight && <PreflightRow busy={start.isPending} onRun={() => start.mutate({ task: PREFLIGHT_TASK, inputs: {} })} />}
      {at && preflight && <NewestChecks project={project} id={id} />}
      {start.isPending && <div className="hint" data-testid="wsp-runs-running">Running on the Runner...</div>}
      {start.data && <RunLine result={start.data} instance={start.data.started.instance ?? instance} />}
    </div>
  )
}
