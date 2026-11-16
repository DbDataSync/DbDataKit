import { useState } from 'react'
import { Link as ConsoleLink } from 'react-router-dom'
import { useMutation, useQuery } from '@tanstack/react-query'
import { ErrorBanner, parseYaml } from '@dbdatakit/shell'
import { workspaceApi, type BuildSummary, type StagedRun, type StagedRunStarted } from '../api'
import { followStagedRun } from './SyncProvisioningPanel'

/** The step the task `deploy` records its report as (`DbDataKit.DataModel.Runner`, `ModelDeploySteps.Report`). */
export const DeployStep = 'deploy-report'

/** What a run of `deploy` reports (the step `deploy-report`): what it did or would do to the model's schema. */
export interface DeployReport { model: string; schema: string; engine: string; action: 'create' | 'expand' | 'none' | string; dryRun: boolean; applied: boolean; statements: string[]; blocked: string[]; notes: string[] }

/** The step the task `contract` records its report as (`ModelContractSteps.Report`). */
export const ContractStep = 'contract-report'

/** What a run of `contract` reports: the plan's hash (what an approval names), its steps, what blocks it, the notes and the columns it drops as the lineage graph names them. */
export interface ContractReport {
  model: string; schema: string; engine: string; hash: string; dryRun: boolean; applied: boolean
  steps: { object: string; description: string; text: string; requiresEveryRowToHaveAValue: boolean }[]
  blocked: string[]; notes: string[]; dropped: { description: string; columnId: string; datasetId: string }[]
}

/** The newest report of a run, of whatever task (null when it recorded none). */
function reportOf<T>(run: StagedRun): T | null {
  const step = [...(run.steps ?? [])].reverse().find((s) => s.result && typeof s.result === 'object')
  return (step?.result as T | undefined) ?? null
}

/** Whether a contract plan may be approved: it has steps and nothing blocks it. */
export const canContract = (r: ContractReport | null): boolean => !!r && r.dryRun && r.steps.length > 0 && r.blocked.length === 0

/** The allowances a person typed (comma or space separated): `<name>@<channel>` or `gateway:<name>`. */
export const allowancesOf = (text: string): string[] => text.split(/[\s,]+/).map((a) => a.trim()).filter(Boolean)

/** The models of a draft: the names of `models/<name>.yaml`, in order. */
export const modelsOf = (paths: readonly string[]): string[] =>
  paths.map((p) => /^models\/([^/]+)\.ya?ml$/.exec(p)?.[1]).filter((n): n is string => !!n).sort()

/** The package a model's deploy runs as on a Runner (`ModelDeployPackage.NameOf`): `model-<name>`, lower case, what a package name does not take made a dash, at most 64. */
export const packageOf = (model: string): string =>
  ('model-' + [...model.toLowerCase()].map((c) => (/[a-z0-9_-]/.test(c) ? c : '-')).join('')).slice(0, 64)

/** The connection a model's text names in `storage.connection`: what the Runner's slot `storage` is bound to unless the person names another. */
export function storageConnectionOf(text: string): string {
  const parsed = parseYaml(text)
  const storage = (parsed.value as { storage?: { connection?: unknown } } | null)?.storage
  return typeof storage?.connection === 'string' ? storage.connection : ''
}

/** The report a run recorded (its newest `deploy-report` step), or null when it recorded none (a run that failed before it planned). */
export const deployReportOf = (run: StagedRun): DeployReport | null => reportOf<DeployReport>(run)

/** The contract report a run recorded, or null. */
export const contractReportOf = (run: StagedRun): ContractReport | null => reportOf<ContractReport>(run)

/** Whether a dry run's report allows the deploy itself: it planned a change and nothing is blocked (a blocked change is the contract step's, not a deploy's). */
export const canApply = (report: DeployReport | null): boolean => !!report && report.dryRun && report.statements.length > 0 && report.blocked.length === 0

/** What a report says in a sentence. */
export function reportLine(r: DeployReport): string {
  const where = `${r.schema} (${r.engine})`
  if (r.action === 'none') return `The tables of ${r.model} in ${where} are what the model says: nothing to do.`
  const [verb, done] = r.action === 'create' ? ['create', 'created'] : ['expand', 'expanded']
  if (r.dryRun) return `A deploy would ${verb} the tables of ${r.model} in ${where}: ${r.statements.length} statement(s)${r.blocked.length > 0 ? `, ${r.blocked.length} blocked` : ''}.`
  return r.applied ? `Deployed: ${done} the tables of ${r.model} in ${where}.` : `Not deployed: the tables of ${r.model} in ${where} were left as they were.`
}

