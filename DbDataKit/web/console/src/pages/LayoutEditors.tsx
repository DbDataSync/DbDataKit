import { useRef, useState, type PointerEvent as ReactPointerEvent } from 'react'
import {
  MEDIA, PIXELS_PER_UNIT, badNumbers, dragTile, fieldsFromWhen, overlapping, pointerToUnits, testSelector, tileMarks, whenFromFields,
  type CanvasLayoutDoc, type EditCommand, type Handle, type LayoutDoc, type Medium, type Rect, type ReflowLayoutDoc, type SelectorFields, type TileDoc, type ViewsDoc,
} from '@dbdatakit/visuals'

type Apply = (command: EditCommand, group?: string) => void
type Placed = ReflowLayoutDoc | CanvasLayoutDoc

/** The width of the drawn grid or canvas, in screen pixels. */
const STAGE_WIDTH = 720
const ROW_HEIGHT = 36
const HANDLES: Handle[] = ['n', 's', 'e', 'w', 'se']
const STRIP: Record<string, React.CSSProperties> = {
  n: { left: 6, right: 6, top: -3, height: 7, cursor: 'ns-resize' },
  s: { left: 6, right: 6, bottom: -3, height: 7, cursor: 'ns-resize' },
  e: { top: 6, bottom: 6, right: -3, width: 7, cursor: 'ew-resize' },
  w: { top: 6, bottom: 6, left: -3, width: 7, cursor: 'ew-resize' },
  se: { right: -4, bottom: -4, width: 12, height: 12, cursor: 'nwse-resize' },
}

let gestures = 0
interface DragState { gesture: number; widget: string; handle: Handle; startX: number; startY: number; rect: Rect; pixelsX: number; pixelsY: number }

/**
 * A grid or a canvas drawn at a fixed width with its tiles as boxes you drag and pull (plan 32 DBR26). A pointer's distance is turned into units (`pointerToUnits`) and `dragTile` (pure, tested) gives the
 * tile the drag makes from the tile's starting rectangle: whole cells in a reflow grid (an overlap is refused and said), the canvas's snap in a canvas (an overlap is outlined). Every move is one `setTile` of
 * one group, so a drag is one undo step. A tile below its widget's minimum is outlined, and so is one that overlaps another.
 */
