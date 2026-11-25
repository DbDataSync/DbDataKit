import { useState } from 'react'
import { PREVIEW_TASK, previewLine, previewValue, RunAnswerStatus, useDraft, useRunAnswer, type MappingPreviewAnswer } from '@dbdatakit/workspace'
import { mappingPath } from '../model'

/** How many rows a person may ask for. */
export const PREVIEW_ROWS = [20, 50, 100] as const

/**
 * **Preview rows** of a mapping: the first rows of its source, read by the set's task `preview` on the Runner the draft was last published to, with the login its
 * channel binds to the source's slot, started through the console as the signed-in person. The screen sends the mapping's file as the draft has it, unsaved
 * edits included (the input `mapping_text`), and the task reads it with that version's replication, connections and drivers. The rows are shown here and kept nowhere: not in the draft, the Runner's run record or the console.
 * Design: docs/authoring-metadata.md#rows-and-trials-are-answered-never-kept
 */
export function MappingRowsPreview({ replication, mapping }: { replication: string; mapping: string }) {
  const draft = useDraft()
  const run = useRunAnswer<MappingPreviewAnswer>(draft.project, draft.id)
  const [rows, setRows] = useState<number>(PREVIEW_ROWS[0])
  const answer = run.answer && run.answer.replication === replication && run.answer.mapping === mapping ? run.answer : undefined
  return (
    <div className="card flush" data-testid="mapping-rows-preview">
      <div className="card-head tight">
        <span className="card-title sm">Rows</span>
        <span className="card-note" data-testid="mapping-rows-preview-version">
          {run.version
            ? `The first rows of the source, as this mapping is in the draft with its unsaved edits, read on ${run.version.channel} of ${run.version.targetName} (version ${run.version.version}) with the login that channel binds.`
            : run.loading ? 'Looking for the version this draft published…' : 'Publish a build of this draft to a Runner first: the preview reads with the connections and drivers of that version.'}
        </span>
        <span className="row spacer" style={{ gap: 6 }}>
          <select className="select" value={rows} onChange={(e) => setRows(Number(e.target.value))} aria-label="Rows to read" data-testid="mapping-rows-preview-count">
            {PREVIEW_ROWS.map((n) => <option key={n} value={n}>{n} rows</option>)}
          </select>
          <button type="button" className="btn btn-sm" disabled={!run.version || run.pending || run.running !== undefined}
            onClick={() => run.ask({ task: PREVIEW_TASK, inputs: { replication, mapping, rows: String(rows), repo_dir: 'repo', mapping_text: draft.textOf(mappingPath(replication, mapping)) ?? '' } })} data-testid="mapping-rows-preview-go">Preview rows</button>
        </span>
      </div>
      <div className="card-body">
        <RunAnswerStatus version={run.version} pending={run.pending} running={run.running} error={run.error} testId="mapping-rows-preview" />
        {answer && (
          <div data-testid="mapping-rows-preview-result" data-run={run.run?.run_id}>
            <div className="hint" data-testid="mapping-rows-preview-summary">{previewLine(answer)}</div>
            <div style={{ overflowX: 'auto' }}>
              <table className="table" data-testid="mapping-rows-preview-table">
                <thead><tr>{answer.columns.map((c, i) => <th key={i} className="mono">{c}</th>)}</tr></thead>
                <tbody>
                  {answer.rows.map((row, r) => (
                    <tr key={r} data-testid="mapping-rows-preview-row">
                      {row.map((v, i) => <td key={i} className={v === null ? 'mono dim' : 'mono'}>{previewValue(v)}</td>)}
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </div>
        )}
      </div>
    </div>
  )
}