/** Waits for a build to end (each `intervalMs`, at most `attempts` times) and returns it; the last read when it has not ended. */
export async function followBuild(read: () => Promise<BuildSummary>, intervalMs = 1000, attempts = 600): Promise<BuildSummary> {
  let b = await read()
  for (let i = 1; i < attempts && b.status !== 'succeeded' && b.status !== 'failed'; i++) {
    await new Promise((r) => setTimeout(r, intervalMs))
    b = await read()
  }
  return b
}

interface Deployed { started: StagedRunStarted; run: StagedRun; build: string }

/** A run's line: its id, package, version, channel and outcome, a link to it on the Runner's screens, and its failure. */
function RunHead({ result }: { result: Deployed }) {
  const { run, started } = result
  return (
    <>
      <div>
        Run <span className="mono">{run.id}</span> of {started.task} of {started.package} version {run.version ?? started.version} on {started.channel}:{' '}
        <span className={run.outcome === 'succeeded' ? '' : 'error'}>{run.finished ? run.outcome : `${run.outcome}, not ended yet`}</span>
        {started.instance && <> (<ConsoleLink to={`/servers/${encodeURIComponent(started.instance)}/runs?run=${encodeURIComponent(run.id)}`} data-testid="wsp-deploy-run-link">the run on {started.target}</ConsoleLink>)</>}
      </div>
      {run.failure && <div className="error" data-testid="wsp-deploy-failure">{run.failure.message}</div>}
    </>
  )
}

export function DeployResult({ result }: { result: Deployed }) {
  const { run } = result
  const report = deployReportOf(run)
  return (
    <div className="card flush" style={{ marginTop: 8, padding: '6px 10px' }} data-testid="wsp-deploy-result" data-outcome={run.outcome} data-dry={report ? String(report.dryRun) : undefined}>
      <RunHead result={result} />
      {report && (
        <>
          <div data-testid="wsp-deploy-line">{reportLine(report)}</div>
          {report.statements.map((s, i) => <div key={i} className="mono hint" data-testid="wsp-deploy-statement">{s}</div>)}
          {report.blocked.map((s, i) => <div key={i} className="error" data-testid="wsp-deploy-blocked">Blocked: {s}</div>)}
          {report.notes.map((s, i) => <div key={i} className="hint" data-testid="wsp-deploy-note">{s}</div>)}
        </>
      )}
    </div>
  )
}

/** A contract run: the plan (each step in words and as the statement), what blocks it, the notes, the columns it drops and its hash; or, after an approval, whether it ran. */
export function ContractResult({ result }: { result: Deployed }) {
  const report = contractReportOf(result.run)
  return (
    <div className="card flush" style={{ marginTop: 8, padding: '6px 10px' }} data-testid="wsp-contract-result" data-outcome={result.run.outcome} data-dry={report ? String(report.dryRun) : undefined}>
      <RunHead result={result} />
      {report && (
        <>
          <div data-testid="wsp-contract-line">
            {report.dryRun
              ? report.steps.length === 0 && report.blocked.length === 0 ? `The tables of ${report.model} in ${report.schema} have nothing to contract.` : `The contract of ${report.model} in ${report.schema} (${report.engine}) would run ${report.steps.length} step(s). Plan ${report.hash.slice(0, 12)}.`
              : report.applied ? `Contracted: ${report.steps.length} step(s) ran on ${report.schema}.` : `Not contracted: ${report.schema} was left as it was.`}
          </div>
          {report.steps.map((s, i) => (
            <div key={i} data-testid="wsp-contract-step">
              <div className="hint">{s.description}{s.requiresEveryRowToHaveAValue ? ' (every row must have a value first)' : ''}</div>
              <pre className="mono" style={{ margin: 0, overflow: 'auto', maxHeight: 160 }}>{s.text}</pre>
            </div>
          ))}
          {report.blocked.map((s, i) => <div key={i} className="error" data-testid="wsp-contract-blocked">Blocked: {s}</div>)}
          {report.notes.map((s, i) => <div key={i} className="hint" data-testid="wsp-contract-note">{s}</div>)}
          {report.dryRun && report.dropped.length > 0 && <div className="hint" data-testid="wsp-contract-dropped">Before it runs, the paired Gateways are asked who reads: {report.dropped.map((d) => d.description).join('; ')}.</div>}
        </>
      )}
    </div>
  )
}