export function GridEditor({ layout, widgets, apply, say }: { layout: Placed; widgets: ViewsDoc['widgets']; apply: Apply; say: (text: string | null) => void }) {
  const drag = useRef<DragState | null>(null)
  const canvas = layout.type === 'canvas' ? layout.canvas : null
  const ppu = canvas ? PIXELS_PER_UNIT[canvas.unit ?? 'px'] : 1
  const columns = layout.type === 'reflow' ? layout.grid.columns : 0
  const reach = layout.tiles.reduce((m, t) => Math.max(m, t.y + t.h), 0)
  const rows = layout.type === 'reflow' ? layout.grid.rows ?? Math.max(6, reach + 1) : 0
  const pixelsX = canvas ? STAGE_WIDTH / (canvas.width * ppu) * ppu : STAGE_WIDTH / columns
  const pixelsY = canvas ? pixelsX : ROW_HEIGHT
  const width = STAGE_WIDTH
  const height = canvas ? canvas.height * pixelsY : rows * ROW_HEIGHT
  const marks = new Map(tileMarks(layout, widgets).map((m) => [m.widget, m]))
  const overlaps = overlapping(layout, canvas !== null)

  const start = (e: ReactPointerEvent, tile: TileDoc, handle: Handle) => {
    if (e.button !== 0) return
    e.stopPropagation()
    ;(e.currentTarget as HTMLElement).setPointerCapture(e.pointerId)
    drag.current = { gesture: ++gestures, widget: tile.widget, handle, startX: e.clientX, startY: e.clientY, rect: { x: tile.x, y: tile.y, w: tile.w, h: tile.h }, pixelsX, pixelsY }
    say(null)
  }
  const move = (e: ReactPointerEvent) => {
    const d = drag.current
    if (!d) return
    const result = dragTile(layout, d.widget, d.rect, d.handle, pointerToUnits(e.clientX - d.startX, d.pixelsX), pointerToUnits(e.clientY - d.startY, d.pixelsY))
    if (result.refused) { say(result.refused); return }
    say(null)
    apply({ type: 'setTile', layout: layout.name, widget: d.widget, changes: { x: result.rect.x, y: result.rect.y, w: result.rect.w, h: result.rect.h } }, `drag:${layout.name}:${d.widget}:${d.gesture}`)
  }
  const end = (e: ReactPointerEvent) => {
    if (!drag.current) return
    drag.current = null
    try { (e.currentTarget as HTMLElement).releasePointerCapture(e.pointerId) } catch { /* already released */ }
  }

  const cellLines = canvas
    ? undefined
    : `linear-gradient(to right, var(--border, #d8d8d8) 1px, transparent 1px) 0 0 / ${pixelsX}px 100%, linear-gradient(to bottom, var(--border, #d8d8d8) 1px, transparent 1px) 0 0 / 100% ${ROW_HEIGHT}px`

  return (
    <div style={{ overflowX: 'auto', margin: '6px 0' }}>
      <div data-testid={`ed-grid-${layout.name}`} data-kind={layout.type} data-pixels-x={pixelsX} data-pixels-y={pixelsY}
        style={{ position: 'relative', width, height, background: cellLines, border: '1px solid var(--border, #bbb)', touchAction: 'none', userSelect: 'none' }}>
        {layout.tiles.map((t) => {
          const mark = marks.get(t.widget)
          const below = mark?.belowMinimum === true
          const over = overlaps.has(t.widget)
          return (
            <div key={t.widget} data-testid={`ed-gridtile-${layout.name}-${t.widget}`} data-below-minimum={String(below)} data-overlap={String(over)}
              onPointerDown={(e) => start(e, t, 'move')} onPointerMove={move} onPointerUp={end} onPointerCancel={end}
              title={below && mark ? `${widgets[t.widget]?.type ?? 'widget'} needs at least ${mark.minimum.width} x ${mark.minimum.height} px` : undefined}
              style={{
                position: 'absolute', left: t.x * pixelsX, top: t.y * pixelsY, width: t.w * pixelsX, height: t.h * pixelsY, boxSizing: 'border-box', cursor: 'move', overflow: 'hidden',
                background: 'var(--surface-2, rgba(80,120,200,.15))', border: `2px solid ${below || over ? 'var(--danger, #c0392b)' : 'var(--accent, #3b6fd4)'}`, borderStyle: below ? 'dashed' : 'solid', fontSize: 12, padding: 2,
              }}>
              <code>{t.widget}</code> <span className="hint">{widgets[t.widget]?.type}</span>
              {HANDLES.map((h) => (
                <div key={h} data-testid={`ed-gridhandle-${layout.name}-${t.widget}-${h}`} onPointerDown={(e) => start(e, t, h)} onPointerMove={move} onPointerUp={end} onPointerCancel={end}
                  style={{ position: 'absolute', background: 'var(--accent, #3b6fd4)', opacity: h === 'se' ? 1 : 0.35, ...STRIP[h] }} />
              ))}
            </div>
          )
        })}
      </div>
    </div>
  )
}

const numberOrUndefined = (text: string) => (text.trim() === '' || !Number.isFinite(Number(text)) ? undefined : Number(text))

