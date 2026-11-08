import { Fragment, useLayoutEffect, useMemo, useRef, useState } from 'react'
import type { Theme } from '../theme'
import type { Box, FlowLayoutDoc, ParameterDefinition, ParameterValue, ParameterValues, ViewsDoc, WidgetDoc } from '../views/model'
import type { RenderedWidget } from '../views/rendered'
import { WidgetView } from '../widgets/WidgetView'
import { flowCss } from './css'
import { layoutFlow, type FlowBlock, type FlowPlan } from './layoutFlow'
import { mmToPx } from './page'
import { DEFAULT_HEIGHT_MM, paginate, planItems, startY, type Measured } from './paginate'
import { describeParameter, formatAsOf, runningText, type RunningContext } from './tokens'

export interface PagedFrameProps {
  document: Pick<ViewsDoc, 'title' | 'widgets' | 'parameters'>
  layout: FlowLayoutDoc
  /** The container in CSS pixels: the sheet is the page's width and the frame scrolls inside this box. */
  box: Box
  theme: Theme
  results: Record<string, RenderedWidget>
  loading?: ReadonlySet<string>
  parameters?: ParameterValues
  onParameterChange?: (parameter: string, value: ParameterValue) => void
  /** When the rows were read, and who is looking: the `{asof}` and `{viewer}` of the running text and the cover. */
  asOf?: string | null
  viewer?: string | null
  /** The browser's own Print on this HTML (the print stylesheet); the default is `window.print`. */
  onPrint?: () => void
  /** The exact PDF in the viewer (the exporter, DBR15); without it the button is disabled and says why. */
  onPrintPreview?: () => void
  previewUnavailable?: string
  /** Called with the widgets whose block is wider than the page's content, after layout (changes only). */
  onOverWidth?: (widgets: string[]) => void
}

const px = (n: number) => `${n}px`

/** The widgets that need a box of their own height: a chart, a metric and an image have a declared (or default) height; the rest take the height of their content. */
const fixedHeightMm = (widget: WidgetDoc | undefined, heightMm: number | undefined) => heightMm ?? (widget ? DEFAULT_HEIGHT_MM[widget.type] : undefined)

/**
 * A report (a `flow` layout) as markup, on screen (plan 32 section 4.2, DBR14): the layout compiled to the HTML the browser lays out (`layoutFlow`, the rules of `flowCss`), set in a scrolling sheet the width of the
 * page, with **page-break guides**: dashed lines where a page starts. The guides are an estimate (`paginate`, over heights measured in the browser once it has laid the blocks out) and the frame says so; the exporter's PDF
 * is exact. The same stylesheet is in the page, so the browser's own Print works on it (`@page`, `break-*`, a repeating table head). After layout, a block wider than the page's content is **reported**
 * (`onOverWidth`, a note on screen). No fetching: the results are given.
 */