/**
 * The Deploy tab of a Store project's workspace (plan 33 SC24): a model's tables created or expanded with no Store server. **Dry run** builds the draft, stages the model's deploy package on a channel of a Runner with the slot `storage` bound
 * to a connection the Runner holds, and runs the manual task `deploy` of that staged version for the signed-in person without writing; **Deploy** runs it again on the same version, writing. The login is the Runner's; this server holds none.
 */
export function StoreDeployPanel({ project, id, paths }: { project: string; id: string; paths: readonly string[] }) {
  const models = modelsOf(paths)
  const [chosen, setChosen] = useState('')
  const model = chosen !== '' && models.includes(chosen) ? chosen : (models[0] ?? '')
  const [target, setTarget] = useState('')
  const [channel, setChannel] = useState('prod')
  const [connection, setConnection] = useState<string | null>(null)
  const [schema, setSchema] = useState('')
  const targets = useQuery({ queryKey: ['workspace', 'publish-targets', project], queryFn: () => workspaceApi.publishTargets(project) })
  const runners = (targets.data ?? []).filter((t) => t.product === 'runner')
  const runner = target !== '' && runners.some((t) => t.name === target) ? target : (runners[0]?.name ?? '')
  const file = paths.find((p) => p === `models/${model}.yaml` || p === `models/${model}.yml`)
  const text = useQuery({ queryKey: ['workspace', project, id, 'file', file], queryFn: () => workspaceApi.readFile(project, id, file!), enabled: !!file })
  const storage = connection ?? storageConnectionOf(text.data ?? '')
  const pkg = packageOf(model)

  const run = async (build: string, dry: boolean): Promise<Deployed> => {
    const inputs: Record<string, string> = { dry_run: String(dry), ...(schema.trim() ? { schema: schema.trim() } : {}) }
    const started = (await workspaceApi.runStaged(project, id, build, 'deploy', inputs, pkg)).run
    return { started, build, run: await followStagedRun(() => workspaceApi.stagedRun(project, id, build, started.run_id, DeployStep, pkg)) }
  }
  // builds the draft as it is and stages the model's package on the Runner's channel, its slot storage bound; the build that did it is what the tasks run of
  const stage = async (): Promise<string> => {
    if (!runner) throw new Error('No Runner is paired with this server. An administrator pairs one under Publisher keys; then a model can be deployed through it.')
    if (!storage.trim()) throw new Error('Name the connection on the Runner the model\'s tables are in (the slot storage is bound to it).')
    const requested = await workspaceApi.requestBuild(project, id)
    const built = await followBuild(async () => (await workspaceApi.build(project, id, requested.id)).build)
    if (built.status !== 'succeeded') throw new Error(`The build ${built.status === 'failed' ? 'failed' : 'did not end'}${built.message ? `: ${built.message}` : ''}. See the Builds tab.`)
    await workspaceApi.publish(project, id, built.id, runner, '', { channel: channel.trim(), model, bind: { storage: storage.trim() } })
    return built.id
  }
  const dry = useMutation({ mutationFn: async (): Promise<Deployed> => run(await stage(), true) })
  const apply = useMutation({ mutationFn: () => run(dry.data!.build, false) })
  const report = dry.data ? deployReportOf(dry.data.run) : null

  const [allow, setAllow] = useState('')
  const plan = useMutation({
    mutationFn: async (): Promise<Deployed> => {
      const build = dry.data?.build ?? await stage()
      const started = (await workspaceApi.runStaged(project, id, build, 'contract', schema.trim() ? { schema: schema.trim() } : {}, pkg)).run
      return { started, build, run: await followStagedRun(() => workspaceApi.stagedRun(project, id, build, started.run_id, ContractStep, pkg)) }
    },
  })
  const planned = plan.data ? contractReportOf(plan.data.run) : null
  const contract = useMutation({
    mutationFn: async (): Promise<Deployed> => {
      const { build, run: read } = plan.data!
      const started = (await workspaceApi.contract(project, id, build, { package: pkg, plan_run: read.id, approve: planned!.hash, allow_consumer: allowancesOf(allow) })).run
      return { started, build, run: await followStagedRun(() => workspaceApi.stagedRun(project, id, build, started.run_id, ContractStep, pkg)) }
    },
  })
  const busy = dry.isPending || apply.isPending || plan.isPending || contract.isPending

  if (models.length === 0) return <div className="hint" data-testid="wsp-deploy-no-model">No model yet: a model's tables are deployed once the draft has one.</div>
  return (
    <div data-testid="wsp-deploy">
      <div className="hint" style={{ marginBottom: 6 }}>Creates the tables of a model, or adds what the model has gained, on a Runner: the draft is built and the model's package staged there (not served), then its task deploy runs for you with the Runner's login. A dry run comes first and writes nothing; removing a column or a table is never done here.</div>
      <div className="row" style={{ gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
        <select className="input mono" aria-label="Model" value={model} onChange={(e) => { setChosen(e.target.value); setConnection(null); dry.reset(); apply.reset(); plan.reset(); contract.reset() }} data-testid="wsp-deploy-model">
          {models.map((m) => <option key={m} value={m}>{m}</option>)}
        </select>
        <select className="input" aria-label="Runner" value={runner} onChange={(e) => setTarget(e.target.value)} data-testid="wsp-deploy-runner" disabled={runners.length === 0}>
          {runners.length === 0 ? <option value="">No Runner paired</option> : runners.map((t) => <option key={t.name} value={t.name}>{t.name}</option>)}
        </select>
        <input className="input mono" style={{ width: 110 }} aria-label="Channel" value={channel} onChange={(e) => setChannel(e.target.value)} data-testid="wsp-deploy-channel" />
        <input className="input mono" style={{ width: 150 }} aria-label="Connection on the Runner" placeholder="connection" value={storage} onChange={(e) => setConnection(e.target.value)} data-testid="wsp-deploy-connection" />
        <input className="input mono" style={{ width: 150 }} aria-label="Schema" placeholder={`schema (${model})`} value={schema} onChange={(e) => setSchema(e.target.value)} data-testid="wsp-deploy-schema" />
        <button type="button" className="btn btn-primary" disabled={busy || !runner || channel.trim() === ''} data-testid="wsp-deploy-dry" onClick={() => { apply.reset(); dry.mutate() }}>{dry.isPending ? 'Building and planning...' : 'Dry run'}</button>
        <button type="button" className="btn" disabled={busy || !canApply(report)} data-testid="wsp-deploy-apply" onClick={() => apply.mutate()}>{apply.isPending ? 'Deploying...' : 'Deploy'}</button>
      </div>
      {targets.isSuccess && runners.length === 0 && <div className="hint" data-testid="wsp-deploy-no-runner">No Runner is paired with this server: an administrator pairs one under Publisher keys.</div>}
      <ErrorBanner error={dry.error ?? apply.error} />
      {dry.data && <DeployResult result={dry.data} />}
      {apply.data && <DeployResult result={apply.data} />}

      <div style={{ marginTop: 18 }} data-testid="wsp-contract">
        <div className="hint" style={{ marginBottom: 6 }}>The contract step removes what the model no longer has (a column is dropped) and makes required what it now requires. Read the plan first; what runs is exactly the plan you approve, and only once no published dashboard, report, app or model member reads what it drops (or you allow each by name, which is recorded).</div>
        <div className="row" style={{ gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
          <button type="button" className="btn" disabled={busy || !runner || channel.trim() === ''} data-testid="wsp-contract-plan" onClick={() => { contract.reset(); plan.mutate() }}>{plan.isPending ? 'Reading the plan...' : 'Plan the contract'}</button>
          {canContract(planned) && (
            <>
              <input className="input mono" style={{ minWidth: 220, flex: 1 }} aria-label="Allow" placeholder="allow: name@channel, gateway:name" value={allow} onChange={(e) => setAllow(e.target.value)} data-testid="wsp-contract-allow" />
              <button type="button" className="btn btn-primary" disabled={busy} data-testid="wsp-contract-apply" onClick={() => contract.mutate()}>{contract.isPending ? 'Contracting...' : `Contract (plan ${planned!.hash.slice(0, 12)})`}</button>
            </>
          )}
        </div>
        <ErrorBanner error={plan.error ?? contract.error} />
        {plan.data && <ContractResult result={plan.data} />}
        {contract.data && <ContractResult result={contract.data} />}
      </div>
    </div>
  )
}
