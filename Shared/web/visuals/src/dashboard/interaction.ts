import type { DataValue } from '../types'
import type { ParameterDefinition, ParameterValue, ParameterValues, ViewsDoc, WidgetDoc } from '../views/model'
import type { RenderedWidget } from '../views/rendered'

/**
 * Cross-filter and drill (plan 32 section 3.5 items 2 and 3, DBR08), as pure functions: what a person pointed at (`Hit`) becomes the row of values it stands for (`rowOf`), the row becomes a change of one
 * declared parameter (`selectFrom`, exactly the change a `control` makes: a binding, not a computation) or the address of another document (`resolveDrill`, then `checkDrill` against the target's own parameters).
 * No component, no timer, no client: the renderers only report hits, and the host decides what to do with the answers.
 */

/** What a person pointed at in a widget: a chart's datum, a table's row (an index into the result's rows), a matrix's row label or cell (indexes into the result's rows and columns). */
export type Hit =
  | { kind: 'datum'; datum: Record<string, DataValue> }
  | { kind: 'row'; index: number }
  | { kind: 'matrix'; row: number; column?: number }

/** How it was pointed at: `additive` (shift, control or command held) adds to or removes from a list of chosen values; `drill` (alt held) follows the widget's `drill` where it also has an `on_select`. */
export interface HitHow { additive: boolean; drill: boolean }

export type Scalar = string | number | boolean
/** Field name to the value the pointed-at mark, row or cell stands for. */
export type Row = Record<string, DataValue>

/** What a widget does when pointed at. */
export interface OnSelect { set: string; from: string }
export interface DrillDoc { to: { dashboard?: string; report?: string; view?: string }; with?: Record<string, string> }
const onSelectOf = (widget: WidgetDoc | undefined): OnSelect | null => {
  const raw = (widget as { on_select?: unknown } | undefined)?.on_select as Partial<OnSelect> | undefined
  return raw && typeof raw.set === 'string' && typeof raw.from === 'string' ? { set: raw.set, from: raw.from } : null
}
const drillOf = (widget: WidgetDoc | undefined): DrillDoc | null => {
  const raw = (widget as { drill?: unknown } | undefined)?.drill as DrillDoc | undefined
  return raw && typeof raw === 'object' && raw.to && typeof raw.to === 'object' ? raw : null
}
/** The name of the link a widget follows when pointed at (`on_click`, DBR35): a link of the document's `links`. Null for a widget with none. */
const clickOf = (widget: WidgetDoc | undefined): string | null => {
  const raw = (widget as { on_click?: unknown } | undefined)?.on_click
  return typeof raw === 'string' ? raw : null
}
export { onSelectOf, drillOf, clickOf }

/** A rendered chart names a field with `_` where the model's name has `.` (`customers.region` is `customers_region`); a row is read by either spelling. */
export const datumKey = (field: string): string => field.replace(/\./g, '_')
/** A field's value in a row, by its model name or its chart spelling; `undefined` when the row has neither. */
export function valueOf(row: Row, field: string): DataValue | undefined {
  if (field in row) return row[field]
  const alias = datumKey(field)
  return alias in row ? row[alias] : undefined
}

/**
 * The values a hit stands for. A chart datum is its own row. A table row is its columns' fields (a cell's value `v`, else the text the server formatted). A matrix row label is the row fields' labels (the
 * `rows` encoding, outermost first), a matrix cell also the column fields' labels. A total row or total column stands for no one value and gives null, as does a hit that matches nothing.
 */
export function rowOf(widget: WidgetDoc | undefined, result: RenderedWidget | undefined, hit: Hit): Row | null {
  if (!widget || !result) return null
  if (hit.kind === 'datum') return widget.type === 'chart' ? { ...hit.datum } : null
  if (hit.kind === 'row') {
    if (widget.type !== 'table' || result.type !== 'table') return null
    const row = result.rows[hit.index]
    if (!row || row.total) return null
    const out: Row = {}
    result.columns.forEach((c, i) => {
      const cell = row.cells[i]
      if (cell) out[c.field] = cell.v !== undefined ? cell.v : cell.text
    })
    return out
  }
  if (widget.type !== 'matrix' || result.type !== 'matrix') return null
  const row = result.rows[hit.row]
  if (!row || row.total) return null
  const encodings = widget.encodings as { rows?: unknown; columns?: unknown }
  const names = (list: unknown) => (Array.isArray(list) ? list.map((n) => String(n)) : [])
  const out: Row = {}
  names(encodings.rows).forEach((f, i) => { if (row.labels[i] !== undefined) out[f] = row.labels[i]! })
  if (hit.column !== undefined) {
    const column = result.columns[hit.column]
    if (!column || column.total) return null
    names(encodings.columns).forEach((f, i) => { if (column.labels[i] !== undefined) out[f] = column.labels[i]! })
  }
  return out
}