export function PagedFrame({ document, layout, box, theme, results, loading, parameters = {}, onParameterChange, asOf = null, viewer = null, onPrint, onPrintPreview, previewUnavailable, onOverWidth }: PagedFrameProps) {
  const plan = useMemo(() => layoutFlow(layout, document.widgets), [layout, document.widgets])
  const { page } = plan
  const content = useRef<HTMLDivElement>(null)
  const [measured, setMeasured] = useState<Record<string, Measured>>({})
  const [wide, setWide] = useState<string[]>([])
  const reported = useRef('')
  const callback = useRef(onOverWidth)
  callback.current = onOverWidth

  // after layout: the top and height of each block (and a table's rows) in the content's own coordinates, and which blocks are wider than the content
  useLayoutEffect(() => {
    const element = content.current
    if (!element) return
    const read = () => {
      const origin = element.getBoundingClientRect()
      const next: Record<string, Measured> = {}
      const over: string[] = []
      element.querySelectorAll<HTMLElement>('[data-flow-key]').forEach((el) => {
        const r = el.getBoundingClientRect()
        const m: Measured = { top: Math.round((r.top - origin.top) * 10) / 10, height: Math.round(r.height * 10) / 10 }
        const table = el.querySelector('table')
        if (table) {
          m.header = Math.round((table.tHead?.getBoundingClientRect().height ?? 0) * 10) / 10
          m.rows = [...table.querySelectorAll('tbody > tr')].map((tr) => {
            const t = tr.getBoundingClientRect()
            return { top: Math.round((t.top - origin.top) * 10) / 10, height: Math.round(t.height * 10) / 10 }
          })
        }
        next[el.dataset.flowKey!] = m
        // wider than the page's content (the block), or than its own column of a row (the cell: its content runs into the next)
        if (el.scrollWidth > el.clientWidth + 1) for (const id of (el.dataset.widgets ?? '').split(' ').filter(Boolean)) if (!over.includes(id)) over.push(id)
        el.querySelectorAll<HTMLElement>('[data-flow-cell]').forEach((cell) => {
          const id = cell.dataset.flowCell!
          if (cell.scrollWidth > cell.clientWidth + 1 && !over.includes(id)) over.push(id)
        })
      })
      setMeasured((old) => (JSON.stringify(old) === JSON.stringify(next) ? old : next))
      setWide((old) => (old.join(' ') === over.join(' ') ? old : over))
    }
    read()
    if (typeof ResizeObserver === 'undefined') return
    const observer = new ResizeObserver(read)
    observer.observe(element)
    return () => observer.disconnect()
  }, [plan, results, box.width, theme, parameters])

  useLayoutEffect(() => {
    const key = wide.join(' ')
    if (key !== reported.current) {
      reported.current = key
      callback.current?.(wide)
    }
  }, [wide])

  const items = useMemo(() => planItems(plan, document.widgets, results, measured), [plan, document.widgets, results, measured])
  const pagination = useMemo(() => paginate(items, page.contentHeightPx), [items, page.contentHeightPx])
  const pages = pagination.pages.length
  const guides = pagination.starts.map((s) => ({ page: s.page, y: Math.round(startY(s, items, measured)) }))
  const running = (part: Record<string, string> | undefined, number: number) => {
    const context: RunningContext = { title: document.title, page: number, pages, asOf, viewer, parameters }
    return runningText(part, context)
  }
  const head = running(plan.header, 1)
  const foot = running(plan.footer, pages)

  const definitions = document.parameters as ParameterDefinition[] | undefined
  const widgetView = (id: string, width: number, heightMm: number | undefined) => {
    const widget = document.widgets[id]
    if (!widget) return null
    const fixed = fixedHeightMm(widget, heightMm)
    return (
      <WidgetView
        id={id}
        widget={widget}
        result={results[id]}
        loading={loading?.has(id) ?? results[id] === undefined}
        box={{ width, height: fixed !== undefined ? mmToPx(fixed) : 100000 }}
        theme={theme}
        options={layout.widget_options?.[id]}
        parameters={parameters}
        definitions={definitions}
        onParameterChange={onParameterChange}
      />
    )
  }
  const itemAttributes = (block: Extract<FlowBlock, { kind: 'widget' | 'row' }>, widgets: string[], fixedMm: number | undefined) => ({
    className: 'vz-flow-item',
    'data-flow-key': block.key,
    'data-widgets': widgets.join(' '),
    'data-keep': block.keep ? '' : undefined,
    'data-fixed': fixedMm !== undefined ? '' : undefined,
    'data-no-repeat': block.kind === 'widget' && block.split && !block.repeatHeader ? '' : undefined,
    style: fixedMm !== undefined ? { height: px(mmToPx(fixedMm)) } : undefined,
  })
  const block = (b: FlowBlock) => {
    if (b.kind === 'break') return <div key={b.key} className="vz-flow-break" data-testid="vz-flow-break" data-flow-key={b.key} />
    if (b.kind === 'row') {
      const fixed = b.heightMm
      return (
        <div key={b.key} {...itemAttributes(b, b.cells.map((c) => c.widget), fixed)}>
          <div className="vz-flow-row" style={fixed !== undefined ? { height: '100%' } : undefined}>
            {b.cells.map((c) => (
              <div key={c.widget} style={{ gridColumn: `span ${c.span}`, minWidth: 0 }} data-flow-cell={c.widget} data-testid={`vz-flow-cell-${c.widget}`}>{widgetView(c.widget, (page.contentWidthPx * c.span) / 12, fixed)}</div>
            ))}
          </div>
        </div>
      )
    }
    const fixed = fixedHeightMm(document.widgets[b.widget], b.heightMm)
    return <div key={b.key} {...itemAttributes(b, [b.widget], fixed)} data-testid={`vz-flow-block-${b.widget}`}>{widgetView(b.widget, page.contentWidthPx, b.heightMm)}</div>
  }

  const shownParameters = (document.parameters ?? []) as ParameterDefinition[]
  return (
    <div className="vz vz-flow-outer vz-flow" data-vz-theme={theme} data-testid="vz-flow" data-layout={plan.layout} data-layout-type="flow" data-pages={pages} style={{ width: box.width, height: box.height }}>
      <style>{flowCss(page)}</style>
      <div className="vz-flow-bar">
        <span className="vz-flow-note" data-testid="vz-flow-note">
          {page.size} {page.orientation}, about {pages} page{pages === 1 ? '' : 's'}: the dashed lines are where the pages probably start; the PDF is exact.
        </span>
        <span className="vz-flow-actions">
          <button type="button" className="vz-button" data-testid="vz-flow-print" onClick={onPrint ?? (() => window.print())}>Print</button>
          <button type="button" className="vz-button" data-testid="vz-flow-preview" disabled={!onPrintPreview} title={onPrintPreview ? undefined : previewUnavailable ?? 'Print preview needs the PDF exporter, which is not installed.'} onClick={onPrintPreview}>
            Print preview
          </button>
        </span>
      </div>
      {wide.length > 0 ? (
        <p className="vz-state vz-error vz-flow-overwidth" role="note" data-testid="vz-flow-overwidth" data-widgets={wide.join(' ')}>
          Wider than the page: {wide.map((id) => document.widgets[id]?.title ?? id).join(', ')}. The page content is {Math.round(page.contentWidthMm)} mm wide; make the block narrower or give the page more room.
        </p>
      ) : null}
      <div className="vz-flow-scroll" data-testid="vz-flow-scroll">
        <div className="vz-flow-sheet" data-testid="vz-flow-sheet" style={{ width: px(page.widthPx), padding: `${px(page.marginsPx.top)} ${px(page.marginsPx.right)} ${px(page.marginsPx.bottom)} ${px(page.marginsPx.left)}` }}>
          {head ? <RunningLine side="head" text={head} margins={page.marginsPx} testid="vz-flow-header" /> : null}
          <div ref={content} className="vz-flow-content" data-testid="vz-flow-content">
            {plan.cover ? (
              <section className="vz-flow-cover" data-testid="vz-flow-cover" style={{ height: px(page.contentHeightPx - 12) }}>
                <h1>{document.title}</h1>
                <dl>
                  <dt>As of</dt><dd>{formatAsOf(asOf) || 'not read yet'}</dd>
                  {viewer ? <Fragment><dt>Viewer</dt><dd>{viewer}</dd></Fragment> : null}
                  {shownParameters.map((p) => <Fragment key={p.name}><dt>{p.label ?? p.name}</dt><dd>{describeParameter(parameters[p.name])}</dd></Fragment>)}
                </dl>
              </section>
            ) : null}
            {plan.sections.map((section) => (
              <section key={section.index} className="vz-flow-section" data-testid={`vz-flow-section-${section.index}`} data-break-before={section.breakBefore ? '' : undefined} data-break-after={section.breakAfter ? '' : undefined}>
                {section.title !== undefined ? <h2 className="vz-flow-title" data-flow-key={`s${section.index}t`}>{section.title}</h2> : null}
                {section.blocks.map(block)}
              </section>
            ))}
            {guides.map((g) => (
              <div key={g.page} className="vz-flow-guide" data-testid="vz-flow-guide" data-page={g.page} data-y={g.y} style={{ top: px(g.y), left: px(-page.marginsPx.left), width: px(page.widthPx) }} aria-hidden="true">
                <span>Page {g.page}</span>
              </div>
            ))}
          </div>
          {foot ? <RunningLine side="foot" text={foot} margins={page.marginsPx} testid="vz-flow-footer" /> : null}
        </div>
      </div>
    </div>
  )
}

function RunningLine({ side, text, margins, testid }: { side: 'head' | 'foot'; text: { left: string; center: string; right: string }; margins: { top: number; right: number; bottom: number; left: number }; testid: string }) {
  return (
    <div className={`vz-flow-running vz-flow-${side}`} data-testid={testid} style={{ [side === 'head' ? 'top' : 'bottom']: 0, height: px(side === 'head' ? margins.top : margins.bottom), left: px(margins.left), right: px(margins.right) }}>
      <span>{text.left}</span><span>{text.center}</span><span>{text.right}</span>
    </div>
  )
}

export type { FlowPlan }
