import type { LayoutDoc, ParameterDefinition, TileDoc, ViewsDoc, WidgetDoc, WidgetOptionsDoc, WidgetType } from '../views/model'
import type { CatalogRoles } from '../views/derive'
import type { Op, Path } from './yamlEdit'
import { WellError, convertWidget, wellOps, wellsOf } from './wells'
import type { WellChange } from './wells'
import { EditError } from './errors'
import { FLOW_COMMANDS, flowOps, type FlowCommand } from './flow'

/**
 * The edit commands of the editor (plan 32 DBR24): what a form, a drag or a menu asks of the document, each a small named change with its checks. `commandOps` turns one into the changes of the text
 * (`Op`s, applied by `applyOps`, which keeps comments and order), so a command is a pure function of the document as it stands and testable with no browser and no DOM.
 * A command never validates the document (the validator does, on every change); it refuses only what it cannot do (a name that is taken, a widget that is not there).
 */
export type EditCommand =
  | { type: 'setTitle'; title: string }
  | { type: 'setPath'; path: Path; value: unknown }
  | { type: 'removePath'; path: Path }
  | { type: 'addWidget'; id: string; widget: WidgetDoc }
  | { type: 'removeWidget'; id: string }
  | { type: 'renameWidget'; id: string; to: string }
  | { type: 'addParameter'; parameter: ParameterDefinition }
  | { type: 'removeParameter'; name: string }
  | { type: 'addLayout'; layout: LayoutDoc }
  | { type: 'duplicateLayout'; name: string; as: string }
  | { type: 'renameLayout'; name: string; to: string }
  | { type: 'deleteLayout'; name: string }
  | { type: 'moveLayout'; name: string; to: number }
  | { type: 'setDefaultLayout'; name: string }
  /** The layout's selector (`when`); absent removes it. */
  | { type: 'setSelector'; layout: string; when: Record<string, unknown> | undefined }
  | { type: 'placeTile'; layout: string; tile: TileDoc }
  | { type: 'setTile'; layout: string; widget: string; changes: Partial<TileDoc> }
  | { type: 'removeTile'; layout: string; widget: string }
  /** A change to one field well of a widget (DBR25): add a field (at a position), remove an entry, move one, clear. `roles` (the catalog's) make a field of the wrong kind refused in the engine's words. */
  | { type: 'setWell'; widget: string; well: string; change: WellChange; roles?: CatalogRoles }
  /** The widget as another type (the chooser): its title, model and filters stay, its fields go where they fit. `roles` (the catalog's) place them by kind. */
  | { type: 'changeWidgetType'; id: string; to: WidgetType; roles?: CatalogRoles }
  /** One of a layout's `widget_options` for one widget; undefined removes it (and the entry, and `widget_options`, when they are left empty). */
  | { type: 'setWidgetOption'; layout: string; widget: string; key: keyof WidgetOptionsDoc; value: WidgetOptionsDoc[keyof WidgetOptionsDoc] | undefined }
  /** The report editor's commands (DBR27): the page, running text, cover, sections, blocks and cells of a flow layout, and the report's own options. */
  | FlowCommand

export { EditError } from './errors'
export type { EditErrorCode } from './errors'

/** A widget id and a layout name: what the documents' keys allow (a letter, then letters, digits, `_` and `-`). */
export const NAME_PATTERN = /^[A-Za-z][A-Za-z0-9_-]*$/

const isObject = (v: unknown): v is Record<string, unknown> => typeof v === 'object' && v !== null && !Array.isArray(v)
const clone = <T>(v: T): T => structuredClone(v)

function checkName(kind: 'widget' | 'layout' | 'parameter', name: string) {
  if (!NAME_PATTERN.test(name)) throw new EditError('bad_name', `\`${name}\` is not a name for a ${kind}: use a letter, then letters, digits, \`_\` or \`-\`.`)
}

