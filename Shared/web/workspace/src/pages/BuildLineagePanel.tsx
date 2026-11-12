import { useState } from 'react'
import { useMutation } from '@tanstack/react-query'
import { ErrorBanner } from '@dbdatakit/shell'
import { workspaceApi, type CheckDiagnostic } from '../api'
import { followableColumns, readLineage, sourceLine, sourcesOf, stepLine, type LineageRead } from '../buildLineage'

const findingLine = (d: CheckDiagnostic) => `${d.file ? `${d.file}${d.line ? `:${d.line}` : ''} ` : ''}${d.code}: ${d.message}`

/** The column lineage the tool computed, drawn without a query: the columns that can be followed, what the chosen one is made from (the tool's own edges) and, once followed, what it comes from and what is built from it. */
export function LineageView({ read, diagnostics, chosen, onChoose }: { read: LineageRead; diagnostics: readonly CheckDiagnostic[]; chosen: string; onChoose: (column: string) => void }) {
  const columns = followableColumns(read.columnEdges)
  // a model is `schema.name` and the tool splits `model.column` at the last dot
  const dot = chosen.lastIndexOf('.')
  const [model, column] = dot < 0 ? [chosen, ''] : [chosen.slice(0, dot), chosen.slice(dot + 1)]
  return (
    <div data-testid="wsp-lineage-result">
      {diagnostics.map((d, i) => <div key={i} className={d.severity === 'error' ? 'error' : 'hint'} data-testid="wsp-lineage-finding">{findingLine(d)}</div>)}
      {read.dropped > 0 && <div className="hint" data-testid="wsp-lineage-dropped">{read.dropped} entr{read.dropped === 1 ? 'y' : 'ies'} of the tool's answer had a shape this screen does not know and {read.dropped === 1 ? 'was' : 'were'} left out.</div>}
      {columns.length === 0
        ? <div className="hint" data-testid="wsp-lineage-none">No built model has columns to follow: a model needs its columns declared and a query that reads declared tables.</div>
        : (
          <>
            <div className="row" style={{ gap: 8, alignItems: 'center', flexWrap: 'wrap', marginTop: 6 }}>
              <select className="input mono" aria-label="Column" value={chosen} onChange={(e) => onChoose(e.target.value)} data-testid="wsp-lineage-column">
                <option value="">choose a column ({columns.length})</option>
                {columns.map((c) => <option key={c} value={c}>{c}</option>)}
              </select>
            </div>
            {chosen !== '' && (
              <div style={{ marginTop: 6 }} data-testid="wsp-lineage-made-of">
                <div className="hint">{chosen} is made of</div>
                {sourcesOf(read.columnEdges, model, column).map((e, i) => <div key={i} className="mono" data-testid="wsp-lineage-source">{sourceLine(e)}</div>)}
              </div>
            )}
            {read.lineage && read.lineage.column.toLowerCase() === chosen.toLowerCase() && (
              <div style={{ marginTop: 6 }} data-testid="wsp-lineage-followed">
                <div className="hint">Comes from</div>
                {read.lineage.upstream.length === 0
                  ? <div data-testid="wsp-lineage-up-none">nothing (a column of a mapped model, or made of constants)</div>
                  : read.lineage.upstream.map((s, i) => <div key={i} className="mono" data-testid="wsp-lineage-up">{stepLine(s)}</div>)}
                <div className="hint" style={{ marginTop: 6 }}>Feeds</div>
                {read.lineage.downstream.length === 0
                  ? <div data-testid="wsp-lineage-down-none">nothing</div>
                  : read.lineage.downstream.map((s, i) => <div key={i} className="mono" data-testid="wsp-lineage-down">{stepLine(s)}</div>)}
              </div>
            )}
          </>
        )}
    </div>
  )
}

/**
 * Column lineage in the Graph view of a Build draft (plan 33 SC20h): `dbdatabuild project show graph --columns` run offline on the saved draft by the Workspace (a copy is not needed, it reads only), and the chosen column followed with `--column`, so the lineage is the product's
 * own computation and not a second one in the browser. It reads the models as saved, not as typed since.
 */
export function BuildLineagePanel({ project, id }: { project: string; id: string }) {
  const [chosen, setChosen] = useState('')
  const all = useMutation({ mutationFn: () => workspaceApi.lineage(project, id) })
  const follow = useMutation({ mutationFn: (column: string) => workspaceApi.lineage(project, id, column) })
  const answer = follow.data && chosen !== '' ? follow.data : all.data
  const read = answer?.data ? readLineage(answer.data) : null
  const base = all.data?.data ? readLineage(all.data.data) : null
  return (
    <div style={{ marginTop: 16 }} data-testid="wsp-lineage">
      <div className="row" style={{ gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
        <span className="hint">Column lineage</span>
        <button type="button" className="btn" disabled={all.isPending} data-testid="wsp-lineage-run" onClick={() => { setChosen(''); follow.reset(); all.mutate() }}>{all.isPending ? 'Reading...' : 'Compute from the saved models'}</button>
      </div>
      <ErrorBanner error={all.error ?? follow.error} />
      {all.data && !all.data.ok && <LineageView read={{ columnEdges: [], lineage: null, dropped: 0 }} diagnostics={all.data.diagnostics} chosen="" onChoose={() => {}} />}
      {all.data?.ok && base && read && (
        <LineageView read={{ ...base, lineage: follow.data?.data ? readLineage(follow.data.data).lineage : null }} diagnostics={all.data.diagnostics.filter((d) => d.severity !== 'info')} chosen={chosen}
          onChoose={(c) => { setChosen(c); if (c !== '') follow.mutate(c); else follow.reset() }} />
      )}
    </div>
  )
}
