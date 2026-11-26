import { useMemo, useState } from 'react'
import { useMutation, useQuery } from '@tanstack/react-query'
import { Link as ConsoleLink } from 'react-router-dom'
import { ErrorBanner } from '@dbdatakit/shell'
import { workspaceApi, type CheckDiagnostic, type ScriptTest } from '../api'
import { authoringReadApi } from '../authoringReads'
import { deployApi, useHostedServer, useRunnerVersion } from '../deploys'
import { channelProblem, defaultScriptTestPlace, PRODUCTION, scriptTestProductionWarning, testScriptOnRunner, type ScriptTestCalls, type ScriptTestPhase, type ScriptTestRun } from '../scriptTestOnRunner'
import { syncNameProblem } from '../syncSchemas'
import { SCRIPT_KINDS, newScriptFiles, scriptCodePath, scriptManifestPath, scriptsOf, type ScriptKind } from '../syncScripts'

const findingLine = (d: CheckDiagnostic) => `${d.file ? `${d.file}${d.line ? `:${d.line}` : ''} ` : ''}${d.code}: ${d.message}`

/** The servers a test calls: the Workspace server builds (through the console's relay), the console stages and runs. */
const calls: ScriptTestCalls = {
  requestBuild: workspaceApi.requestBuild,
  build: workspaceApi.build,
  deploy: deployApi.deploy,
  answer: (deploy, task, inputs) => authoringReadApi.answer<ScriptTest>(deploy, task, inputs),
  followAnswer: (deploy, run) => authoringReadApi.followAnswer<ScriptTest>(deploy, run),
  wait: (ms) => new Promise((resolve) => setTimeout(resolve, ms)),
}

const PHASE_WORDS: Record<ScriptTestPhase, string> = { building: 'Building the draft...', staging: 'Staging the build...', running: 'Running the test...' }

