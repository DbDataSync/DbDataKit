import { useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { ErrorBanner } from '@dbdatakit/shell'
import { workspaceApi, type CheckDiagnostic, type CompileResult, type LoweredResult, type SampleResult } from '../api'
import { cellText, compileActions, compileCounts, compiledWord } from '../buildCompile'
import { modelsOf } from '../buildPlan'
import { findingsKey } from './SyncSetPanel'

const findingLine = (d: CheckDiagnostic) => `${d.file ? `${d.file}${d.line ? `:${d.line}` : ''} ` : ''}${d.code}: ${d.message}`

function Findings({ diagnostics }: { diagnostics: readonly CheckDiagnostic[] }) {
  return (
    <ul style={{ listStyle: 'none', padding: 0, margin: '6px 0 0' }}>
      {diagnostics.map((d, i) => <li key={i} className={d.severity === 'error' ? 'error' : 'hint'} data-testid="wsp-compile-finding" data-code={d.code} data-severity={d.severity}>{findingLine(d)}</li>)}
    </ul>
  )
}

/** What a compile would do to the draft's `rendered/`, drawn without a query: the verdict, each file with its status, the text of the ones that change (opened on a click). */
export function CompileResultView({ result, canWrite, busy, onWrite }: { result: CompileResult; canWrite: boolean; busy: boolean; onWrite: () => void }) {
  const counts = compileCounts(result.files)
  const actions = compileActions(result)
  const [open, setOpen] = useState<string | null>(null)
  const todo = actions.write.length + actions.remove.length
  return (
    <div data-testid="wsp-compile-result" data-ok={String(result.ok)}>
      <div className={result.ok ? 'hint' : 'error'} data-testid="wsp-compile-verdict">{result.message ?? (result.ok ? 'Compiled.' : 'Not compiled.')}</div>
      <Findings diagnostics={result.diagnostics.filter((d) => d.severity !== 'info' || !result.ok)} />
      {result.ok && (
        <>
          {result.files.map((f) => (
            <div key={f.path} data-testid="wsp-compile-file" data-path={f.path} data-status={f.status}>
              <div className="grid-row" style={{ gap: 10 }}>
                {f.content !== null
                  ? <button type="button" className="btn-link mono" data-testid="wsp-compile-toggle" onClick={() => setOpen(open === f.path ? null : f.path)}>{f.path}</button>
                  : <span className="mono">{f.path}</span>}
                <span className="hint">{compiledWord(f.status)}{f.truncated ? ' (too large to show or write here)' : ''}</span>
              </div>
              {open === f.path && f.content !== null && <pre className="mono" style={{ margin: 0, overflow: 'auto', maxHeight: 260 }} data-testid="wsp-compile-text">{f.content}</pre>}
            </div>
          ))}
          <div className="row" style={{ gap: 10, marginTop: 8, alignItems: 'center', flexWrap: 'wrap' }}>
            <button type="button" className="btn btn-primary" disabled={!canWrite || busy || todo === 0} data-testid="wsp-compile-write" onClick={onWrite}>Write them into the draft</button>
            <span className="hint" data-testid="wsp-compile-counts">{counts.written} to write, {counts.removed} to remove, {counts.unchanged} as committed.</span>
            {actions.skipped.length > 0 && <span className="error" data-testid="wsp-compile-skipped">Too large to write here: {actions.skipped.join(', ')}. Run the compile where the project is a git checkout.</span>}
          </div>
        </>
      )}
    </div>
  )
}

/** The text a model is lowered and rendered to, one block per connection. */
export function LoweredView({ result }: { result: LoweredResult }) {
  return (
    <div data-testid="wsp-lowered" data-ok={String(result.ok)}>
      <Findings diagnostics={result.diagnostics} />
      {result.ok && result.files.length === 0 && <div className="hint" data-testid="wsp-lowered-none">The compiler gave no text for this model.</div>}
      {result.files.map((f) => (
        <div key={f.path} style={{ marginTop: 6 }}>
          <div className="mono hint">{f.path}</div>
          <pre className="mono" style={{ margin: 0, overflow: 'auto', maxHeight: 240 }} data-testid="wsp-lowered-text">{f.content}</pre>
        </div>
      ))}
    </div>
  )
}

/** The tables of a run on generated rows: each with its columns and rows (the count is complete, the rows are the first), or the error it stopped on. */
export function SampleView({ result }: { result: SampleResult }) {
  const tables = result.data?.tables ?? []
  return (
    <div data-testid="wsp-sample" data-ok={String(result.ok)}>
      <Findings diagnostics={result.diagnostics} />
      {tables.map((t) => (
        <div key={t.name} className="card flush" style={{ marginTop: 8 }} data-testid="wsp-sample-table" data-name={t.name} data-rows={t.row_count}>
          <div className="hint" style={{ padding: '6px 10px' }}><span className="mono">{t.name}</span> ({t.kind}, {t.row_count} row{t.row_count === 1 ? '' : 's'})</div>
          {t.error && <div className="error" style={{ padding: '0 10px 6px' }} data-testid="wsp-sample-error">{t.error}</div>}
          {t.warnings.map((w, i) => <div key={i} className="hint" style={{ padding: '0 10px' }}>{w}</div>)}
          {!t.error && (
            <div style={{ overflow: 'auto' }}>
              <table className="mono" style={{ borderCollapse: 'collapse', width: '100%' }}>
                <thead><tr>{t.columns.map((c) => <th key={c.name} style={{ textAlign: 'left', padding: '4px 10px' }} title={c.type}>{c.name}</th>)}</tr></thead>
                <tbody>{t.rows.map((row, i) => <tr key={i} data-testid="wsp-sample-row">{row.map((cell, j) => <td key={j} style={{ padding: '4px 10px' }}>{cellText(cell)}</td>)}</tr>)}</tbody>
              </table>
            </div>
          )}
        </div>
      ))}
      {!result.ok && tables.length === 0 && <div className="hint">No table ran.</div>}
    </div>
  )
}

/**
 * The Compile tab of a Build project's workspace (plan 33 SC20l): compile the saved draft on the server's copy and see what `rendered/` would become (added, changed, removed files, with the text), then write it into the draft; the lowered SQL of one model;
 * and a run of the models on generated rows, offline. None of it reads a database, and nothing changes in the draft until **Write them into the draft**.
 */
export function BuildCompilePanel({ project, id, paths, canWrite }: { project: string; id: string; paths: readonly string[]; canWrite: boolean }) {
  const client = useQueryClient()
  const models = modelsOf(paths)
  const [model, setModel] = useState('')
  const [rows, setRows] = useState('50')
  const [seed, setSeed] = useState('1')
  const queryOf = (m: string) => models.find((x) => `${x.folder}/${x.name}` === m)?.files.find((f) => f.endsWith('.sql')) ?? models.find((x) => `${x.folder}/${x.name}` === m)?.files[0]
  const compile = useMutation({ mutationFn: () => workspaceApi.compile(project, id) })
  const write = useMutation({
    mutationFn: async (result: CompileResult) => {
      const { write: files, remove } = compileActions(result)
      for (const f of files) await workspaceApi.writeFile(project, id, f.path, f.content)
      for (const path of remove) await workspaceApi.deleteFile(project, id, path)
      return files.length + remove.length
    },
    onSuccess: async () => {
      await client.invalidateQueries({ queryKey: ['workspace', 'files', project, id] })
      await client.invalidateQueries({ queryKey: ['workspace', 'changes', project, id] })
      await client.invalidateQueries({ queryKey: ['workspace', 'file', project, id] })
      await client.invalidateQueries({ queryKey: findingsKey(project, id) })
      await compile.mutateAsync()
    },
  })
  const lowered = useMutation({ mutationFn: (path: string) => workspaceApi.lowered(project, id, path) })
  const sample = useMutation({ mutationFn: () => workspaceApi.sample(project, id, { ...(model && queryOf(model) ? { models: [queryOf(model)!] } : {}), rows: Number(rows) || 50, seed: Number(seed) || 1 }) })
  return (
    <div data-testid="wsp-compile">
      <div className="row" style={{ gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
        <button type="button" className="btn btn-primary" disabled={compile.isPending} data-testid="wsp-compile-run" onClick={() => compile.mutate()}>{compile.isPending ? 'Compiling...' : 'Compile the draft'}</button>
        <span className="hint">Saved files only, on a copy: nothing in the draft changes until you write the files.</span>
      </div>
      <ErrorBanner error={compile.error ?? write.error} />
      {compile.data && <CompileResultView result={compile.data} canWrite={canWrite} busy={write.isPending} onWrite={() => write.mutate(compile.data!)} />}
      <div className="hint" style={{ margin: '16px 0 4px' }}>One model: the lowered SQL and a run on generated rows</div>
      <div className="row" style={{ gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
        <select className="input" aria-label="Model" value={model} onChange={(e) => setModel(e.target.value)} data-testid="wsp-compile-model">
          <option value="">every model</option>
          {models.map((m) => <option key={`${m.folder}/${m.name}`} value={`${m.folder}/${m.name}`}>{m.folder}/{m.name}</option>)}
        </select>
        <button type="button" className="btn" disabled={!model || lowered.isPending} data-testid="wsp-lowered-run" onClick={() => lowered.mutate(queryOf(model)!)}>Lowered SQL</button>
        <label className="row" style={{ gap: 4 }}>rows <input className="input mono" style={{ width: 70 }} value={rows} onChange={(e) => setRows(e.target.value)} data-testid="wsp-sample-rows" /></label>
        <label className="row" style={{ gap: 4 }}>seed <input className="input mono" style={{ width: 60 }} value={seed} onChange={(e) => setSeed(e.target.value)} data-testid="wsp-sample-seed" /></label>
        <button type="button" className="btn" disabled={sample.isPending} data-testid="wsp-sample-run" onClick={() => sample.mutate()}>{sample.isPending ? 'Running...' : 'Run on generated rows'}</button>
      </div>
      <ErrorBanner error={lowered.error ?? sample.error} />
      {lowered.data && <LoweredView result={lowered.data} />}
      {sample.data && <SampleView result={sample.data} />}
    </div>
  )
}
