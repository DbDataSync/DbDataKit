import { RUNNING_TOKENS } from '../views/document'
import type { FlowBlockDoc, FlowCellDoc, FlowLayoutDoc, FlowSectionDoc, ViewsDoc } from '../views/model'
import { EditError } from './errors'
import type { Op, Path } from './yamlEdit'

/**
 * The commands of the report editor (plan 32 DBR27): a flow layout's page, running text, cover, sections, blocks and the cells of a row, and the report's own options (`strict`, `limits`, the cover default).
 * Like every command they are pure functions of the document that give the changes of its text (`Op`s); they refuse only what they cannot do (a section that is not there, a widget that does not exist, a
 * span that is not a whole number) and leave the loader's rules (a row's spans add up to at most 12, a token the vocabulary lacks, a margin out of range) to the validator, which answers on every change.
 */
export type FlowCommand =
  /** The page's size (a named one, or `{w, h}` in millimetres) or orientation; absent removes it (and `page`, when it is left empty). */
  | { type: 'setPage'; layout: string; field: 'size' | 'orientation'; value: string | { w: number; h: number } | undefined }
  | { type: 'setMargin'; layout: string; side: 'top' | 'right' | 'bottom' | 'left'; value: number | undefined }
  /** One side of the header or footer; empty text removes it (and the part, when it is left empty). */
  | { type: 'setRunningText'; layout: string; part: 'header' | 'footer'; side: 'left' | 'center' | 'right'; text: string | undefined }
  | { type: 'setCover'; layout: string; parameters: boolean | undefined }
  /** A new section (at the end, or at `index`), with a first block that places `widget` when one is given. */
  | { type: 'addSection'; layout: string; title?: string; widget?: string; index?: number }
  | { type: 'removeSection'; layout: string; section: number }
  | { type: 'moveSection'; layout: string; section: number; to: number }
  | { type: 'setSection'; layout: string; section: number; key: 'title' | 'break'; value: string | undefined }
  | { type: 'addBlock'; layout: string; section: number; block: FlowBlockDoc; index?: number }
  | { type: 'removeBlock'; layout: string; section: number; block: number }
  | { type: 'moveBlock'; layout: string; section: number; block: number; to: number }
  /** `height` (millimetres), `keep` or `repeat_header` of a widget block (`keep` also of a row); absent removes it. */
  | { type: 'setBlock'; layout: string; section: number; block: number; key: 'height' | 'keep' | 'repeat_header'; value: number | boolean | undefined }
  | { type: 'addCell'; layout: string; section: number; block: number; cell: FlowCellDoc }
  | { type: 'setCell'; layout: string; section: number; block: number; cell: number; changes: Partial<FlowCellDoc> }
  | { type: 'removeCell'; layout: string; section: number; block: number; cell: number }
  /** The report's own options: `strict`, `limits.pages`, `limits.rows` and `cover.parameters` (the default of a flow layout with no cover of its own); absent removes it (and the group, when it is left empty). */
  | { type: 'setReportOption'; key: 'strict' | 'limits.pages' | 'limits.rows' | 'cover.parameters'; value: number | boolean | undefined }

export const FLOW_COMMANDS: ReadonlySet<string> = new Set(['setPage', 'setMargin', 'setRunningText', 'setCover', 'addSection', 'removeSection', 'moveSection', 'setSection', 'addBlock', 'removeBlock', 'moveBlock', 'setBlock', 'addCell', 'setCell', 'removeCell', 'setReportOption'])

const isObject = (v: unknown): v is Record<string, unknown> => typeof v === 'object' && v !== null && !Array.isArray(v)
const finite = (n: unknown): n is number => typeof n === 'number' && Number.isFinite(n)

function flowAt(doc: ViewsDoc, name: string): { layout: FlowLayoutDoc; li: number } {
  const li = (doc.layouts ?? []).findIndex((l) => l.name === name)
  if (li < 0) throw new EditError('unknown_layout', `There is no layout named \`${name}\`; the layouts are ${(doc.layouts ?? []).map((l) => `\`${l.name}\``).join(', ') || 'none'}.`)
  const layout = doc.layouts[li]!
  if (layout.type !== 'flow') throw new EditError('not_flow', `The layout \`${name}\` is a ${layout.type} layout, not a flow layout: it has no page, sections or blocks.`)
  return { layout, li }
}