function layoutIndex(doc: ViewsDoc, name: string): number {
  const i = (doc.layouts ?? []).findIndex((l) => l.name === name)
  if (i < 0) throw new EditError('unknown_layout', `There is no layout named \`${name}\`; the layouts are ${(doc.layouts ?? []).map((l) => `\`${l.name}\``).join(', ') || 'none'}.`)
  return i
}

function widgetOf(doc: ViewsDoc, id: string): WidgetDoc {
  const widget = doc.widgets?.[id]
  if (!widget) throw new EditError('unknown_widget', `There is no widget named \`${id}\`; the widgets are ${Object.keys(doc.widgets ?? {}).map((w) => `\`${w}\``).join(', ') || 'none'}.`)
  return widget
}

function tileIndex(layout: LayoutDoc, widget: string): number {
  const tiles = (layout as { tiles?: TileDoc[] }).tiles
  const i = tiles ? tiles.findIndex((t) => t.widget === widget) : -1
  if (i < 0) throw new EditError('not_placeable', `The layout \`${layout.name}\` does not place \`${widget}\`.`)
  return i
}

/** Every place a flow layout names a widget (a block, or a cell of a row), as the path of the block or the cell and the path of the list that holds it. */
function flowUses(layout: LayoutDoc, li: number, id: string, wholeRows = false): Array<{ list: Path; index: number }> {
  const uses: Array<{ list: Path; index: number }> = []
  if (layout.type !== 'flow') return uses
  ;(layout.sections as Array<{ blocks?: Array<{ widget?: string; row?: Array<{ widget?: string }> }> }>).forEach((section, si) =>
    (section.blocks ?? []).forEach((block, bi) => {
      if (block.widget === id) uses.push({ list: ['layouts', li, 'sections', si, 'blocks'], index: bi })
      const cells = block.row ?? []
      // a row whose every cell is this widget goes whole: a row of no cells is not a document
      if (wholeRows && cells.length > 0 && cells.every((c) => c.widget === id)) { uses.push({ list: ['layouts', li, 'sections', si, 'blocks'], index: bi }); return }
      cells.forEach((cell, ci) => { if (cell.widget === id) uses.push({ list: ['layouts', li, 'sections', si, 'blocks', bi, 'row'], index: ci }) })
    }))
  return uses
}

/** Removals ordered so that a later one never shifts the position an earlier one used: deepest and highest index first. */
function removals(uses: Array<{ list: Path; index: number }>): Op[] {
  return [...uses]
    .sort((a, b) => b.list.length - a.list.length || b.index - a.index)
    .map((u) => ({ op: 'remove', path: [...u.list, u.index] }))
}

const isFlowCommand = (command: EditCommand): command is FlowCommand => FLOW_COMMANDS.has(command.type)

