import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ErrorBanner } from '@dbdatakit/shell'
import { buildApi } from '../api'
import type { CommandAnswer, Diagnostic, ImportResult, ImportRow } from '../types'

/** What a listed table would do: only the ones the project does not map yet, or maps differently, can be imported. */
export const importable = (row: ImportRow) => row.status === 'new' || row.status === 'changed'

const STATUS_TEXT: Record<string, string> = {
  new: 'not mapped yet',
  changed: 'the model differs from the table',
  unchanged: 'already mapped, the same',
  stale: 'a model, but the connection shows no such table',
  invalid: 'its file does not load, so it was left alone',
}

export const statusText = (status: string) => STATUS_TEXT[status] ?? status

/** The mark in front of a diagnostic, as the editor's markers are: an error is blocking, a warning is worth reading. */
export const markOf = (d: Diagnostic) => (d.severity === 'error' ? '!' : d.severity === 'warning' ? '~' : 'i')

/**
 * The sentence for an import that gave no list: no login set for the connection (the message names the variable, never a value), a connection that cannot be read, or whatever the tool printed first. A login is never part of what comes back, so there
 * is none to echo; a driver's own message is not quoted either.
 */
export function importFailure(answer: CommandAnswer<ImportResult>): string | null {
  if (answer.document?.ok && answer.document.data?.mapped_models) return null
  const problem = answer.document?.diagnostics?.find((d) => d.severity === 'error')
  if (problem?.code === 'DDB-501') return `This connection has no read login on the server yet. ${problem.found.split('\n')[0]}`
  return problem?.found.split('\n')[0] ?? answer.document?.errors?.[0] ?? (answer.text ? answer.text.split('\n')[0] : null) ?? 'The tables could not be read from this connection.'
}

export interface ImportViewProps {
  connections: string[]
  connection: string
  listing: boolean
  rows: ImportRow[] | null
  skipped: { object: string; reason: string }[]
  diagnostics: Diagnostic[]
  selected: string[]
  importing: boolean
  failure: string | null
  written: string[] | null
  onConnection: (name: string) => void
  onList: () => void
  onToggle: (name: string) => void
  onImport: () => void
  onOpen: (file: string) => void
  onCancel: () => void
}

/** Import from a connection: choose one, list what it has, tick the tables, and write them as mapped models in the workspace. What the tool says about a table is listed as markers, like the compiler's. */
export function ImportView(p: ImportViewProps) {
  const shown = p.diagnostics.filter((d) => d.severity !== 'note')
  return (
    <div className="card" style={{ marginBottom: 14 }} data-testid="build-import">
      <div className="card-body">
        <div className="hint" style={{ marginBottom: 8 }}>Reads the tables and views of a connection with the login the server holds for it, and writes the ones you tick as mapped models in your workspace. The login is never shown here.</div>
        <div className="row" style={{ gap: 12, flexWrap: 'wrap' }}>
          <select value={p.connection} onChange={(e) => p.onConnection(e.target.value)} aria-label="Connection" data-testid="build-import-connection">
            <option value="">Choose a connection…</option>
            {p.connections.map((c) => <option key={c} value={c}>{c}</option>)}
          </select>
          <button type="button" className="btn" disabled={p.connection === '' || p.listing} onClick={p.onList} data-testid="build-import-list">{p.listing ? 'Reading…' : 'List its tables'}</button>
          <button type="button" className="btn" onClick={p.onCancel} data-testid="build-import-cancel">Close</button>
        </div>
        {p.failure && <div className="banner error" role="alert" style={{ marginTop: 10 }} data-testid="build-import-failed"><span className="mark">!</span><span>{p.failure}</span></div>}
        {p.rows !== null && p.rows.length === 0 && <div className="empty" data-testid="build-import-none">This connection shows no table or view to map.</div>}
        {p.rows !== null && p.rows.length > 0 && (
          <div className="card flush" style={{ marginTop: 10 }} data-testid="build-import-tables">
            <div className="grid-head" style={{ gridTemplateColumns: '0.4fr 2fr 1fr 2.5fr', gap: 14 }}><span /><span>Table</span><span>Columns</span><span>State</span></div>
            {p.rows.map((row) => (
              <label key={row.name} className="grid-row" style={{ gridTemplateColumns: '0.4fr 2fr 1fr 2.5fr', gap: 14, alignItems: 'center' }} data-testid={`build-import-row-${row.name}`}>
                <input type="checkbox" disabled={!importable(row)} checked={p.selected.includes(row.name)} onChange={() => p.onToggle(row.name)} aria-label={`Import ${row.name}`} data-testid={`build-import-check-${row.name}`} />
                <span className="mono">{row.name} <span className="hint">({row.object_kind})</span></span>
                <span>{row.columns?.length ?? '—'}</span>
                <span>{statusText(row.status)}</span>
              </label>
            ))}
          </div>
        )}
        {p.skipped.length > 0 && <div className="hint" style={{ marginTop: 6 }} data-testid="build-import-skipped">Left out: {p.skipped.map((s) => `${s.object} (${s.reason})`).join('; ')}</div>}
        {shown.length > 0 && (
          <div className="card flush" style={{ marginTop: 10 }} data-testid="build-import-diagnostics">
            {shown.map((d, i) => (
              <button key={`${d.code}-${i}`} type="button" className="grid-row" style={{ width: '100%', textAlign: 'left', gridTemplateColumns: '0.3fr 0.8fr 2fr 3fr', gap: 14 }} onClick={() => p.onOpen(d.location.file)} data-testid="build-import-diagnostic" data-severity={d.severity}>
                <span className="mark" aria-label={d.severity}>{markOf(d)}</span>
                <span className="mono">{d.code}</span>
                <span className="mono">{d.location.file}</span>
                <span>{d.found.split('\n')[0]}</span>
              </button>
            ))}
          </div>
        )}
        {p.rows !== null && p.rows.length > 0 && (
          <div className="row" style={{ gap: 12, marginTop: 10 }}>
            <button type="button" className="btn btn-primary" disabled={p.selected.length === 0 || p.importing} onClick={p.onImport} data-testid="build-import-write">{p.importing ? 'Writing…' : `Import ${p.selected.length} table${p.selected.length === 1 ? '' : 's'}`}</button>
            <span className="hint">The models are written in your workspace; commit and merge them like any change.</span>
          </div>
        )}
        {p.written && <div className="hint" style={{ marginTop: 8 }} data-testid="build-import-written">Wrote {p.written.join(', ')}.</div>}
      </div>
    </div>
  )
}