function sectionAt(layout: FlowLayoutDoc, section: number): FlowSectionDoc {
  const s = (layout.sections as FlowSectionDoc[])[section]
  if (!s) throw new EditError('bad_index', `The layout \`${layout.name}\` has no section ${section + 1}; it has ${layout.sections.length}.`)
  return s
}

function blockAt(layout: FlowLayoutDoc, section: number, block: number): FlowBlockDoc {
  const s = sectionAt(layout, section)
  const b = s.blocks?.[block]
  if (!b) throw new EditError('bad_index', `Section ${section + 1} of \`${layout.name}\` has no block ${block + 1}; it has ${s.blocks?.length ?? 0}.`)
  return b
}

function widgetExists(doc: ViewsDoc, id: string) {
  if (!doc.widgets?.[id]) throw new EditError('unknown_widget', `There is no widget named \`${id}\`; the widgets are ${Object.keys(doc.widgets ?? {}).map((w) => `\`${w}\``).join(', ') || 'none'}.`)
}

function checkSpan(span: unknown) {
  if (!finite(span) || !Number.isInteger(span) || span < 1 || span > 12) throw new EditError('bad_value', `A span is a whole number of columns from 1 to 12, not \`${String(span)}\`.`)
}

function checkBlock(doc: ViewsDoc, block: FlowBlockDoc) {
  if ('widget' in block) widgetExists(doc, block.widget)
  else if ('row' in block) {
    if (block.row.length === 0) throw new EditError('bad_value', 'A row has at least one cell.')
    if (block.row.length > 12) throw new EditError('bad_value', 'A row has at most 12 cells.')
    for (const cell of block.row) { widgetExists(doc, cell.widget); checkSpan(cell.span) }
  }
}

/** Removing `path` from a mapping that holds only it removes the mapping too (up to, not including, the layout itself), so `page: {}` and `header: {}` are never left behind. */
function removeOps(doc: ViewsDoc, path: Path, floor = 2): Op[] {
  let at: Path = path
  while (at.length - 1 > floor) {
    let parent: unknown = doc
    for (const step of at.slice(0, -1)) parent = (parent as Record<string | number, unknown> | undefined)?.[step]
    if (isObject(parent) && Object.keys(parent).length === 1) at = at.slice(0, -1)
    else break
  }
  return [{ op: 'remove', path: at }]
}

function has(root: unknown, path: Path): boolean {
  let at: unknown = root
  for (const step of path) {
    if (at === null || typeof at !== 'object' || !(step in (at as object))) return false
    at = (at as Record<string | number, unknown>)[step]
  }
  return true
}

/** A value set, or removed (with its emptied parents) when it is absent; no change when it is not there to remove. */
function setOrRemove(doc: ViewsDoc, path: Path, value: unknown, floor = 2): Op[] {
  if (value === undefined) return has(doc, path) ? removeOps(doc, path, floor) : []
  return [{ op: 'set', path, value }]
}

