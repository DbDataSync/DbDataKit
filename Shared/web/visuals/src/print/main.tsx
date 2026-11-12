import { createRoot } from 'react-dom/client'
import { DashboardView } from '../dashboard/DashboardView'
import type { ViewsClient } from '../client/types'
import type { CustomViewsHost } from '../custom/CustomWidgetFrame'
import type { FrameState } from '../custom/controller'
import type { ViewKind } from '../views/document'
import type { ParameterValues, ViewsDoc } from '../views/model'
import type { RenderedWidgets } from '../views/rendered'
import './print.css'
import { customProblem } from './custom'
import { whenDrawn } from './ready'

/**
 * The print page of a dashboard or report (plan 32 DBR15): what the `views` module's print route and the PDF exporter load. The module puts one JSON block in the page (`#dbdatakit-print`: the document, the layout in play,
 * the paper, the parameters and **the rendered widgets as the viewer**); this draws it with the same `DashboardView` the console draws, over a client that answers from that block (nothing is fetched: the page may load
 * nothing from outside), at the paper's printable size, in the `print` medium. When everything is drawn (fonts loaded, no widget busy, every chart's SVG in place) it sets `window.__reportReady`; a page that cannot be drawn
 * sets `window.__reportError` with the words, which the exporter turns into a refusal.
 */
interface PrintPayload {
  kind: ViewKind
  name: string
  layout: string
  viewer?: string | null
  parameters?: ParameterValues
  paper: { width_mm: number; height_mm: number; margins_mm: { top: number; right: number; bottom: number; left: number } }
  document: ViewsDoc
  rendered: RenderedWidgets
  /** The app files that draw custom widgets (DBR32): each component's page, relative to this page's origin; the app's files are served from the same one-shot origin. */
  custom?: { frames: Record<string, string> }
}

declare global {
  interface Window { __reportReady?: boolean; __reportError?: string }
}

const px = (mm: number) => Math.round(((mm * 96) / 25.4) * 100) / 100

function fail(message: string): void {
  window.__reportError = message
}

async function main() {
  const block = document.getElementById('dbdatakit-print')
  if (!block?.textContent) return fail('the page carries no document (the print block is missing).')
  const payload = JSON.parse(block.textContent) as PrintPayload
  const rendered = payload.rendered
  const nothing = (what: string) => () => Promise.reject(new Error(`${what} is not available on a print page.`))
  const client: ViewsClient = {
    list: async () => [],
    open: nothing('open'),
    resolve: async () => rendered,
    validate: nothing('validate'),
    save: nothing('save'),
    preview: nothing('preview'),
  }
  const { paper } = payload
  const box = {
    width: Math.floor(px(paper.width_mm - paper.margins_mm.left - paper.margins_mm.right)),
    height: Math.floor(px(paper.height_mm - paper.margins_mm.top - paper.margins_mm.bottom)),
  }
  const parameters = payload.parameters && Object.keys(payload.parameters).length > 0 ? payload.parameters : undefined

  // custom widgets (DBR32): the code of the app draws them in frames loaded from the app's own files, given the rows embedded in this page; the page is drawn when every frame is ready, and a frame that failed or whose content is taller than a page fails the export
  const frames = payload.custom?.frames ?? {}
  const customIds = Object.keys(rendered.widgets).filter((id) => payload.document.widgets[id]?.type === 'custom')
  const states: Record<string, FrameState> = {}
  const sizes: Record<string, number> = {}
  const customViews: CustomViewsHost | undefined = customIds.length === 0 ? undefined : {
    frameSrc: (component) => frames[component] ?? null,
    onFrameState: (widget, state) => { states[widget] = state },
    onFrameSize: (widget, height) => { sizes[widget] = height },
  }

  const root = document.getElementById('root')!
  createRoot(root).render(
    <DashboardView kind={payload.kind} name={payload.name} document={payload.document} client={client} layout={payload.layout} medium="print" box={box} theme="light" parameters={parameters} viewer={payload.viewer ?? undefined} customViews={customViews} />,
  )
  await whenDrawn(root, { widgets: Object.keys(rendered.widgets).length })
  const problem = customProblem(payload.document, rendered, customIds, states, sizes, box.height)
  if (problem) return fail(problem)
  window.__reportReady = true
}

main().catch((e: unknown) => fail(e instanceof Error ? e.message : String(e)))