/** A number box that edits a value of the document: an empty box removes the key, a number sets it, a half-typed one waits. */
function NumberField({ label, testId, value, onChange, step }: { label: string; testId: string; value: number | undefined; onChange: (n: number | undefined) => void; step?: number }) {
  return (
    <label className="hint" style={{ display: 'inline-flex', gap: 4, alignItems: 'center' }}>{label}
      <input type="number" step={step} data-testid={testId} style={{ width: 80 }} value={value ?? ''} onChange={(e) => { if (e.target.value === '') onChange(undefined); else { const n = numberOrUndefined(e.target.value); if (n !== undefined) onChange(n) } }} />
    </label>
  )
}

/** The grid's or the canvas's own settings: columns and rows; width, height, unit, overflow and the two scales. A key left empty is removed, so the document keeps its defaults. */
export function LayoutSettings({ layout, index, apply }: { layout: Placed; index: number; apply: Apply }) {
  const set = (path: Array<string | number>, value: unknown, exists: boolean) => {
    if (value === undefined && !exists) return
    apply(value === undefined ? { type: 'removePath', path } : { type: 'setPath', path, value }, `layout:${layout.name}:${path.join('.')}`)
  }
  if (layout.type === 'reflow')
    return (
      <div className="row" style={{ gap: 12, flexWrap: 'wrap' }} data-testid={`ed-settings-${layout.name}`}>
        <NumberField label="Columns" testId={`ed-grid-columns-${layout.name}`} value={layout.grid.columns} onChange={(n) => { if (n !== undefined) set(['layouts', index, 'grid', 'columns'], n, true) }} />
        <NumberField label="Rows" testId={`ed-grid-rows-${layout.name}`} value={layout.grid.rows} onChange={(n) => { if (n === undefined && layout.grid.rows === undefined) return; set(['layouts', index, 'grid', 'rows'], n, layout.grid.rows !== undefined) }} />
      </div>
    )
  const c = layout.canvas
  return (
    <div className="row" style={{ gap: 12, flexWrap: 'wrap', alignItems: 'center' }} data-testid={`ed-settings-${layout.name}`}>
      <NumberField label="Width" testId={`ed-canvas-width-${layout.name}`} value={c.width} onChange={(n) => { if (n !== undefined) set(['layouts', index, 'canvas', 'width'], n, true) }} />
      <NumberField label="Height" testId={`ed-canvas-height-${layout.name}`} value={c.height} onChange={(n) => { if (n !== undefined) set(['layouts', index, 'canvas', 'height'], n, true) }} />
      <label className="hint">Unit{' '}
        <select data-testid={`ed-canvas-unit-${layout.name}`} value={c.unit ?? 'px'} onChange={(e) => set(['layouts', index, 'canvas', 'unit'], e.target.value === 'px' ? undefined : e.target.value, c.unit !== undefined)}>
          <option value="px">px</option><option value="mm">mm</option><option value="in">in</option>
        </select>
      </label>
      <label className="hint">Overflow{' '}
        <select data-testid={`ed-canvas-overflow-${layout.name}`} value={layout.overflow ?? 'scale'} onChange={(e) => set(['layouts', index, 'overflow'], e.target.value, layout.overflow !== undefined)}>
          <option value="scale">scale</option><option value="scroll">scroll</option>
        </select>
      </label>
      <NumberField label="Min scale" step={0.1} testId={`ed-canvas-min-scale-${layout.name}`} value={layout.min_scale} onChange={(n) => { if (n === undefined && layout.min_scale === undefined) return; set(['layouts', index, 'min_scale'], n, layout.min_scale !== undefined) }} />
      <NumberField label="Max scale" step={0.1} testId={`ed-canvas-max-scale-${layout.name}`} value={layout.max_scale} onChange={(n) => { if (n === undefined && layout.max_scale === undefined) return; set(['layouts', index, 'max_scale'], n, layout.max_scale !== undefined) }} />
    </div>
  )
}