/** "Import from a connection" in the Edit tab. */
export function ImportPanel({ project, workspace, onWritten, onOpen, onCancel }: { project: string; workspace: string; onWritten: () => void; onOpen: (file: string) => void; onCancel: () => void }) {
  const client = useQueryClient()
  const [connection, setConnection] = useState('')
  const [listed, setListed] = useState<ImportResult | null>(null)
  const [diagnostics, setDiagnostics] = useState<Diagnostic[]>([])
  const [failure, setFailure] = useState<string | null>(null)
  const [selected, setSelected] = useState<string[]>([])
  const [written, setWritten] = useState<string[] | null>(null)
  const connections = useQuery({ queryKey: ['build', project, workspace, 'connections'], queryFn: () => buildApi.connections(project, workspace) })

  const take = (answer: CommandAnswer<ImportResult>) => {
    const problem = importFailure(answer)
    setFailure(problem)
    setDiagnostics(answer.document?.diagnostics ?? [])
    return problem === null
  }
  const list = useMutation({
    mutationFn: () => buildApi.importPreview(project, workspace, connection),
    onSuccess: (answer) => {
      if (!take(answer)) { setListed(null); setSelected([]); return }
      setListed(answer.document!.data)
      setSelected([])
    },
  })
  const write = useMutation({
    mutationFn: () => buildApi.importTables(project, workspace, connection, selected),
    onSuccess: async (answer) => {
      if (!take(answer)) return
      const files = answer.document!.data.written ?? []
      setWritten(files)
      setSelected([])
      await client.invalidateQueries({ queryKey: ['build', project, workspace] })
      onWritten()
      // what was written is mapped now: the list shows it as such
      list.mutate()
    },
  })
  return (
    <>
      <ImportView
        connections={connections.data ?? []}
        connection={connection}
        listing={list.isPending}
        rows={listed?.mapped_models ?? null}
        skipped={listed?.skipped ?? []}
        diagnostics={diagnostics}
        selected={selected}
        importing={write.isPending}
        failure={failure}
        written={written}
        onConnection={(c) => { setConnection(c); setListed(null); setSelected([]); setFailure(null); setDiagnostics([]); setWritten(null) }}
        onList={() => { setWritten(null); list.mutate() }}
        onToggle={(name) => setSelected((now) => (now.includes(name) ? now.filter((n) => n !== name) : [...now, name]))}
        onImport={() => write.mutate()}
        onOpen={onOpen}
        onCancel={onCancel}
      />
      <ErrorBanner error={list.error ?? write.error} />
    </>
  )
}