/** What a parameter has when nothing is chosen: its `default`, else an empty list for `many`, else nothing. */
export function parameterDefault(definition: Pick<ParameterDefinition, 'default' | 'many'>): ParameterValue {
  return definition.default !== undefined ? definition.default : definition.many ? [] : null
}

/** A value as the parameter's type; undefined when it is not one (a word for an integer). */
export function coerceValue(definition: Pick<ParameterDefinition, 'type'>, raw: DataValue | ParameterValue | undefined): Scalar | undefined {
  if (raw === null || raw === undefined || Array.isArray(raw)) return undefined
  switch (definition.type) {
    case 'integer': {
      const n = typeof raw === 'number' ? raw : typeof raw === 'string' && /^-?\d+$/.test(raw.trim()) ? Number(raw) : NaN
      return Number.isInteger(n) ? n : undefined
    }
    case 'number': {
      const n = typeof raw === 'number' ? raw : typeof raw === 'string' && raw.trim() !== '' ? Number(raw) : NaN
      return Number.isFinite(n) ? n : undefined
    }
    case 'boolean': return typeof raw === 'boolean' ? raw : raw === 'true' ? true : raw === 'false' ? false : undefined
    default: return String(raw)
  }
}

/** The values a parameter has chosen now, as a list: a `many` parameter's items, one value for a single one, none when nothing is chosen. */
export function chosenValues(value: ParameterValue | undefined): Scalar[] {
  if (value === null || value === undefined) return []
  return Array.isArray(value) ? value : [value]
}

/** The values a widget's `on_select` has chosen now (what its selection chip shows and what a table highlights). Empty for a widget with no `on_select`. */
export function selectedOf(widget: WidgetDoc | undefined, values: ParameterValues): Scalar[] {
  const on = onSelectOf(widget)
  return on ? chosenValues(values[on.set]) : []
}

export type SelectOutcome =
  | { ok: true; parameter: string; value: ParameterValue; cleared: boolean }
  | { ok: false; code: 'no_on_select' | 'no_parameter' | 'no_value' | 'bad_value'; message: string }

/**
 * The change of a parameter a selection makes. The value is the row's `from` field read as the parameter's type. A plain selection sets the parameter to that value (a list of one for a `many` parameter);
 * selecting the value that is already the only one **clears** the selection, which puts the parameter back to its default; an `additive` selection of a `many` parameter adds the value to the list or takes it out.
 */
export function selectFrom(document: Pick<ViewsDoc, 'widgets' | 'parameters'>, widgetId: string, row: Row | null, values: ParameterValues, how: Pick<HitHow, 'additive'> = { additive: false }): SelectOutcome {
  const widget = document.widgets[widgetId]
  const on = onSelectOf(widget)
  if (!on) return { ok: false, code: 'no_on_select', message: `The widget \`${widgetId}\` has no \`on_select\`.` }
  const definition = (document.parameters ?? []).find((p) => p.name === on.set)
  if (!definition) return { ok: false, code: 'no_parameter', message: `Selecting sets \`${on.set}\`, a parameter the document does not declare.` }
  const raw = row ? valueOf(row, on.from) : undefined
  if (raw === undefined || raw === null) return { ok: false, code: 'no_value', message: `That ${row ? 'mark' : 'row'} has no value for \`${on.from}\`.` }
  const value = coerceValue(definition, raw)
  if (value === undefined) return { ok: false, code: 'bad_value', message: `\`${String(raw)}\` is not a ${definition.type}, which \`${on.set}\` is.` }
  const now = chosenValues(values[on.set])
  const has = now.some((v) => v === value)
  if (definition.many) {
    const next = how.additive ? (has ? now.filter((v) => v !== value) : [...now, value]) : has && now.length === 1 ? [] : [value]
    const cleared = next.length === 0
    return { ok: true, parameter: on.set, value: cleared ? parameterDefault(definition) : next, cleared }
  }
  return has ? { ok: true, parameter: on.set, value: parameterDefault(definition), cleared: true } : { ok: true, parameter: on.set, value, cleared: false }
}

/** The value that clears a selection of this parameter (its default). */
export function clearedValue(document: Pick<ViewsDoc, 'parameters'>, parameter: string): ParameterValue {
  const definition = (document.parameters ?? []).find((p) => p.name === parameter)
  return definition ? parameterDefault(definition) : null
}

// ---- drill ----

/** A drill with its values read: where it goes and the parameters it gives (not yet checked against what the target requires). */
export interface ResolvedDrill {
  kind: 'dashboard' | 'report' | 'view'
  name: string
  parameters: Record<string, ParameterValue>
}

export type DrillOutcome =
  | { ok: true; drill: ResolvedDrill }
  | { ok: false; code: 'no_drill' | 'bad_target' | 'no_value' | 'no_parameter'; message: string }

