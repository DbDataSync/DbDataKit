import { useQuery } from '@tanstack/react-query'
import { ErrorBanner } from '@dbdatakit/shell'
import { buildApi } from '../api'
import type { SampleTable } from '../types'

/** Whether a file is a model's query: the files the compiler lowers and the sampler runs. */
export const isModelQuery = (path: string) => path.startsWith('models/') && path.endsWith('.sql')

/** The name a rendered file is shown under: the connection (or `lowered`) it belongs to, then the file. */
export function renderedLabel(path: string): string {
  const parts = path.replace(/^rendered\//, '').split('/')
  return parts.length >= 3 ? `${parts[0]} · ${parts[parts.length - 1]}` : parts.join(' · ')
}

/**
 * What a model file comes to (plan 13, W06): the text the compiler lowers it to and renders for each connection, and a few rows it returns on generated data. Both are asked of the draft as it was last saved
 * (`version` is the hash of that), so they change when the file is saved, and nothing is written or connected to.
 */
export function PreviewPanel({ project, workspace, path, version }: { project: string; workspace: string; path: string; version: string }) {
  const rendered = useQuery({ queryKey: ['build', project, workspace, 'rendered', path, version], queryFn: () => buildApi.rendered(project, workspace, path) })
  const sample = useQuery({ queryKey: ['build', project, workspace, 'sample', path, version], queryFn: () => buildApi.sampleOf(project, workspace, path) })
  const files = rendered.data?.document?.data?.files ?? []
  const table: SampleTable | undefined = sample.data?.document?.data?.tables?.[0]

  return (
    <div style={{ marginTop: 14 }} data-testid="build-preview">
      <div className="card" style={{ marginBottom: 14 }}>
        <div className="hint" style={{ marginBottom: 6 }}>Lowered and rendered text</div>
        {rendered.isLoading && <div className="empty">Compiling…</div>}
        {!rendered.isLoading && files.length === 0 && <div className="empty" data-testid="build-preview-no-text">The compiler gave no text for this file; see the problems below.</div>}
        {files.map((f) => (
          <div key={f.path} style={{ marginBottom: 10 }}>
            <div className="mono hint">{renderedLabel(f.path)}</div>
            <pre className="mono" style={{ margin: 0, overflow: 'auto', maxHeight: 220 }} data-testid="build-preview-text">{f.content}</pre>
          </div>
        ))}
      </div>
      <div className="card flush" data-testid="build-preview-rows">
        <div className="hint" style={{ padding: '8px 12px' }}>Sample rows (generated data, run offline)</div>
        {sample.isLoading && <div className="empty">Running…</div>}
        {!sample.isLoading && table === undefined && <div className="empty">No rows: the model did not run.</div>}
        {table?.error && <div className="empty" data-testid="build-preview-rows-error">{table.error}</div>}
        {table && !table.error && (
          <div style={{ overflow: 'auto' }}>
            <table className="mono" style={{ borderCollapse: 'collapse', width: '100%' }}>
              <thead><tr>{table.columns.map((c) => <th key={c.name} style={{ textAlign: 'left', padding: '4px 10px' }} title={c.type}>{c.name}</th>)}</tr></thead>
              <tbody>
                {table.rows.map((row, i) => (
                  <tr key={i} data-testid="build-preview-row">{row.map((cell, j) => <td key={j} style={{ padding: '4px 10px' }}>{cell ?? 'null'}</td>)}</tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
      <ErrorBanner error={rendered.error ?? sample.error} />
    </div>
  )
}
