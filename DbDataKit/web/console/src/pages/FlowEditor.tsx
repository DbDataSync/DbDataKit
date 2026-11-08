import { useEffect, useMemo, useState } from 'react'
import {
  PagedFrame, defaultParameters, diagnosticsFor, pageGeometry, placedWidgets, runningTokens, unknownTokens, useTheme,
  type ClientDiagnostic, type DocumentPreviewRequest, type EditCommand, type FlowBlockDoc, type FlowLayoutDoc, type FlowSectionDoc, type RenderedWidget, type RenderedWidgets, type ViewsDoc, type WidgetDoc,
} from '@dbdatakit/visuals'

type Apply = (command: EditCommand, group?: string) => void

const SIZES = ['A3', 'A4', 'A5', 'Letter', 'Legal'] as const
const SIDES = ['left', 'center', 'right'] as const
const MARGINS = ['top', 'right', 'bottom', 'left'] as const
const SPLITTING = new Set(['table', 'matrix'])

function Problems({ diagnostics, testId }: { diagnostics: ClientDiagnostic[]; testId: string }) {
  if (diagnostics.length === 0) return null
  return <ul className="hint" data-testid={testId} style={{ margin: '2px 0' }}>{diagnostics.map((d, i) => <li key={i} role="alert">{d.code} {d.message}</li>)}</ul>
}

/** A number box: empty removes the value, a number sets it, a half-typed text waits. */
function Millimetres({ label, testId, value, onChange, placeholder }: { label: string; testId: string; value: number | undefined; onChange: (n: number | undefined) => void; placeholder?: string }) {
  return (
    <label className="hint" style={{ display: 'inline-flex', gap: 4, alignItems: 'center' }}>{label}
      <input type="number" data-testid={testId} style={{ width: 72 }} placeholder={placeholder} value={value ?? ''}
        onChange={(e) => { if (e.target.value === '') onChange(undefined); else if (Number.isFinite(Number(e.target.value))) onChange(Number(e.target.value)) }} />
    </label>
  )
}

const describe = (block: FlowBlockDoc, widgets: ViewsDoc['widgets']) =>
  'pagebreak' in block ? 'Page break' : 'row' in block ? `Row of ${block.row.length} (${block.row.map((c) => c.span).join(' + ')} of 12)` : `${block.widget} (${widgets[block.widget]?.type ?? 'unknown widget'})`

/**
 * The form of one flow layout (plan 32 DBR27): page setup (size, orientation, margins, with the printable area the guides are drawn from), header and footer text with their tokens, the cover, and the
 * sections with their blocks (a widget, a row of spans, a page break; keep, repeat the header, height). Every change is an edit command, one undo step, and the validator's answer is shown at the field it is about.
 */