export function commandOps(doc: ViewsDoc, command: EditCommand): Op[] {
  if (isFlowCommand(command)) return flowOps(doc, command)
  switch (command.type) {
    case 'setTitle': return [{ op: 'set', path: ['title'], value: command.title }]
    case 'setPath': return [{ op: 'set', path: command.path, value: command.value }]
    case 'removePath': return [{ op: 'remove', path: command.path }]

    case 'addWidget': {
      checkName('widget', command.id)
      if (doc.widgets?.[command.id]) throw new EditError('name_taken', `There is already a widget named \`${command.id}\`.`)
      return [{ op: 'set', path: ['widgets', command.id], value: command.widget }]
    }
    case 'removeWidget': {
      widgetOf(doc, command.id)
      const ops: Op[] = []
      doc.layouts.forEach((layout, li) => {
        const tiles = (layout as { tiles?: TileDoc[] }).tiles
        const uses = [
          ...(tiles ?? []).flatMap((t, i) => (t.widget === command.id ? [{ list: ['layouts', li, 'tiles'] as Path, index: i }] : [])),
          ...flowUses(layout, li, command.id, true),
        ]
        ops.push(...removals(uses))
        if (layout.widget_options && command.id in layout.widget_options) ops.push({ op: 'remove', path: ['layouts', li, 'widget_options', command.id] })
      })
      ops.push({ op: 'remove', path: ['widgets', command.id] })
      return ops
    }
    case 'renameWidget': {
      widgetOf(doc, command.id)
      checkName('widget', command.to)
      if (command.to !== command.id && doc.widgets[command.to]) throw new EditError('name_taken', `There is already a widget named \`${command.to}\`.`)
      if (command.to === command.id) return []
      const ops: Op[] = [{ op: 'renameKey', path: ['widgets'], from: command.id, to: command.to }]
      doc.layouts.forEach((layout, li) => {
        const tiles = (layout as { tiles?: TileDoc[] }).tiles
        ;(tiles ?? []).forEach((t, i) => { if (t.widget === command.id) ops.push({ op: 'set', path: ['layouts', li, 'tiles', i, 'widget'], value: command.to }) })
        for (const use of flowUses(layout, li, command.id)) ops.push({ op: 'set', path: [...use.list, use.index, 'widget'], value: command.to })
        if (layout.widget_options && command.id in layout.widget_options) ops.push({ op: 'renameKey', path: ['layouts', li, 'widget_options'], from: command.id, to: command.to })
      })
      return ops
    }

    case 'addParameter': {
      checkName('parameter', command.parameter.name)
      if ((doc.parameters ?? []).some((p) => p.name === command.parameter.name)) throw new EditError('name_taken', `There is already a parameter named \`${command.parameter.name}\`.`)
      return doc.parameters
        ? [{ op: 'insert', path: ['parameters'], index: doc.parameters.length, value: command.parameter }]
        : [{ op: 'set', path: ['parameters'], value: [command.parameter] }]
    }
    case 'removeParameter': {
      const i = (doc.parameters ?? []).findIndex((p) => p.name === command.name)
      if (i < 0) throw new EditError('unknown_parameter', `There is no parameter named \`${command.name}\`.`)
      return (doc.parameters ?? []).length === 1 ? [{ op: 'remove', path: ['parameters'] }] : [{ op: 'remove', path: ['parameters', i] }]
    }

    case 'addLayout': {
      checkName('layout', command.layout.name)
      if ((doc.layouts ?? []).some((l) => l.name === command.layout.name)) throw new EditError('name_taken', `There is already a layout named \`${command.layout.name}\`.`)
      return [{ op: 'insert', path: ['layouts'], index: (doc.layouts ?? []).length, value: command.layout }]
    }
    case 'duplicateLayout': {
      const i = layoutIndex(doc, command.name)
      checkName('layout', command.as)
      if (doc.layouts.some((l) => l.name === command.as)) throw new EditError('name_taken', `There is already a layout named \`${command.as}\`.`)
      const copy = clone(doc.layouts[i]!) as unknown as Record<string, unknown>
      copy.name = command.as
      delete copy.default
      return [{ op: 'insert', path: ['layouts'], index: i + 1, value: copy }]
    }
    case 'renameLayout': {
      const i = layoutIndex(doc, command.name)
      checkName('layout', command.to)
      if (command.to !== command.name && doc.layouts.some((l) => l.name === command.to)) throw new EditError('name_taken', `There is already a layout named \`${command.to}\`.`)
      const ops: Op[] = [{ op: 'set', path: ['layouts', i, 'name'], value: command.to }]
      if (isObject(doc.kiosk) && (doc.kiosk as { layout?: string }).layout === command.name) ops.push({ op: 'set', path: ['kiosk', 'layout'], value: command.to })
      return command.to === command.name ? [] : ops
    }
    case 'deleteLayout': {
      const i = layoutIndex(doc, command.name)
      if (doc.layouts.length === 1) throw new EditError('last_layout', 'A document needs at least one layout; add another before deleting this one.')
      const ops: Op[] = [{ op: 'remove', path: ['layouts', i] }]
      // the default goes to the first layout left, so there is still exactly one
      if (doc.layouts[i]!.default) {
        const next = doc.layouts.findIndex((_, j) => j !== i)
        ops.push({ op: 'set', path: ['layouts', next, 'default'], value: true })
        ops.reverse()
      }
      return ops
    }
    case 'moveLayout': {
      const i = layoutIndex(doc, command.name)
      const to = Math.max(0, Math.min(doc.layouts.length - 1, command.to))
      return to === i ? [] : [{ op: 'move', path: ['layouts'], from: i, to }]
    }
    case 'setDefaultLayout': {
      const i = layoutIndex(doc, command.name)
      const ops: Op[] = doc.layouts.flatMap((l, j) => (j !== i && l.default ? [{ op: 'remove', path: ['layouts', j, 'default'] } as Op] : []))
      if (!doc.layouts[i]!.default) ops.push({ op: 'set', path: ['layouts', i, 'default'], value: true })
      return ops
    }

    case 'setSelector': {
      const i = layoutIndex(doc, command.layout)
      if (command.when === undefined) return doc.layouts[i]!.when === undefined ? [] : [{ op: 'remove', path: ['layouts', i, 'when'] }]
      return JSON.stringify(doc.layouts[i]!.when) === JSON.stringify(command.when) ? [] : [{ op: 'set', path: ['layouts', i, 'when'], value: command.when }]
    }

    case 'placeTile': {
      const li = layoutIndex(doc, command.layout)
      const layout = doc.layouts[li]!
      if (layout.type === 'flow') throw new EditError('not_placeable', `The layout \`${layout.name}\` is a flow layout: it places widgets in sections and blocks, not in tiles.`)
      widgetOf(doc, command.tile.widget)
      if (layout.tiles.some((t) => t.widget === command.tile.widget)) throw new EditError('already_placed', `The layout \`${layout.name}\` already places \`${command.tile.widget}\`.`)
      return [{ op: 'insert', path: ['layouts', li, 'tiles'], index: layout.tiles.length, value: command.tile }]
    }
    case 'setTile': {
      const li = layoutIndex(doc, command.layout)
      const ti = tileIndex(doc.layouts[li]!, command.widget)
      const tile = (doc.layouts[li] as { tiles: TileDoc[] }).tiles[ti]! as unknown as Record<string, unknown>
      const ops: Op[] = []
      for (const [key, value] of Object.entries(command.changes)) {
        if (value === undefined) { if (key in tile) ops.push({ op: 'remove', path: ['layouts', li, 'tiles', ti, key] }) }
        else if (JSON.stringify(tile[key]) !== JSON.stringify(value)) ops.push({ op: 'set', path: ['layouts', li, 'tiles', ti, key], value })
      }
      return ops
    }
    case 'removeTile': {
      const li = layoutIndex(doc, command.layout)
      return [{ op: 'remove', path: ['layouts', li, 'tiles', tileIndex(doc.layouts[li]!, command.widget)] }]
    }

    case 'setWell': {
      const widget = widgetOf(doc, command.widget)
      const spec = wellsOf(widget.type).find((w) => w.id === command.well)
      if (!spec) throw new EditError('unknown_well', `A ${widget.type} widget has no well named \`${command.well}\`; its wells are ${wellsOf(widget.type).map((w) => `\`${w.id}\``).join(', ') || 'none'}.`)
      try { return wellOps(widget, command.widget, spec, command.change, command.roles) } catch (e) { if (e instanceof WellError) throw new EditError(e.code, e.message); throw e }
    }
    case 'changeWidgetType': {
      const widget = widgetOf(doc, command.id)
      if (widget.type === command.to) return []
      try {
        const model = Object.values(doc.widgets).map((w) => (w as { query?: { model?: unknown } }).query?.model).find((m): m is string => typeof m === 'string') ?? ''
        return [{ op: 'set', path: ['widgets', command.id], value: convertWidget(widget, command.to, { roles: command.roles, model, parameter: doc.parameters?.[0]?.name }).widget }]
      } catch (e) { if (e instanceof WellError) throw new EditError(e.code === 'unknown_well' ? 'unknown_parameter' : e.code, e.message); throw e }
    }
    case 'setWidgetOption': {
      const li = layoutIndex(doc, command.layout)
      widgetOf(doc, command.widget)
      const layout = doc.layouts[li]!
      const all = layout.widget_options
      const mine: Record<string, unknown> = { ...all?.[command.widget] }
      if (command.value === undefined) delete mine[command.key]
      else mine[command.key] = command.value
      if (JSON.stringify(all?.[command.widget] ?? {}) === JSON.stringify(mine)) return []
      if (Object.keys(mine).length > 0) return [{ op: 'set', path: ['layouts', li, 'widget_options', command.widget], value: mine }]
      const others = Object.keys(all ?? {}).filter((k) => k !== command.widget)
      return [{ op: 'remove', path: others.length > 0 ? ['layouts', li, 'widget_options', command.widget] : ['layouts', li, 'widget_options'] }]
    }
  }
}