/**
 * A widget's `drill` for a pointed-at row (the link vocabulary of plan 32 GP06): `with` maps the target's parameters to `$field` (a field of the row), `$params.name` (a parameter of this document, its value
 * now) or a literal. A `$field` the row lacks, or a `$params` the document does not declare, is refused naming it; so is a row that has no value for the field.
 */
export function resolveDrill(document: Pick<ViewsDoc, 'widgets' | 'parameters'>, widgetId: string, row: Row | null, values: ParameterValues): DrillOutcome {
  const drill = drillOf(document.widgets[widgetId])
  if (!drill) return { ok: false, code: 'no_drill', message: `The widget \`${widgetId}\` has no \`drill\`.` }
  const targets = (['dashboard', 'report', 'view'] as const).filter((k) => typeof drill.to[k] === 'string')
  if (targets.length !== 1) return { ok: false, code: 'bad_target', message: 'A drill goes to exactly one dashboard, report or view.' }
  const kind = targets[0]!
  const parameters: Record<string, ParameterValue> = {}
  for (const [name, spec] of Object.entries(drill.with ?? {})) {
    if (spec.startsWith('$params.')) {
      const own = spec.slice('$params.'.length)
      if (!(document.parameters ?? []).some((p) => p.name === own)) return { ok: false, code: 'no_parameter', message: `\`${spec}\` names a parameter the document does not declare.` }
      parameters[name] = values[own] ?? null
    } else if (spec.startsWith('$')) {
      const field = spec.slice(1)
      const value = row ? valueOf(row, field) : undefined
      if (value === undefined || value === null) return { ok: false, code: 'no_value', message: `That ${row ? 'mark' : 'row'} has no value for \`${field}\`, which the drill passes as \`${name}\`.` }
      parameters[name] = value
    } else parameters[name] = spec
  }
  return { ok: true, drill: { kind, name: drill.to[kind]!, parameters } }
}

const given = (v: ParameterValue | undefined) => v !== null && v !== undefined && !(Array.isArray(v) && v.length === 0)

export type DrillCheck = { ok: true } | { ok: false; missing: string[]; unknown: string[]; message: string }

/**
 * The drill against the target's own parameters: every parameter the target requires and does not default must be given a value (`missing`), and every name given must be one the target declares (`unknown`:
 * the address would silently drop it). Without the target's definitions (a `view` target, a source that does not know it) nothing can be said and the drill passes.
 */
export function checkDrill(drill: ResolvedDrill, targetParameters: ParameterDefinition[] | undefined): DrillCheck {
  if (!targetParameters) return { ok: true }
  const missing = targetParameters.filter((p) => p.required === true && p.default === undefined && !given(drill.parameters[p.name])).map((p) => p.name)
  const unknown = Object.keys(drill.parameters).filter((n) => !targetParameters.some((p) => p.name === n))
  if (missing.length === 0 && unknown.length === 0) return { ok: true }
  const parts = [
    ...(missing.length ? [`does not give the required parameter${missing.length > 1 ? 's' : ''} ${missing.map((m) => `\`${m}\``).join(', ')}`] : []),
    ...(unknown.length ? [`gives ${unknown.map((m) => `\`${m}\``).join(', ')}, which ${drill.kind} \`${drill.name}\` does not declare`] : []),
  ]
  return { ok: false, missing, unknown, message: `The drill to ${drill.kind} \`${drill.name}\` ${parts.join(' and ')}.` }
}

// ---- a click on a chart ----

/** The datum of a Vega scene item (a mark), as a plain row of scalars; null for a click on nothing (the background, an axis) or on a mark that holds no data. */
export function datumFromItem(item: unknown): Record<string, DataValue> | null {
  const datum = (item as { datum?: unknown } | null | undefined)?.datum
  if (typeof datum !== 'object' || datum === null || Array.isArray(datum)) return null
  const row: Record<string, DataValue> = {}
  for (const [k, v] of Object.entries(datum)) if (v === null || typeof v === 'string' || typeof v === 'number' || typeof v === 'boolean') row[k] = v
  return Object.keys(row).length > 0 ? row : null
}

/** The modifiers of a pointer or key event as `HitHow`. */
export function howOf(event: { shiftKey?: boolean; ctrlKey?: boolean; metaKey?: boolean; altKey?: boolean } | null | undefined): HitHow {
  return { additive: Boolean(event?.shiftKey || event?.ctrlKey || event?.metaKey), drill: Boolean(event?.altKey) }
}

/** The listener a chart gives to its Vega view's `click`: reports the mark's datum and how it was clicked. */
export function chartClick(onHit: (hit: Hit, how: HitHow) => void): (event: unknown, item: unknown) => void {
  return (event, item) => {
    const datum = datumFromItem(item)
    if (datum) onHit({ kind: 'datum', datum }, howOf(event as Parameters<typeof howOf>[0]))
  }
}