export function FlowEditor({ layout, index, doc, diagnostics, apply, name, text, preview, previewDocument }: {
  layout: FlowLayoutDoc; index: number; doc: ViewsDoc; diagnostics: ClientDiagnostic[]; apply: Apply; name: string
  /** The draft's text, which `previewDocument` sends. */
  text: string
  preview: (widget: WidgetDoc, id: string) => Promise<RenderedWidget>
  /** The source's one-call preview of an unsaved document (DBR28); a source without one is asked for each widget through `preview`. */
  previewDocument?: (request: Pick<DocumentPreviewRequest, 'text' | 'layout'>) => Promise<RenderedWidgets>
}) {
  const L = layout.name
  const id = (rest: string) => `ed-flow-${L}-${rest}`
  const own = diagnosticsFor(diagnostics, { area: 'layout', index })
  const at = (prefix: string) => own.filter((d) => d.path.replace(/^layouts\[\d+\]\./, '').startsWith(prefix))
  const page = (layout.page ?? {}) as { size?: string | { w: number; h: number }; orientation?: string; margins?: Partial<Record<(typeof MARGINS)[number], number>> }
  const geometry = pageGeometry(layout.page)
  const custom = typeof page.size === 'object' && page.size !== null
  const sections = layout.sections as FlowSectionDoc[]
  const widgets = doc.widgets ?? {}
  const placed = new Set(placedWidgets(doc, L))
  const [pick, setPick] = useState<Record<string, string>>({})
  const chosen = (key: string) => pick[key] ?? Object.keys(widgets)[0] ?? ''
  const sizeValue = custom ? 'custom' : typeof page.size === 'string' ? page.size : ''
  const known = runningTokens(doc)

  return (
    <div data-testid={id('editor')} style={{ borderTop: '1px dotted var(--border, #ddd)', marginTop: 6, paddingTop: 6 }}>
      <div data-testid={id('page')}>
        <strong>Page</strong>{' '}
        <label className="hint">size{' '}
          <select data-testid={id('size')} value={sizeValue} onChange={(e) => {
            const v = e.target.value
            apply({ type: 'setPage', layout: L, field: 'size', value: v === '' ? undefined : v === 'custom' ? { w: 210, h: 297 } : v })
          }}>
            <option value="">(A4)</option>
            {SIZES.map((s) => <option key={s} value={s}>{s}</option>)}
            <option value="custom">custom</option>
          </select>
        </label>{' '}
        {custom && (
          <>
            <Millimetres label="width mm" testId={id('size-w')} value={(page.size as { w: number }).w} onChange={(n) => { if (n !== undefined) apply({ type: 'setPage', layout: L, field: 'size', value: { w: n, h: (page.size as { h: number }).h } }, `${id('size-w')}`) }} />{' '}
            <Millimetres label="height mm" testId={id('size-h')} value={(page.size as { h: number }).h} onChange={(n) => { if (n !== undefined) apply({ type: 'setPage', layout: L, field: 'size', value: { w: (page.size as { w: number }).w, h: n } }, `${id('size-h')}`) }} />{' '}
          </>
        )}
        <label className="hint">orientation{' '}
          <select data-testid={id('orientation')} value={page.orientation ?? ''} onChange={(e) => apply({ type: 'setPage', layout: L, field: 'orientation', value: e.target.value === '' ? undefined : e.target.value })}>
            <option value="">(portrait)</option><option value="portrait">portrait</option><option value="landscape">landscape</option>
          </select>
        </label>{' '}
        {MARGINS.map((side) => (
          <span key={side}>
            <Millimetres label={`${side} mm`} testId={id(`margin-${side}`)} value={page.margins?.[side]} placeholder={String(geometry.margins[side])}
              onChange={(n) => apply({ type: 'setMargin', layout: L, side, value: n }, id(`margin-${side}`))} />{' '}
          </span>
        ))}
        <div className="hint" data-testid={id('printable')} data-width-px={geometry.contentWidthPx} data-height-px={geometry.contentHeightPx}>
          {geometry.widthMm} x {geometry.heightMm} mm {geometry.orientation}; the printable area is {Math.round(geometry.contentWidthMm * 10) / 10} x {Math.round(geometry.contentHeightMm * 10) / 10} mm ({Math.round(geometry.contentWidthPx)} x {Math.round(geometry.contentHeightPx)} px).
        </div>
        <Problems diagnostics={at('page')} testId={id('page-problems')} />
      </div>

      {(['header', 'footer'] as const).map((part) => (
        <div key={part} data-testid={id(part)} style={{ margin: '6px 0' }}>
          <strong>{part === 'header' ? 'Header' : 'Footer'}</strong>{' '}
          {SIDES.map((side) => {
            const text = (layout[part] as Record<string, string> | undefined)?.[side] ?? ''
            const unknown = unknownTokens(text, doc)
            return (
              <span key={side} style={{ display: 'inline-block', marginRight: 8 }}>
                <label className="hint">{side}{' '}
                  <input data-testid={id(`${part}-${side}`)} aria-invalid={at(`${part}.${side}`).length > 0 || unknown.length > 0} style={{ width: 170 }} value={text}
                    onChange={(e) => apply({ type: 'setRunningText', layout: L, part, side, text: e.target.value }, id(`${part}-${side}`))} />
                </label>
                <Problems diagnostics={at(`${part}.${side}`)} testId={id(`${part}-${side}-problems`)} />
              </span>
            )
          })}
        </div>
      ))}
      <div className="hint" data-testid={id('tokens')}>Tokens: {known.map((t) => <code key={t} style={{ marginRight: 6 }}>{t}</code>)}</div>
      <label className="hint"><input type="checkbox" data-testid={id('cover')} checked={(layout.cover as { parameters?: boolean } | undefined)?.parameters === true}
        onChange={(e) => apply({ type: 'setCover', layout: L, parameters: e.target.checked ? true : undefined })} /> cover page with the parameters</label>
      <Problems diagnostics={at('cover')} testId={id('cover-problems')} />

      {sections.map((section, si) => (
        <div key={si} data-testid={id(`section-${si}`)} style={{ border: '1px solid var(--border, #ddd)', borderRadius: 6, padding: 6, margin: '8px 0' }}>
          <div className="row" style={{ gap: 6, alignItems: 'center', flexWrap: 'wrap' }}>
            <strong>Section {si + 1}</strong>
            <input aria-label={`Section ${si + 1} title`} data-testid={id(`section-${si}-title`)} placeholder="title" value={section.title ?? ''}
              onChange={(e) => apply({ type: 'setSection', layout: L, section: si, key: 'title', value: e.target.value }, id(`section-${si}-title`))} />
            <label className="hint">break{' '}
              <select data-testid={id(`section-${si}-break`)} value={section.break ?? ''} onChange={(e) => apply({ type: 'setSection', layout: L, section: si, key: 'break', value: e.target.value === '' ? undefined : e.target.value })}>
                <option value="">none</option><option value="before">before</option><option value="after">after</option>
              </select>
            </label>
            <button type="button" className="btn btn-sm" data-testid={id(`section-${si}-up`)} disabled={si === 0} onClick={() => apply({ type: 'moveSection', layout: L, section: si, to: si - 1 })}>Up</button>
            <button type="button" className="btn btn-sm" data-testid={id(`section-${si}-down`)} disabled={si === sections.length - 1} onClick={() => apply({ type: 'moveSection', layout: L, section: si, to: si + 1 })}>Down</button>
            <button type="button" className="btn btn-sm" data-testid={id(`section-${si}-remove`)} disabled={sections.length === 1} onClick={() => apply({ type: 'removeSection', layout: L, section: si })}>Remove section</button>
          </div>
          <Problems diagnostics={own.filter((d) => d.path.replace(/^layouts\[\d+\]\./, '') === `sections[${si}].blocks` || d.path.replace(/^layouts\[\d+\]\./, '') === `sections[${si}]`)} testId={id(`section-${si}-problems`)} />
          <ol style={{ margin: '6px 0', paddingLeft: 22 }}>
            {section.blocks.map((block, bi) => {
              const key = `${si}-${bi}`
              const widgetBlock = 'widget' in block ? block : null
              const type = widgetBlock ? widgets[widgetBlock.widget]?.type : undefined
              const prefix = `sections[${si}].blocks[${bi}]`
              return (
                <li key={bi} data-testid={id(`block-${key}`)} style={{ marginBottom: 4 }}>
                  <div className="row" style={{ gap: 6, alignItems: 'center', flexWrap: 'wrap' }}>
                    <span>{describe(block, widgets)}</span>
                    {!('pagebreak' in block) && (
                      <label className="hint">keep together{' '}
                        <select data-testid={id(`block-${key}-keep`)} value={block.keep === undefined ? '' : String(block.keep)} onChange={(e) => apply({ type: 'setBlock', layout: L, section: si, block: bi, key: 'keep', value: e.target.value === '' ? undefined : e.target.value === 'true' })}>
                          <option value="">default</option><option value="true">yes</option><option value="false">no (may split)</option>
                        </select>
                      </label>
                    )}
                    {widgetBlock && type !== undefined && SPLITTING.has(type) && (
                      <label className="hint">repeat header{' '}
                        <select data-testid={id(`block-${key}-repeat`)} value={widgetBlock.repeat_header === undefined ? '' : String(widgetBlock.repeat_header)} onChange={(e) => apply({ type: 'setBlock', layout: L, section: si, block: bi, key: 'repeat_header', value: e.target.value === '' ? undefined : e.target.value === 'true' })}>
                          <option value="">default (yes)</option><option value="true">yes</option><option value="false">no</option>
                        </select>
                      </label>
                    )}
                    {widgetBlock && <Millimetres label="height mm" testId={id(`block-${key}-height`)} value={widgetBlock.height} onChange={(n) => apply({ type: 'setBlock', layout: L, section: si, block: bi, key: 'height', value: n }, id(`block-${key}-height`))} />}
                    <button type="button" className="btn btn-sm" data-testid={id(`block-${key}-up`)} disabled={bi === 0} onClick={() => apply({ type: 'moveBlock', layout: L, section: si, block: bi, to: bi - 1 })}>Up</button>
                    <button type="button" className="btn btn-sm" data-testid={id(`block-${key}-down`)} disabled={bi === section.blocks.length - 1} onClick={() => apply({ type: 'moveBlock', layout: L, section: si, block: bi, to: bi + 1 })}>Down</button>
                    <button type="button" className="btn btn-sm" data-testid={id(`block-${key}-remove`)} onClick={() => apply({ type: 'removeBlock', layout: L, section: si, block: bi })}>Remove</button>
                  </div>
                  {'row' in block && (
                    <div className="row" style={{ gap: 6, alignItems: 'center', flexWrap: 'wrap', margin: '4px 0 0 12px' }}>
                      {block.row.map((cell, ci) => (
                        <span key={ci} data-testid={id(`cell-${key}-${ci}`)} style={{ border: '1px dashed var(--border, #bbb)', borderRadius: 4, padding: '2px 4px' }}>
                          <select aria-label={`Cell ${ci + 1} widget`} data-testid={id(`cell-${key}-${ci}-widget`)} value={cell.widget} onChange={(e) => apply({ type: 'setCell', layout: L, section: si, block: bi, cell: ci, changes: { widget: e.target.value } })}>
                            {Object.keys(widgets).map((w) => <option key={w} value={w}>{w}</option>)}
                          </select>{' '}
                          <input type="number" min={1} max={12} aria-label={`Cell ${ci + 1} span`} data-testid={id(`cell-${key}-${ci}-span`)} style={{ width: 52 }} value={cell.span}
                            onChange={(e) => { if (e.target.value !== '') apply({ type: 'setCell', layout: L, section: si, block: bi, cell: ci, changes: { span: Number(e.target.value) } }, id(`cell-${key}-${ci}-span`)) }} />{' '}
                          <button type="button" className="btn btn-sm" aria-label={`Remove cell ${ci + 1}`} data-testid={id(`cell-${key}-${ci}-remove`)} disabled={block.row.length === 1} onClick={() => apply({ type: 'removeCell', layout: L, section: si, block: bi, cell: ci })}>x</button>
                        </span>
                      ))}
                      <button type="button" className="btn btn-sm" data-testid={id(`cell-${key}-add`)} disabled={Object.keys(widgets).length === 0 || block.row.length >= 12}
                        onClick={() => { const unused = Object.keys(widgets).find((w) => !placed.has(w)) ?? Object.keys(widgets)[0]!; const used = block.row.reduce((n, c) => n + c.span, 0); apply({ type: 'addCell', layout: L, section: si, block: bi, cell: { widget: unused, span: Math.max(1, Math.min(12, 12 - used)) } }) }}>Add cell</button>
                    </div>
                  )}
                  <Problems diagnostics={own.filter((d) => d.path.replace(/^layouts\[\d+\]\./, '').startsWith(prefix))} testId={id(`block-${key}-problems`)} />
                </li>
              )
            })}
            {section.blocks.length === 0 && <li className="hint" style={{ listStyle: 'none', marginLeft: -22 }}>No blocks yet.</li>}
          </ol>
          <div className="row" style={{ gap: 6, alignItems: 'center', flexWrap: 'wrap' }}>
            <select aria-label={`Widget to add to section ${si + 1}`} data-testid={id(`section-${si}-pick`)} value={chosen(`s${si}`)} onChange={(e) => setPick({ ...pick, [`s${si}`]: e.target.value })}>
              {Object.keys(widgets).map((w) => <option key={w} value={w}>{w}{placed.has(w) ? '' : ' (not placed)'}</option>)}
            </select>
            <button type="button" className="btn btn-sm" data-testid={id(`section-${si}-add-widget`)} disabled={!chosen(`s${si}`)} onClick={() => apply({ type: 'addBlock', layout: L, section: si, block: { widget: chosen(`s${si}`) } })}>Add widget block</button>
            <button type="button" className="btn btn-sm" data-testid={id(`section-${si}-add-row`)} disabled={!chosen(`s${si}`)} onClick={() => apply({ type: 'addBlock', layout: L, section: si, block: { row: [{ widget: chosen(`s${si}`), span: 12 }] } })}>Add row</button>
            <button type="button" className="btn btn-sm" data-testid={id(`section-${si}-add-break`)} onClick={() => apply({ type: 'addBlock', layout: L, section: si, block: { pagebreak: true } })}>Add page break</button>
          </div>
        </div>
      ))}
      <div className="row" style={{ gap: 6, alignItems: 'center' }}>
        <button type="button" className="btn btn-sm" data-testid={id('section-add')} onClick={() => apply({ type: 'addSection', layout: L, title: `Section ${sections.length + 1}` })}>Add section</button>
        {Object.keys(widgets).filter((w) => !placed.has(w)).length > 0 && <span className="hint" data-testid={id('unplaced')}>Not placed in this layout: {Object.keys(widgets).filter((w) => !placed.has(w)).join(', ')}</span>}
      </div>

      <FlowPreview layout={layout} doc={doc} name={name} text={text} preview={preview} previewDocument={previewDocument} testId={id('preview')} />
    </div>
  )
}

