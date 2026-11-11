import type { LayoutDoc, TileDoc, ViewsDoc, WidgetDoc, WidgetType } from '@dbdatakit/visuals'

/** The model the document's widgets already query (the first), or the retail sample's: what a new widget asks of; the field wells (DBR25) then choose its fields from the model's catalog. */
export function modelOf(doc: ViewsDoc, fallback = 'retail_sales'): string {
  for (const w of Object.values(doc.widgets ?? {})) {
    const model = (w as { query?: { model?: unknown } }).query?.model
    if (typeof model === 'string') return model
  }
  return fallback
}

/** A new widget of a type, valid and plain: a starting point to edit (the palette of DBR24; the wells of DBR25 choose the fields from the model's catalog). */
export function newWidget(type: WidgetType, doc: ViewsDoc, fallbackModel?: string): WidgetDoc {
  const query = { model: modelOf(doc, fallbackModel) }
  switch (type) {
    case 'metric': return { type, label: 'New metric', encodings: { value: 'sales.net_amount' }, query }
    case 'chart': return { type, title: 'New chart', mark: 'bar', encodings: { x: 'customers.region', y: 'sales.net_amount' }, query }
    case 'table': return { type, title: 'New table', encodings: { columns: [{ field: 'products.product', label: 'Product' }, { field: 'sales.units', label: 'Units', align: 'right' }] }, query }
    case 'matrix': return { type, title: 'New matrix', encodings: { rows: ['customers.region'], columns: ['products.category'], values: [{ field: 'sales.net_amount' }] }, query }
    case 'text': return { type, text: 'Write a note.' }
    // an image is held by the hash of its bytes: until the publish slice uploads one this is the placeholder, and the editor says no bytes are recorded
    case 'image': return { type, hash: `sha256:${'0'.repeat(64)}`, media_type: 'image/png', alt: 'Describe the image' }
    case 'control': {
      const parameter = doc.parameters?.[0]
      if (!parameter) throw new Error('A control edits a parameter of the document: add a parameter first.')
      return { type, parameter: parameter.name, label: parameter.label ?? parameter.name }
    }
  }
}

/** Where a newly placed widget goes: under everything in a reflow grid (full width up to six columns, two rows), below the lowest tile of a canvas. */
export function nextTile(layout: LayoutDoc, widget: WidgetDoc, id: string): TileDoc {
  const tiles = (layout as { tiles?: TileDoc[] }).tiles ?? []
  const bottom = tiles.reduce((m, t) => Math.max(m, t.y + t.h), 0)
  if (layout.type === 'canvas') return { widget: id, x: 10, y: bottom + 10, w: Math.min(300, layout.canvas.width - 20), h: widget.type === 'text' ? 80 : 200 }
  if (layout.type === 'reflow') return { widget: id, x: 0, y: bottom, w: Math.min(6, layout.grid.columns), h: widget.type === 'text' ? 1 : 2 }
  throw new Error('A flow layout places widgets in blocks, not tiles.')
}

/** The named number presets a format select offers (a format may also be a mapping, which the select shows as one read-only choice). */
export const FORMAT_PRESETS = ['catalog', 'full', 'compact', 'percent'] as const
/** The value of the read-only choice that stands for a format written as a mapping. */
export const CUSTOM_FORMAT = '(custom)'

/** What a format select shows for a stored `format`: a preset name, the custom marker for a mapping (or a name that is no preset), or empty for none. A mapping is edited in the JSON, not here. */
export function formatChoice(format: unknown): string {
  if (format === undefined || format === null || format === '') return ''
  return typeof format === 'string' && (FORMAT_PRESETS as readonly string[]).includes(format) ? format : CUSTOM_FORMAT
}
