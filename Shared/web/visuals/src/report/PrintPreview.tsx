import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import type { ExportedFile, ViewsClient } from '../client/types'
import type { ViewKind } from '../views/document'
import type { ParameterValues } from '../views/model'

/**
 * The print preview of a report (plan 32 DBR27): the PDF the exporter draws of the **published** document (`ViewsClient.exportPdf`), shown in the page as the browser's own PDF viewer shows it, with the page count the exporter
 * reported. It is the exact print (the paged frame's guides are an estimate), so it is of what is published and not of an unsaved draft; a host that holds a draft says so (`previewUnavailable`).
 */
export function PrintPreviewDialog({ file, onClose }: { file: ExportedFile; onClose: () => void }) {
  const url = useMemo(() => {
    if (typeof URL === 'undefined' || typeof URL.createObjectURL !== 'function') return null
    return URL.createObjectURL(new Blob([file.bytes as BlobPart], { type: file.contentType }))
  }, [file])
  useEffect(() => () => { if (url) URL.revokeObjectURL(url) }, [url])
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose() }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [onClose])
  return (
    <div className="vz-print-preview" role="dialog" aria-modal="true" aria-label="Print preview" data-testid="vz-print-preview" data-pages={file.pages ?? undefined} data-bytes={file.bytes.length}>
      <div className="vz-print-preview-bar">
        <strong data-testid="vz-print-preview-pages">{file.pages === null ? 'Print preview' : `${file.pages} page${file.pages === 1 ? '' : 's'}`}</strong>
        <span className="vz-sr"> as the exporter drew the published report</span>
        {url ? <a className="vz-button" href={url} download={file.fileName} data-testid="vz-print-preview-download">Download</a> : null}
        <button type="button" className="vz-button" data-testid="vz-print-preview-close" onClick={onClose}>Close</button>
      </div>
      {url ? <iframe className="vz-print-preview-frame" title={`Print preview of ${file.fileName}`} src={url} data-testid="vz-print-preview-frame" /> : <p className="vz-state">This browser cannot show the file here.</p>}
    </div>
  )
}

export interface PrintPreview {
  /** What the paged frame's Print preview button does; absent when the client has no exporter. */
  onPrintPreview?: () => void
  /** Why the button is disabled, when it is. */
  unavailable?: string
  /** The dialog (or the refusal), to render beside the frame. */
  element: ReactNode
}

/**
 * The state of a print preview over a client's exporter: a click asks for the PDF of the layout in play with the parameters in play, a refusal (the host has no browser, a widget failed, a limit) is shown in the module's own
 * words, and the file is shown in `PrintPreviewDialog` until it is closed. A client with no `exportPdf` (the fixtures) gives no handler and the reason.
 */
export function usePrintPreview(args: { client: ViewsClient; kind: ViewKind; name: string; layout: string | null; parameters: ParameterValues; unavailable?: string }): PrintPreview {
  const { client, kind, name, layout, parameters } = args
  const [file, setFile] = useState<ExportedFile | null>(null)
  const [problem, setProblem] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const live = useRef({ client, kind, name, layout, parameters })
  live.current = { client, kind, name, layout, parameters }
  const start = useCallback(() => {
    const l = live.current
    if (!l.client.exportPdf) return
    setBusy(true)
    setProblem(null)
    l.client.exportPdf({ kind: l.kind, name: l.name }, { ...(l.layout ? { layout: l.layout } : {}), parameters: l.parameters }).then(
      (made) => { setFile(made); setBusy(false) },
      (e: unknown) => { setProblem(e instanceof Error ? e.message : String(e)); setBusy(false) },
    )
  }, [])
  const close = useCallback(() => setFile(null), [])
  const element = (
    <>
      {busy ? <p className="vz-state" role="status" data-testid="vz-print-preview-busy">Drawing the PDF…</p> : null}
      {problem ? <p className="vz-state vz-error" role="alert" data-testid="vz-print-preview-refused">{problem} <button type="button" className="vz-button" onClick={() => setProblem(null)}>Dismiss</button></p> : null}
      {file ? <PrintPreviewDialog file={file} onClose={close} /> : null}
    </>
  )
  if (!client.exportPdf) return { element, unavailable: args.unavailable ?? 'The source of this document has no exporter: the print preview is the PDF of a published report.' }
  return { onPrintPreview: busy ? undefined : start, element, ...(busy ? { unavailable: 'Drawing the PDF…' } : {}) }
}