/**
 * The paged preview of the draft's flow layout (DBR14's `PagedFrame`: the guides where a page starts, the running text with its tokens, the cover), drawn from the draft as it stands, saved or not. With a source that previews a document
 * (the module, DBR28) the whole draft is sent in one call and resolved as the person; otherwise each widget the layout places is asked of `preview`. A draft the source refuses (what a publish would refuse) is said, not drawn.
 */
function FlowPreview({ layout, doc, name, text, preview, previewDocument, testId }: {
  layout: FlowLayoutDoc; doc: ViewsDoc; name: string; text: string; testId: string
  preview: (widget: WidgetDoc, id: string) => Promise<RenderedWidget>
  previewDocument?: (request: Pick<DocumentPreviewRequest, 'text' | 'layout'>) => Promise<RenderedWidgets>
}) {
  const theme = useTheme('auto')
  const [results, setResults] = useState<Record<string, RenderedWidget>>({})
  const [refused, setRefused] = useState<string | null>(null)
  const ids = useMemo(() => placedWidgets(doc, layout.name), [doc, layout.name])
  const key = previewDocument ? `${layout.name}\n${text}` : JSON.stringify(ids.map((id) => [id, doc.widgets[id]]))
  useEffect(() => {
    let cancelled = false
    const failed = (message: string): Record<string, RenderedWidget> => Object.fromEntries(ids.map((id): [string, RenderedWidget] => [id, { type: 'error', code: 'preview_failed', message }]))
    const timer = setTimeout(() => {
      if (previewDocument) {
        previewDocument({ text, layout: layout.name }).then(
          (answer) => { if (!cancelled) { setRefused(null); setResults(answer.widgets) } },
          (e: unknown) => { if (!cancelled) { const message = e instanceof Error ? e.message : String(e); setRefused(message); setResults(failed(message)) } })
        return
      }
      const asked = JSON.parse(key) as Array<[string, WidgetDoc | undefined]>
      void Promise.all(asked.map(async ([id, widget]): Promise<[string, RenderedWidget]> => {
        if (!widget) return [id, { type: 'error', code: 'unknown_widget', message: `There is no widget named \`${id}\`.` }]
        try { return [id, await preview(widget, id)] } catch (e) { return [id, { type: 'error', code: 'preview_failed', message: e instanceof Error ? e.message : String(e) }] }
      })).then((pairs) => { if (!cancelled) { setRefused(null); setResults(Object.fromEntries(pairs)) } })
    }, 200)
    return () => { cancelled = true; clearTimeout(timer) }
    // eslint-disable-next-line react-hooks/exhaustive-deps -- `key` is the draft (the text, or the widgets it places); the functions are the page's and change with it
  }, [key, name])
  const parameters = useMemo(() => defaultParameters(doc), [doc])
  return (
    <div style={{ marginTop: 8 }} data-testid={testId} data-widgets={ids.join(' ')} data-via={previewDocument ? 'document' : 'widgets'} data-ready={String(ids.every((id) => results[id] !== undefined))}>
      <strong>Preview</strong> <span className="hint">of what is written here, saved or not</span>
      {refused && <p className="hint" role="alert" data-testid={`${testId}-refused`}>{refused}</p>}
      <div style={{ width: 'min(100%, 820px)', height: 560, marginTop: 4, border: '1px solid var(--border, #ddd)', overflow: 'auto' }}>
        <PagedFrame document={doc} layout={layout} box={{ width: 820, height: 560 }} theme={theme} results={results} parameters={parameters} />
      </div>
    </div>
  )
}