/** What a script did against generated input on a Runner: the verdict (compiled and ran, or the compiler's or the script's own words), where the input came from, each case, the log lines a transform wrote, and the run. */
export function ScriptTestView({ test, run }: { test: ScriptTest; run?: Omit<ScriptTestRun, 'test'> }) {
  return (
    <div data-testid="wsp-script-result" data-ok={String(test.ok)} data-run={run?.run_id}>
      <div className={test.ok ? 'hint' : 'error'} data-testid="wsp-script-verdict">
        {test.ok ? `${test.name} compiled and ran: ${test.source}.` : (test.message ?? `${test.name} did not run.`)}
      </div>
      {run && (
        <div className="hint" data-testid="wsp-script-run">
          Build {run.build}{run.version != null ? `, version ${run.version}` : ''} in {run.channel}: <ConsoleLink to={run.link} data-testid="wsp-script-run-link">the run on {run.runner_name}</ConsoleLink>
        </div>
      )}
      {test.cases.length > 0 && (
        <div style={{ overflow: 'auto' }}>
          <table className="mono" style={{ borderCollapse: 'collapse', width: '100%' }}>
            <thead><tr><th style={{ textAlign: 'left', padding: '4px 10px' }}>Input</th><th style={{ textAlign: 'left', padding: '4px 10px' }}>Output</th><th style={{ textAlign: 'left', padding: '4px 10px' }}>Note</th></tr></thead>
            <tbody>
              {test.cases.map((c, i) => (
                <tr key={i} data-testid="wsp-script-case">
                  <td style={{ padding: '4px 10px' }}>{c.input}</td>
                  <td style={{ padding: '4px 10px' }}>{c.output ?? '(none)'}</td>
                  <td className="hint" style={{ padding: '4px 10px' }}>{c.note ?? ''}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {test.log.length > 0 && <pre className="mono" style={{ margin: '6px 0 0', overflow: 'auto', maxHeight: 160 }} data-testid="wsp-script-log">{test.log.join('\n')}</pre>}
    </div>
  )
}

/**
 * The Scripts tab of a Sync project's workspace: the scripts of `config/scripts/` with the slot each fills, a new script from the skeleton of a slot, and the test of one on the saved draft
 * on a Runner: the draft is built, the build is staged in the channel the person picks through the console (production shows a warning first), and the set's task `script-test` compiles the script and runs it against
 * generated sample input there. Nothing in the draft changes; the files are edited like any other.
 * Design: docs/authoring-metadata.md#a-scripts-test-runs-on-a-runner
 */
export function SyncScriptsPanel({ project, id, paths, canWrite, busy, onOpen, onCreate }: { project: string; id: string; paths: readonly string[]; canWrite: boolean; busy: boolean; onOpen: (path: string) => void; onCreate: (files: { path: string; text: string }[]) => void }) {
  const names = scriptsOf(paths)
  const kinds = useQuery({ queryKey: ['workspace', 'scripts', project, id, names.join(',')], queryFn: () => workspaceApi.scripts(project, id), retry: false })
  const kindOf = (name: string) => kinds.data?.data?.scripts.find((s) => s.name === name)
  const [name, setName] = useState('')
  const [kind, setKind] = useState<ScriptKind>('sqlColumnExpression')
  const [rows, setRows] = useState('5')
  const [tested, setTested] = useState<string | null>(null)
  const [phase, setPhase] = useState<ScriptTestPhase | null>(null)
  const server = useHostedServer()
  const newest = useRunnerVersion(project, id)
  const servers = useQuery({ queryKey: ['console', 'servers'], queryFn: deployApi.servers, enabled: server !== null, staleTime: 10_000 })
  const runners = useMemo(() => (servers.data ?? []).filter((s) => s.product === 'dbdatarunner').sort((a, b) => a.name.localeCompare(b.name)), [servers.data])
  const [chosen, setChosen] = useState<{ runner?: string; channel?: string }>({})
  const fallback = defaultScriptTestPlace(newest.data ?? null, runners)
  const runner = chosen.runner ?? fallback?.runner ?? ''
  const channel = chosen.channel ?? fallback?.channel ?? ''
  const where = server === null ? 'A script is tested on a Runner through the console: open this draft from the console.' : runner === '' ? (servers.isLoading ? null : 'The console has no Runner to test a script on.') : channelProblem(channel)
  const test = useMutation<ScriptTestRun, Error, string>({
    mutationFn: (script) => {
      setTested(script)
      return testScriptOnRunner(calls, { workspaceServer: server!, project, workspace: id, runner, channel }, script, Math.min(50, Math.max(1, Number(rows) || 5)), setPhase)
    },
    onSettled: () => setPhase(null),
  })
  const problem = syncNameProblem(name, paths, scriptManifestPath)
  return (
    <div data-testid="wsp-scripts">
      <div className="hint" style={{ marginBottom: 6 }}>Scripts (C#, in <span className="mono">config/scripts/</span>): a test builds the saved draft, stages it on a Runner in the channel you pick, and compiles and runs the script there against generated sample input. It opens no connection.</div>
      {names.length === 0 && <div className="hint" data-testid="wsp-scripts-none">No script yet.</div>}
      {names.map((n) => {
        const entry = kindOf(n)
        return (
          <div key={n} className="grid-row" style={{ gap: 8, flexWrap: 'wrap' }} data-testid="wsp-script" data-name={n}>
            <button type="button" className="btn-link mono" data-testid="wsp-script-open-code" onClick={() => onOpen(scriptCodePath(n))}>{n}</button>
            <button type="button" className="btn-link mono hint" data-testid="wsp-script-open-manifest" onClick={() => onOpen(scriptManifestPath(n))}>{n}.yaml</button>
            <span className="hint" data-testid="wsp-script-kind">{entry?.error ? `unreadable: ${entry.error}` : entry ? `${entry.kind}, ${entry.language}` : ''}</span>
            <button type="button" className="btn" disabled={!canWrite || test.isPending || where !== null || runner === ''} title={!canWrite ? 'Testing builds and stages the draft: it needs the author role.' : where ?? undefined} data-testid="wsp-script-test" onClick={() => test.mutate(n)}>{test.isPending && tested === n ? 'Testing...' : 'Test'}</button>
          </div>
        )
      })}
      <div className="row" style={{ gap: 8, alignItems: 'center', margin: '8px 0', flexWrap: 'wrap' }}>
        <label className="row" style={{ gap: 4 }}>sample rows <input className="input mono" style={{ width: 60 }} value={rows} onChange={(e) => setRows(e.target.value)} data-testid="wsp-script-rows" /></label>
        {server !== null && runners.length > 0 && (
          <>
            <label className="row" style={{ gap: 4 }}>on
              <select className="input" aria-label="Runner" value={runner} onChange={(e) => setChosen({ ...chosen, runner: e.target.value })} data-testid="wsp-script-runner">
                {runners.map((r) => <option key={r.id} value={r.id}>{r.name}{r.reachable ? '' : ' (not answering)'}</option>)}
              </select>
            </label>
            <label className="row" style={{ gap: 4 }}>channel <input className="input mono" style={{ width: 90 }} value={channel} onChange={(e) => setChosen({ ...chosen, channel: e.target.value.trim() })} data-testid="wsp-script-channel" /></label>
          </>
        )}
        {phase && <span className="hint" data-testid="wsp-script-phase">{PHASE_WORDS[phase]}</span>}
      </div>
      {where && <div className="hint" data-testid="wsp-script-where">{where}</div>}
      {where === null && channel === PRODUCTION && (
        <div className="banner" role="alert" data-testid="wsp-script-production"><span className="mark">!</span><span>{scriptTestProductionWarning(channel, runners.find((r) => r.id === runner)?.name ?? runner)}</span></div>
      )}
      <ErrorBanner error={test.error ?? (kinds.data && !kinds.data.ok ? new Error(kinds.data.diagnostics.map(findingLine).join(' ')) : null)} />
      {test.data && <ScriptTestView test={test.data.test} run={test.data} />}
      {canWrite && (
        <form className="row" style={{ gap: 6, marginTop: 12, flexWrap: 'wrap', alignItems: 'center' }} data-testid="wsp-new-script"
          onSubmit={(e) => { e.preventDefault(); if (name !== '' && !problem && !busy) { onCreate(newScriptFiles(name, kind)); setName('') } }}>
          <input className="input mono" style={{ flex: 1, minWidth: 120 }} aria-label="New script name" placeholder="upper-name" value={name} onChange={(e) => setName(e.target.value)} data-testid="wsp-new-script-name" />
          <select className="input" aria-label="Slot" value={kind} onChange={(e) => setKind(e.target.value as ScriptKind)} data-testid="wsp-new-script-kind">
            {SCRIPT_KINDS.map((k) => <option key={k} value={k}>{k}</option>)}
          </select>
          <button type="submit" className="btn" disabled={name === '' || !!problem || busy} data-testid="wsp-new-script-go">New script</button>
          {problem && <span className="hint" data-testid="wsp-new-script-why">{problem}</span>}
        </form>
      )}
    </div>
  )
}