/** The selector of one layout (`when`): width, height and aspect ranges, orientation and media; empty conditions are left out, and no condition at all removes the selector. */
export function SelectorEditor({ layout, apply }: { layout: LayoutDoc; apply: Apply }) {
  const [typed, setTyped] = useState<SelectorFields | null>(null)
  const fields = typed ?? fieldsFromWhen(layout.when)
  const bad = badNumbers(fields)
  const change = (patch: Partial<SelectorFields>) => {
    const next = { ...fields, ...patch }
    setTyped(next)
    if (badNumbers(next).length === 0) apply({ type: 'setSelector', layout: layout.name, when: whenFromFields(next) }, `selector:${layout.name}`)
  }
  const box = (key: Exclude<keyof SelectorFields, 'orientation' | 'media'>, label: string) => (
    <label className="hint" key={key} style={{ display: 'inline-flex', gap: 4, alignItems: 'center' }}>{label}
      <input data-testid={`ed-selector-${key}-${layout.name}`} style={{ width: 70 }} inputMode="decimal" aria-invalid={bad.includes(key)} value={fields[key]} onChange={(e) => change({ [key]: e.target.value })} />
    </label>
  )
  return (
    <div className="row" style={{ gap: 10, flexWrap: 'wrap', alignItems: 'center' }} data-testid={`ed-selector-${layout.name}`}>
      <span className="hint">Shown when</span>
      {box('widthMin', 'width from')}{box('widthMax', 'to')}
      {box('heightMin', 'height from')}{box('heightMax', 'to')}
      {box('aspectMin', 'aspect from')}{box('aspectMax', 'to')}
      <label className="hint">orientation{' '}
        <select data-testid={`ed-selector-orientation-${layout.name}`} value={fields.orientation} onChange={(e) => change({ orientation: e.target.value as SelectorFields['orientation'] })}>
          <option value="">any</option><option value="portrait">portrait</option><option value="landscape">landscape</option>
        </select>
      </label>
      {MEDIA.map((m) => (
        <label key={m} className="hint"><input type="checkbox" data-testid={`ed-selector-medium-${m}-${layout.name}`} checked={fields.media.includes(m)} onChange={(e) => change({ media: e.target.checked ? [...fields.media, m] : fields.media.filter((x: Medium) => x !== m) })} /> {m}</label>
      ))}
      {bad.length > 0 && <span className="hint" role="alert" data-testid={`ed-selector-bad-${layout.name}`}>Not a number: {bad.join(', ')}. The selector is kept as it was until it is.</span>}
    </div>
  )
}

/** "Test at W x H": which layout the viewer would show in a window of that size and medium, by `selectLayout`. */
export function SelectorTester({ doc }: { doc: ViewsDoc }) {
  const [width, setWidth] = useState('390')
  const [height, setHeight] = useState('844')
  const [medium, setMedium] = useState<Medium>('screen')
  const w = numberOrUndefined(width), h = numberOrUndefined(height)
  const answer = w === undefined || h === undefined ? null : testSelector(doc, { width: w, height: h, medium })
  return (
    <div className="row" style={{ gap: 8, alignItems: 'center', flexWrap: 'wrap' }} data-testid="ed-selector-test">
      <strong>Test at</strong>
      <input aria-label="Test width" data-testid="ed-test-width" style={{ width: 70 }} value={width} onChange={(e) => setWidth(e.target.value)} /> x
      <input aria-label="Test height" data-testid="ed-test-height" style={{ width: 70 }} value={height} onChange={(e) => setHeight(e.target.value)} />
      <select aria-label="Test medium" data-testid="ed-test-medium" value={medium} onChange={(e) => setMedium(e.target.value as Medium)}>{MEDIA.map((m) => <option key={m} value={m}>{m}</option>)}</select>
      <span data-testid="ed-test-result" data-layout={answer?.ok ? answer.layout : ''}>
        {answer === null ? 'Give a width and a height.' : answer.ok ? `shows the layout ${answer.layout} (${answer.choice === 'matched' ? 'its selector matches' : 'the default'})` : answer.refusal}
      </span>
    </div>
  )
}
