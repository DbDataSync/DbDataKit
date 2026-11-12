import { useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { ErrorBanner } from '@dbdatakit/shell'
import { workspaceApi, type CheckDiagnostic } from '../api'
import { allTablesOf, leftOut, patternProblem, patternsOf, readImport, statusWord, writableFiles, type ImportRead } from '../buildImport'
import type { CacheConnection } from '../catalogCache'
import { findingsKey } from './SyncSetPanel'

const findingLine = (d: CheckDiagnostic) => `${d.file ? `${d.file}${d.line ? `:${d.line}` : ''} ` : ''}${d.code}: ${d.message}`

/** The mapped models the tool made of the cache, drawn without a query: each with its status, the changes against the committed descriptor, the columns the cache gave a type the mapping cannot hold (left out, with the tool's reason), and a checkbox for each file it wrote. */
export function ImportResultView({ read, diagnostics, chosen, onChoose }: { read: ImportRead; diagnostics: readonly CheckDiagnostic[]; chosen: ReadonlySet<string>; onChoose: (path: string, on: boolean) => void }) {
  const files = new Map(writableFiles(read).map((f) => [f.path, f]))
  return (
    <div data-testid="wsp-import-result">
      {diagnostics.map((d, i) => <div key={i} className={d.severity === 'error' ? 'error' : 'hint'} data-testid="wsp-import-finding">{findingLine(d)}</div>)}
      {read.dropped > 0 && <div className="hint" data-testid="wsp-import-dropped">{read.dropped} entr{read.dropped === 1 ? 'y' : 'ies'} of the tool's answer had a shape this screen does not know and {read.dropped === 1 ? 'was' : 'were'} left out.</div>}
      {read.notes.map((n, i) => <div key={i} className="hint" data-testid="wsp-import-note">Note: {n}.</div>)}
      {read.models.length === 0 && <div className="hint" data-testid="wsp-import-none">No cached table matched.</div>}
      {read.models.map((m) => {
        const file = m.file !== null ? files.get(m.file) : undefined
        return (
          <div key={m.name} className="card flush" style={{ marginTop: 8, padding: '6px 10px' }} data-testid="wsp-import-model" data-name={m.name} data-status={m.status}>
            <div className="row" style={{ gap: 10, alignItems: 'baseline', flexWrap: 'wrap' }}>
              {file && <input type="checkbox" aria-label={`Write ${file.path}`} checked={chosen.has(file.path)} onChange={(e) => onChoose(file.path, e.target.checked)} data-testid="wsp-import-choose" />}
              <span className="mono">{m.name}</span>
              <span className="hint">{m.objectKind}, {m.columns.length} {m.columns.length === 1 ? 'column' : 'columns'}{m.grain.length > 0 ? `, grain ${m.grain.join(', ')}` : ', no key in the cache'}</span>
              <span className={m.status === 'new' || m.status === 'changed' ? '' : 'hint'} data-testid="wsp-import-status">{statusWord(m.status)}</span>
              {m.file && <span className="mono hint">{m.file}</span>}
            </div>
            {m.changes.map((c, i) => <div key={i} className="hint" data-testid="wsp-import-change">{c.kind}{c.column ? ` ${c.column}` : ''}: {c.detail}</div>)}
            {leftOut(m).map((c) => <div key={c.name} className="error" data-testid="wsp-import-left-out">{c.name} ({c.nativeType}) is left out: {c.reason ?? 'no logical type'}</div>)}
            {m.columns.filter((c) => c.logicalType !== null && c.fit !== 'exact').map((c) => <div key={c.name} className="hint" data-testid="wsp-import-fit">{c.name}: {c.nativeType} as {c.logicalType} ({c.fit}{c.reason ? `: ${c.reason}` : ''})</div>)}
          </div>
        )
      })}
      {read.skipped.map((s) => <div key={s.object} className="hint" data-testid="wsp-import-skipped">{s.object} skipped: {s.reason}</div>)}
    </div>
  )
}

/**
 * **Write model files from the cached tables** (plan 33 SC20k; Build's `ImportPanel`): `dbdatabuild project import --from-cache` run by the Workspace on a copy of the draft makes the mapped models, with the product's own type mapping and its merge with the descriptors the project has;
 * what it would write is listed with its status, and the files chosen are written into the draft through the file routes. The cache has columns, types, nullability and the primary key and nothing else: no index and no foreign key, which the tool says and a committed descriptor keeps.
 */
export function BuildImportPanel({ project, id, connections, canWrite }: { project: string; id: string; connections: Readonly<Record<string, CacheConnection>>; canWrite: boolean }) {
  const names = Object.keys(connections)
  const client = useQueryClient()
  const [connection, setConnection] = useState('')
  const [tables, setTables] = useState('')
  const [chosen, setChosen] = useState<ReadonlySet<string>>(new Set())
  const [written, setWritten] = useState<string[] | null>(null)
  const current = connection !== '' && names.includes(connection) ? connection : (names[0] ?? '')
  const patterns = patternsOf(tables)
  const problem = patterns.map(patternProblem).find((p) => p !== null) ?? null
  const preview = useMutation({
    mutationFn: () => workspaceApi.importPreview(project, id, current, patterns),
    onSuccess: (answer) => {
      setWritten(null)
      const read = answer.data ? readImport(answer.data) : null
      setChosen(new Set(read ? writableFiles(read).map((f) => f.path) : []))
    },
  })
  const read = preview.data?.data ? readImport(preview.data.data) : null
  const write = useMutation({
    mutationFn: async () => {
      const files = writableFiles(read!).filter((f) => chosen.has(f.path))
      for (const f of files) await workspaceApi.writeFile(project, id, f.path, f.content)
      return files.map((f) => f.path)
    },
    onSuccess: async (paths) => {
      setWritten(paths)
      await client.invalidateQueries({ queryKey: ['workspace', 'files', project, id] })
      await client.invalidateQueries({ queryKey: ['workspace', 'changes', project, id] })
      await client.invalidateQueries({ queryKey: ['workspace', 'file', project, id] })
      await client.invalidateQueries({ queryKey: findingsKey(project, id) })
      preview.mutate()
    },
  })
  return (
    <div style={{ marginTop: 16 }} data-testid="wsp-import">
      <div className="hint" style={{ marginBottom: 4 }}>Write model files from the cached tables: the tool's own import, offline, on a copy. The cache has no index and no foreign key, so a new descriptor has none.</div>
      <form className="row" style={{ gap: 8, alignItems: 'center', flexWrap: 'wrap' }} onSubmit={(e) => { e.preventDefault(); if (!problem && current !== '') preview.mutate() }}>
        <select className="input mono" aria-label="Connection" value={current} onChange={(e) => setConnection(e.target.value)} data-testid="wsp-import-connection">
          {names.map((n) => <option key={n} value={n}>{n}</option>)}
        </select>
        <input className="input mono" style={{ flex: 1, minWidth: 160 }} aria-label="Tables" placeholder="src.* sales.orders (blank: refresh the descriptors the project has)" value={tables} onChange={(e) => setTables(e.target.value)} data-testid="wsp-import-tables" />
        <button type="button" className="btn-link" disabled={current === ''} data-testid="wsp-import-all" onClick={() => setTables(allTablesOf(connections[current]?.tables ?? []).join(' '))}>all cached tables</button>
        <button type="submit" className="btn" disabled={!!problem || current === '' || preview.isPending} data-testid="wsp-import-run">{preview.isPending ? 'Reading...' : 'Preview the models'}</button>
        {problem && <span className="hint" data-testid="wsp-import-why">{problem}</span>}
      </form>
      <ErrorBanner error={preview.error ?? write.error} />
      {preview.data && read && <ImportResultView read={read} diagnostics={preview.data.diagnostics.filter((d) => d.severity !== 'info' || !preview.data!.ok)} chosen={chosen} onChoose={(p, on) => setChosen((c) => { const n = new Set(c); if (on) n.add(p); else n.delete(p); return n })} />}
      {preview.data && !read && preview.data.diagnostics.map((d, i) => <div key={i} className="error" data-testid="wsp-import-finding">{findingLine(d)}</div>)}
      {read && writableFiles(read).length > 0 && (
        <div className="row" style={{ gap: 10, marginTop: 8, alignItems: 'center', flexWrap: 'wrap' }}>
          <button type="button" className="btn btn-primary" disabled={!canWrite || write.isPending || chosen.size === 0} data-testid="wsp-import-write" onClick={() => write.mutate()}>{write.isPending ? 'Writing...' : `Write ${chosen.size} file${chosen.size === 1 ? '' : 's'} into the draft`}</button>
          {written && <span className="hint" data-testid="wsp-import-written">Wrote {written.length} file{written.length === 1 ? '' : 's'}: {written.join(', ')}.</span>}
        </div>
      )}
    </div>
  )
}
