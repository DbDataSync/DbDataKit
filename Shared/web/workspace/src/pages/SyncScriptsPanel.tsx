import { useState } from 'react'
import { useMutation, useQuery } from '@tanstack/react-query'
import { ErrorBanner } from '@dbdatakit/shell'
import { workspaceApi, type CheckDiagnostic, type ScriptTest, type ToolAnswer } from '../api'
import { syncNameProblem } from '../syncSchemas'
import { SCRIPT_KINDS, newScriptFiles, scriptCodePath, scriptManifestPath, scriptsOf, type ScriptKind } from '../syncScripts'

const findingLine = (d: CheckDiagnostic) => `${d.file ? `${d.file}${d.line ? `:${d.line}` : ''} ` : ''}${d.code}: ${d.message}`

/** What a script did against generated input: the verdict (compiled and ran, or the compiler's or the script's own words), where the input came from, each case and the log lines a transform wrote. */
export function ScriptTestView({ answer }: { answer: ToolAnswer<ScriptTest> }) {
  const test = answer.data
  return (
    <div data-testid="wsp-script-result" data-ok={String(answer.ok && test?.ok === true)}>
      {answer.diagnostics.map((d, i) => <div key={i} className="error" data-testid="wsp-script-finding">{findingLine(d)}</div>)}
      {test && (
        <>
          <div className={test.ok ? 'hint' : 'error'} data-testid="wsp-script-verdict">
            {test.ok ? `${test.name} compiled and ran: ${test.source}.` : (test.error ?? `${test.name} did not run.`)}
          </div>
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
        </>
      )}
    </div>
  )
}

/**
 * The Scripts tab of a Sync project's workspace (plan 33 SC22k): the scripts of `config/scripts/` with the slot each fills, a new script from the skeleton of a slot, and the test of one on the saved draft: Sync's own compiler and its generated sample input,
 * run in a child process on a copy, offline. Nothing in the draft changes; the files are edited like any other.
 */
export function SyncScriptsPanel({ project, id, paths, canWrite, busy, onOpen, onCreate }: { project: string; id: string; paths: readonly string[]; canWrite: boolean; busy: boolean; onOpen: (path: string) => void; onCreate: (files: { path: string; text: string }[]) => void }) {
  const names = scriptsOf(paths)
  const kinds = useQuery({ queryKey: ['workspace', 'scripts', project, id, names.join(',')], queryFn: () => workspaceApi.scripts(project, id), retry: false })
  const kindOf = (name: string) => kinds.data?.data?.scripts.find((s) => s.name === name)
  const [name, setName] = useState('')
  const [kind, setKind] = useState<ScriptKind>('sqlColumnExpression')
  const [rows, setRows] = useState('5')
  const [tested, setTested] = useState<string | null>(null)
  const test = useMutation({ mutationFn: (script: string) => { setTested(script); return workspaceApi.scriptTest(project, id, script, Math.min(50, Math.max(1, Number(rows) || 5))) } })
  const problem = syncNameProblem(name, paths, scriptManifestPath)
  return (
    <div data-testid="wsp-scripts">
      <div className="hint" style={{ marginBottom: 6 }}>Scripts (C#, in <span className="mono">config/scripts/</span>): a test compiles the saved script and runs it against generated sample input on a copy, offline.</div>
      {names.length === 0 && <div className="hint" data-testid="wsp-scripts-none">No script yet.</div>}
      {names.map((n) => {
        const entry = kindOf(n)
        return (
          <div key={n} className="grid-row" style={{ gap: 8, flexWrap: 'wrap' }} data-testid="wsp-script" data-name={n}>
            <button type="button" className="btn-link mono" data-testid="wsp-script-open-code" onClick={() => onOpen(scriptCodePath(n))}>{n}</button>
            <button type="button" className="btn-link mono hint" data-testid="wsp-script-open-manifest" onClick={() => onOpen(scriptManifestPath(n))}>{n}.yaml</button>
            <span className="hint" data-testid="wsp-script-kind">{entry?.error ? `unreadable: ${entry.error}` : entry ? `${entry.kind}, ${entry.language}` : ''}</span>
            <button type="button" className="btn" disabled={!canWrite || test.isPending} title={!canWrite ? 'Testing runs the script: it needs the author role.' : undefined} data-testid="wsp-script-test" onClick={() => test.mutate(n)}>{test.isPending && tested === n ? 'Testing...' : 'Test'}</button>
          </div>
        )
      })}
      <div className="row" style={{ gap: 8, alignItems: 'center', margin: '8px 0', flexWrap: 'wrap' }}>
        <label className="row" style={{ gap: 4 }}>sample rows <input className="input mono" style={{ width: 60 }} value={rows} onChange={(e) => setRows(e.target.value)} data-testid="wsp-script-rows" /></label>
      </div>
      <ErrorBanner error={test.error ?? (kinds.data && !kinds.data.ok ? new Error(kinds.data.diagnostics.map(findingLine).join(' ')) : null)} />
      {test.data && <ScriptTestView answer={test.data} />}
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