export function flowOps(doc: ViewsDoc, command: FlowCommand): Op[] {
  switch (command.type) {
    case 'setReportOption': {
      if (doc.kind !== 'report') throw new EditError('not_flow', 'strict, limits and the cover default belong to a report.')
      const [group, key] = command.key.split('.') as [string, string | undefined]
      if (command.value !== undefined) {
        if (command.key === 'strict' && typeof command.value !== 'boolean') throw new EditError('bad_value', '`strict` is true or false.')
        if (group === 'limits' && (!finite(command.value) || !Number.isInteger(command.value))) throw new EditError('bad_value', `A limit is a whole number, not \`${String(command.value)}\`.`)
        if (command.key === 'cover.parameters' && typeof command.value !== 'boolean') throw new EditError('bad_value', '`cover.parameters` is true or false.')
      }
      return setOrRemove(doc, key ? [group, key] : [group], command.value, 0)
    }

    case 'setPage': {
      const { li } = flowAt(doc, command.layout)
      const v = command.value
      if (command.field === 'orientation' && v !== undefined && v !== 'portrait' && v !== 'landscape') throw new EditError('bad_value', `The orientation is \`portrait\` or \`landscape\`, not \`${String(v)}\`.`)
      if (command.field === 'size' && isObject(v) && !(finite(v.w) && finite(v.h))) throw new EditError('bad_value', 'A custom size gives a width `w` and a height `h` in millimetres.')
      return setOrRemove(doc, ['layouts', li, 'page', command.field], v)
    }
    case 'setMargin': {
      const { li } = flowAt(doc, command.layout)
      if (command.value !== undefined && !finite(command.value)) throw new EditError('bad_value', `A margin is a number of millimetres, not \`${String(command.value)}\`.`)
      return setOrRemove(doc, ['layouts', li, 'page', 'margins', command.side], command.value)
    }
    case 'setRunningText': {
      const { li } = flowAt(doc, command.layout)
      return setOrRemove(doc, ['layouts', li, command.part, command.side], command.text === '' ? undefined : command.text)
    }
    case 'setCover': {
      const { li } = flowAt(doc, command.layout)
      return setOrRemove(doc, ['layouts', li, 'cover', 'parameters'], command.parameters)
    }

    case 'addSection': {
      const { layout, li } = flowAt(doc, command.layout)
      if (command.widget !== undefined) widgetExists(doc, command.widget)
      const sections = layout.sections as FlowSectionDoc[]
      const index = Math.max(0, Math.min(sections.length, command.index ?? sections.length))
      const section: Record<string, unknown> = {}
      if (command.title) section.title = command.title
      section.blocks = command.widget !== undefined ? [{ widget: command.widget }] : []
      return [{ op: 'insert', path: ['layouts', li, 'sections'], index, value: section }]
    }
    case 'removeSection': {
      const { layout, li } = flowAt(doc, command.layout)
      sectionAt(layout, command.section)
      if (layout.sections.length === 1) throw new EditError('last_section', 'A flow layout needs at least one section; add another before removing this one.')
      return [{ op: 'remove', path: ['layouts', li, 'sections', command.section] }]
    }
    case 'moveSection': {
      const { layout, li } = flowAt(doc, command.layout)
      sectionAt(layout, command.section)
      const to = Math.max(0, Math.min(layout.sections.length - 1, command.to))
      return to === command.section ? [] : [{ op: 'move', path: ['layouts', li, 'sections'], from: command.section, to }]
    }
    case 'setSection': {
      const { layout, li } = flowAt(doc, command.layout)
      sectionAt(layout, command.section)
      if (command.key === 'break' && command.value !== undefined && command.value !== 'before' && command.value !== 'after') throw new EditError('bad_value', `A section breaks \`before\` or \`after\`, not \`${String(command.value)}\`.`)
      const value = command.key === 'title' && command.value === '' ? undefined : command.value
      return setOrRemove(doc, ['layouts', li, 'sections', command.section, command.key], value, 4)
    }

    case 'addBlock': {
      const { layout, li } = flowAt(doc, command.layout)
      const section = sectionAt(layout, command.section)
      checkBlock(doc, command.block)
      const index = Math.max(0, Math.min(section.blocks.length, command.index ?? section.blocks.length))
      return [{ op: 'insert', path: ['layouts', li, 'sections', command.section, 'blocks'], index, value: command.block }]
    }
    case 'removeBlock': {
      const { layout, li } = flowAt(doc, command.layout)
      blockAt(layout, command.section, command.block)
      return [{ op: 'remove', path: ['layouts', li, 'sections', command.section, 'blocks', command.block] }]
    }
    case 'moveBlock': {
      const { layout, li } = flowAt(doc, command.layout)
      blockAt(layout, command.section, command.block)
      const count = sectionAt(layout, command.section).blocks.length
      const to = Math.max(0, Math.min(count - 1, command.to))
      return to === command.block ? [] : [{ op: 'move', path: ['layouts', li, 'sections', command.section, 'blocks'], from: command.block, to }]
    }
    case 'setBlock': {
      const { layout, li } = flowAt(doc, command.layout)
      const block = blockAt(layout, command.section, command.block)
      if ('pagebreak' in block) throw new EditError('bad_value', 'A page break has no options.')
      if (command.key !== 'keep' && 'row' in block) throw new EditError('bad_value', `A row has \`keep\` only; \`${command.key}\` belongs to a widget block.`)
      const v = command.value
      if (v !== undefined) {
        if (command.key === 'height' && !finite(v)) throw new EditError('bad_value', `A height is a number of millimetres, not \`${String(v)}\`.`)
        if (command.key !== 'height' && typeof v !== 'boolean') throw new EditError('bad_value', `\`${command.key}\` is true or false.`)
      }
      return setOrRemove(doc, ['layouts', li, 'sections', command.section, 'blocks', command.block, command.key], v, 6)
    }

    case 'addCell': {
      const { layout, li } = flowAt(doc, command.layout)
      const block = blockAt(layout, command.section, command.block)
      if (!('row' in block)) throw new EditError('bad_value', 'Only a row has cells.')
      widgetExists(doc, command.cell.widget)
      checkSpan(command.cell.span)
      if (block.row.length >= 12) throw new EditError('bad_value', 'A row has at most 12 cells.')
      return [{ op: 'insert', path: ['layouts', li, 'sections', command.section, 'blocks', command.block, 'row'], index: block.row.length, value: command.cell }]
    }
    case 'setCell': {
      const { layout, li } = flowAt(doc, command.layout)
      const block = blockAt(layout, command.section, command.block)
      if (!('row' in block) || !block.row[command.cell]) throw new EditError('bad_index', `Block ${command.block + 1} has no cell ${command.cell + 1}.`)
      const cell = block.row[command.cell]!
      const ops: Op[] = []
      const base: Path = ['layouts', li, 'sections', command.section, 'blocks', command.block, 'row', command.cell]
      if (command.changes.widget !== undefined && command.changes.widget !== cell.widget) { widgetExists(doc, command.changes.widget); ops.push({ op: 'set', path: [...base, 'widget'], value: command.changes.widget }) }
      if (command.changes.span !== undefined && command.changes.span !== cell.span) { checkSpan(command.changes.span); ops.push({ op: 'set', path: [...base, 'span'], value: command.changes.span }) }
      return ops
    }
    case 'removeCell': {
      const { layout, li } = flowAt(doc, command.layout)
      const block = blockAt(layout, command.section, command.block)
      if (!('row' in block) || !block.row[command.cell]) throw new EditError('bad_index', `Block ${command.block + 1} has no cell ${command.cell + 1}.`)
      if (block.row.length === 1) throw new EditError('bad_value', 'A row has at least one cell: remove the block to take the row out.')
      return [{ op: 'remove', path: ['layouts', li, 'sections', command.section, 'blocks', command.block, 'row', command.cell] }]
    }
  }
}

/** The tokens a header or footer may hold, as the forms list them: the fixed ones, then `{params.<name>}` for each declared parameter. */
export function runningTokens(doc: Pick<ViewsDoc, 'parameters'>): string[] {
  return [...RUNNING_TOKENS, ...(doc.parameters ?? []).map((p) => `params.${p.name}`)].map((t) => `{${t}}`)
}

/** The tokens in a text that header and footer text does not have (what the validator refuses, `DBD-VIEW-030` and `DBD-VIEW-008`), as written between the braces; for a hint while typing. */
export function unknownTokens(text: string, doc: Pick<ViewsDoc, 'parameters'>): string[] {
  const known = new Set([...RUNNING_TOKENS, ...(doc.parameters ?? []).map((p) => `params.${p.name}`)])
  return [...text.matchAll(/\{([^{}]*)\}/g)].map((m) => m[1]!).filter((t) => !known.has(t))
}
