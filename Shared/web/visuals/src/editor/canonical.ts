import { Document, isMap, isScalar, isSeq, YAMLMap, YAMLSeq, type Node } from 'yaml'

/**
 * The canonical text of a dashboard or a report (plan 32 DBR24): the keys of the document, of a widget, of a layout and of a tile in the order a person reads them, short leaves in flow style
 * (`{widget: net, x: 0, y: 1, w: 3, h: 2}`), everything else in block style. The editor writes this order, so a diff of two saves shows what changed and an agent's file and the editor's
 * differ only in order and style. Formatting a file drops its comments; an edit through a command keeps them (`applyOps`).
 */

const ROOT = ['schemaVersion', 'kind', 'title', 'description', 'parameters', 'refresh', 'kiosk', 'theme', 'locale', 'strict', 'limits', 'cover', 'widgets', 'layouts']
const WIDGET = ['type', 'title', 'label', 'format', 'parameter', 'control', 'values', 'mark', 'encodings', 'query', 'on_select', 'drill', 'text', 'hash', 'media_type', 'alt', 'fit', 'url', 'with', 'link_text']
const LAYOUT = ['name', 'type', 'default', 'when', 'grid', 'canvas', 'overflow', 'min_scale', 'max_scale', 'page', 'header', 'footer', 'cover', 'widget_options', 'tiles', 'sections']
const TILE = ['widget', 'x', 'y', 'w', 'h', 'min', 'layer']
const PAGE = ['size', 'orientation', 'margins']
const MARGINS = ['top', 'right', 'bottom', 'left']
const SIDES = ['left', 'center', 'right']
const SECTION = ['title', 'break', 'blocks']
const BLOCK = ['widget', 'row', 'height', 'keep', 'repeat_header', 'pagebreak']
const CELL = ['widget', 'span']
const PARAMETER = ['name', 'type', 'for', 'many', 'required', 'default', 'label']

/** The canonical key order of the mapping at a path of the document (the document, a widget, a layout, a tile, a parameter), or none for a mapping whose order is its own. */
export function keyOrderAt(path: ReadonlyArray<string | number>): string[] | undefined {
  if (path.length === 0) return ROOT
  const [a, b, c, d] = path
  if (a === 'widgets' && path.length === 2) return WIDGET
  if (a === 'parameters' && path.length === 2) return PARAMETER
  if (a !== 'layouts' || typeof b !== 'number') return undefined
  if (path.length === 2) return LAYOUT
  if (c === 'tiles' && path.length === 4) return TILE
  if (c === 'page') return path.length === 3 ? PAGE : path.length === 4 && d === 'margins' ? MARGINS : undefined
  if ((c === 'header' || c === 'footer') && path.length === 3) return SIDES
  if (c === 'sections') return path.length === 4 ? SECTION : path.length === 6 && path[4] === 'blocks' ? BLOCK : path.length === 8 && path[6] === 'row' ? CELL : undefined
  return undefined
}

const isObject = (v: unknown): v is Record<string, unknown> => typeof v === 'object' && v !== null && !Array.isArray(v)

function ordered(object: Record<string, unknown>, order: string[]): Record<string, unknown> {
  const out: Record<string, unknown> = {}
  for (const k of order) if (k in object) out[k] = object[k]
  for (const k of Object.keys(object)) if (!(k in out)) out[k] = object[k]
  return out
}

/** A document as plain values with every known mapping in canonical key order (widgets and unknown keys keep their own order). */
export function canonicalValue(value: unknown): unknown {
  if (!isObject(value)) return value
  const root = ordered(value, ROOT)
  if (isObject(root.widgets)) root.widgets = Object.fromEntries(Object.entries(root.widgets).map(([id, w]) => [id, isObject(w) ? ordered(w, WIDGET) : w]))
  if (Array.isArray(root.parameters)) root.parameters = root.parameters.map((p) => (isObject(p) ? ordered(p, PARAMETER) : p))
  if (Array.isArray(root.layouts))
    root.layouts = root.layouts.map((l) => {
      if (!isObject(l)) return l
      const layout = ordered(l, LAYOUT)
      if (Array.isArray(layout.tiles)) layout.tiles = layout.tiles.map((t) => (isObject(t) ? ordered(t, TILE) : t))
      if (isObject(layout.page)) {
        const page = ordered(layout.page, PAGE)
        if (isObject(page.margins)) page.margins = ordered(page.margins, MARGINS)
        layout.page = page
      }
      for (const part of ['header', 'footer'] as const) if (isObject(layout[part])) layout[part] = ordered(layout[part] as Record<string, unknown>, SIDES)
      if (Array.isArray(layout.sections))
        layout.sections = layout.sections.map((section) => {
          if (!isObject(section)) return section
          const out = ordered(section, SECTION)
          if (Array.isArray(out.blocks))
            out.blocks = out.blocks.map((block) => {
              if (!isObject(block)) return block
              const b = ordered(block, BLOCK)
              if (Array.isArray(b.row)) b.row = b.row.map((cell) => (isObject(cell) ? ordered(cell, CELL) : cell))
              return b
            })
          return out
        })
      return layout
    })
  return root
}

const FLOW_WIDTH = 100

/** A short mapping or list of plain values reads on one line; a list of mappings, or anything that holds one, is a block. */
export function flowOk(value: unknown): boolean {
  if (Array.isArray(value)) return value.every((v) => !isObject(v) && flowOk(v)) && JSON.stringify(value).length <= FLOW_WIDTH
  if (isObject(value)) return Object.values(value).every(flowOk) && JSON.stringify(value).length <= FLOW_WIDTH
  return typeof value !== 'string' || !value.includes('\n')
}

/** Whether a collection at this path is always a block: the document, the two maps of widgets and layouts, each widget and layout. */
export const alwaysBlock = (path: ReadonlyArray<string | number>) => path.length === 0 || (path.length <= 2 && (path[0] === 'widgets' || path[0] === 'layouts'))

/** Sets the style of a created node and everything under it. `path` is where the node goes: the document, the two maps of widgets and layouts and each widget and layout are always blocks. */
export function styleNode(node: unknown, path: ReadonlyArray<string | number>): void {
  if (!isMap(node) && !isSeq(node)) return
  const plain = (node as Node & { toJSON(): unknown }).toJSON()
  ;(node as YAMLMap | YAMLSeq).flow = (node as YAMLMap | YAMLSeq).items.length === 0 || (!alwaysBlock(path) && flowOk(plain))
  const items = (node as YAMLMap | YAMLSeq).items as unknown[]
  for (let i = 0; i < items.length; i++) {
    const item = items[i]
    if (isMap(node)) {
      const pair = item as { key: unknown; value: unknown }
      if (!isScalar(pair.value)) styleNode(pair.value, [...path, String((pair.key as { value?: unknown })?.value ?? '')])
    } else if (!isScalar(item)) styleNode(item, [...path, i])
  }
}

/** The text of a document in canonical order and style. */
export function canonicalText(value: unknown): string {
  const doc = new Document()
  doc.contents = doc.createNode(canonicalValue(value)) as never
  styleNode(doc.contents, [])
  return doc.toString({ lineWidth: 0, flowCollectionPadding: false })
}
