import { useState } from 'react'
import type { ExportFormat, ExportedFile, ViewKind, ViewsClient, ViewsDoc, ParameterValues } from '@dbdatakit/visuals'

/** Which downloads a widget has: its rows as CSV or xlsx when it has rows (a table, a matrix or a chart), and a chart's picture as SVG. A metric, text, image, control, embed or custom widget has none. */
export function widgetDownloads(type: string): ExportFormat[] {
  return type === 'chart' ? ['csv', 'xlsx', 'svg'] : type === 'table' || type === 'matrix' ? ['csv', 'xlsx'] : []
}

/** Hands a file the browser keeps (a link to an object address, clicked, then released). */
export function saveFile(file: ExportedFile): void {
  const url = URL.createObjectURL(new Blob([file.bytes as BlobPart], { type: file.contentType }))
  const a = document.createElement('a')
  a.href = url
  a.download = file.fileName
  document.body.appendChild(a)
  a.click()
  a.remove()
  setTimeout(() => URL.revokeObjectURL(url), 10_000)
}

/**
 * The downloads of a published board (plan 32 DBR17): the PDF of the layout shown, the xlsx of all its tables, matrices and charts, and one widget's rows (CSV, xlsx) or a chart's picture (SVG). They are what the server makes as the viewer;
 * a source with no server (the fixtures) has none, and the buttons are not drawn. A refusal (no browser for the PDF, a widget with no rows) is shown in the server's words.
 */
export function ExportButtons({ client, kind, name, document, layout, parameters, save = saveFile }: {
  client: ViewsClient; kind: ViewKind; name: string; document: ViewsDoc; layout: string | null; parameters: ParameterValues; save?: (file: ExportedFile) => void
}) {
  const [busy, setBusy] = useState<string | null>(null)
  const [problem, setProblem] = useState<string | null>(null)
  const widgets = Object.entries(document.widgets ?? {}).filter(([, w]) => widgetDownloads((w as { type: string }).type).length > 0)
  const [widget, setWidget] = useState('')
  if (!client.exportFile) return null
  const run = async (key: string, format: ExportFormat, only?: string) => {
    setBusy(key)
    setProblem(null)
    try {
      save(await client.exportFile!({ kind, name }, format, { ...(layout ? { layout } : {}), ...(only ? { widget: only } : {}), parameters }))
    } catch (e) {
      setProblem(e instanceof Error ? e.message : String(e))
    } finally { setBusy(null) }
  }
  const chosen = widgets.find(([id]) => id === widget)
  const formats = chosen ? widgetDownloads((chosen[1] as { type: string }).type) : []
  return (
    <span className="row" style={{ gap: 6, alignItems: 'center', flexWrap: 'wrap' }} data-testid="views-downloads">
      <button type="button" className="btn btn-sm" disabled={busy !== null} data-testid="views-download-pdf" onClick={() => void run('pdf', 'pdf')}>{busy === 'pdf' ? 'Making PDF…' : 'PDF'}</button>
      <button type="button" className="btn btn-sm" disabled={busy !== null} data-testid="views-download-xlsx" title="Every table, matrix and chart of the layout, one sheet each" onClick={() => void run('xlsx', 'xlsx')}>{busy === 'xlsx' ? 'Making Excel…' : 'Excel'}</button>
      {widgets.length > 0 && (
        <>
          <select aria-label="A widget to download" data-testid="views-download-widget" value={widget} onChange={(e) => setWidget(e.target.value)}>
            <option value="">A widget…</option>
            {widgets.map(([id, w]) => <option key={id} value={id}>{(w as { title?: string }).title ?? id}</option>)}
          </select>
          {formats.map((f) => <button key={f} type="button" className="btn btn-sm" disabled={busy !== null} data-testid={`views-download-widget-${f}`} onClick={() => void run(`w-${f}`, f, widget)}>{f.toUpperCase()}</button>)}
        </>
      )}
      {problem && <span className="error" role="alert" data-testid="views-download-error">{problem}</span>}
    </span>
  )
}
