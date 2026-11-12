import { Issues, NAME, VIEW, at, isObject, join } from './common'
import { readLinkValues, type LinkRow, type LinkParams, type LinkValue, type LinkPrint } from './links'

/**
 * Actions (plan 32 section 9h part 4, DBR53): the `change` and `task` targets of the link vocabulary, with the shell's confirmation popup's words (`confirm`) and the fields the person fills in (`ask`).
 * Two halves like links.ts. `checkAction` is the publish check, with the codes and paths of `ViewsLoader.ReadAction` in .NET (`conformance/views` runs one set of documents through both). `resolveAction`
 * is pure: for a clicked row it derives what the popup says and the request to send, and `ActionsGolden` (`conformance/views/actions.json`) holds cases both languages derive the same. Nothing here touches the
 * DOM or the network.
 */

export const ACTION_TARGETS = ['change', 'task'] as const
export type ActionKind = (typeof ACTION_TARGETS)[number]
export type ActionOperation = 'insert' | 'edit' | 'delete' | 'run'
export const ASK_TYPES = ['string', 'number', 'integer', 'boolean', 'date'] as const
export type AskType = (typeof ASK_TYPES)[number]
export const MAX_ASKS = 20

export interface ActionAsk { name: string; label: string; type: AskType; choices: string[]; required: boolean; default?: string }
export interface ActionConfirmDoc { title?: string; show?: string[]; button?: string }
export interface ActionConfirm { title?: string; show: string[]; button?: string }

/** The `change` and `task` of a link as written. */
export interface ChangeDoc {
  model: string; entity: string
  insert?: Record<string, string | number | boolean>
  edit?: { key: string | number | boolean | Record<string, string | number | boolean>; version?: string | number | boolean }
  delete?: { key: string | number | boolean | Record<string, string | number | boolean>; version?: string | number | boolean }
  set?: Record<string, string | number | boolean>
}
export interface TaskDoc { package: string; task: string; inputs?: Record<string, string | number | boolean> }
export interface AskDoc { label?: string; type?: AskType; choices?: string[]; required?: boolean; default?: string | number | boolean }

/** An action link read: what it does (`operation`, the entity or task), the values it writes, the popup's words and the fields it asks for. */
export interface ActionInfo {
  kind: ActionKind
  operation: ActionOperation
  model?: string; entity?: string; package?: string; task?: string
  key: Record<string, LinkValue>
  version?: LinkValue
  set: Record<string, LinkValue>
  confirm: ActionConfirm
  ask: Record<string, ActionAsk>
}

const PLACEHOLDER = /\{\{\s*([A-Za-z_][A-Za-z0-9_.]*)\s*\}\}/g
const MODEL = /^[A-Za-z][A-Za-z0-9_.-]{0,127}$/
const ENTITY = /^[A-Za-z_][A-Za-z0-9_]{0,63}$/
const FIELD = /^[A-Za-z_][A-Za-z0-9_.]*$/
const VALUE_NAME = /^[A-Za-z][A-Za-z0-9_-]{0,63}$/

/** What `checkAction` returns: the action and the link-level keys it also carries. */
export interface ActionLinkRead { action: ActionInfo; thenKind: 'refresh' | 'none' | 'widgets'; thenWidgets: string[]; print?: LinkPrint; image?: LinkPrint; label?: string }

const oneLine = (v: unknown, max: number): v is string => typeof v === 'string' && v.length >= 1 && v.length <= max && ![...v].some((c) => c.charCodeAt(0) < 32 || c.charCodeAt(0) === 127)

/**
 * The publish checks of an action link, in the words and with the codes and paths of the .NET loader. `others` are the other target keys the link wrote (a link has one target). Returns what it read, or null when a
 * target-level fault means there is no link.
 */
export function checkAction(raw: Record<string, unknown>, kind: ActionKind, others: string[], path: string, context: { declared: Set<string>; widgetIds: Set<string> | null }, issues: Issues): ActionLinkRead | null {
  if (others.length > 0) { issues.add('DBD-106', path, `A link has one target: \`${kind}\` and ${others.map((t) => `\`${t}\``).join(', ')} are written together.`); return null }
  for (const key of ['with', 'new', 'edit', 'as', 'lock'])
    if (raw[key] !== undefined) issues.add('DBD-106', join(path, key), `\`${key}\` does not belong to an action: its popup is the shell's, and what it changes is under \`${kind}\`.`)

  const asks = readAsks(raw, path, issues)
  const values = (node: unknown, p: string, into: Record<string, LinkValue>) => {
    if (!isObject(node)) { issues.add('DBD-106', p, "An action's values must be a mapping."); return }
    for (const [name, value] of Object.entries(node)) {
      const vp = join(p, name)
      if (!VALUE_NAME.test(name)) { issues.add('DBD-106', vp, "A name is a field of the entity (or an input of the task): letters, digits, '-' and '_', starting with a letter."); continue }
      const v = actionValue(value, vp, context, asks, issues)
      if (v) into[name] = v
    }
  }

  const kp = join(path, kind)
  const set: Record<string, LinkValue> = {}
  const key: Record<string, LinkValue> = {}
  let version: LinkValue | undefined
  let operation: ActionOperation = 'run'
  let model: string | undefined, entity: string | undefined, pkg: string | undefined, task: string | undefined
  const node = raw[kind]
  if (!isObject(node)) issues.add('DBD-106', kp, `A link's \`${kind}\` must be a mapping.`)
  else if (kind === 'change') {
    for (const k of Object.keys(node)) if (!['model', 'entity', 'insert', 'edit', 'delete', 'set'].includes(k)) issues.add('DBD-104', join(kp, k), 'Unknown key `' + k + '` in a change.')
    model = word(node, 'model', kp, MODEL, 'a model name', issues)
    entity = word(node, 'entity', kp, ENTITY, 'an entity name', issues)
    const ops = (['insert', 'edit', 'delete'] as const).filter((k) => node[k] !== undefined)
    if (ops.length !== 1) issues.add('DBD-106', kp, ops.length === 0 ? 'A change is an `insert`, an `edit` or a `delete`.' : `A change is one of \`insert\`, \`edit\`, \`delete\`: ${ops.map((o) => `\`${o}\``).join(', ')} are written together.`)
    else {
      operation = ops[0]!
      const opPath = join(kp, operation)
      if (operation === 'insert') {
        values(node.insert, opPath, set)
        if (node.set !== undefined) issues.add('DBD-106', join(kp, 'set'), 'An insert gives its values under `insert`; `set` belongs to an `edit`.')
      } else {
        version = keyAndVersion(node[operation], opPath, operation, key, context, asks, issues)
        if (operation === 'edit') {
          if (node.set === undefined) issues.add('DBD-105', join(kp, 'set'), 'An edit says what it sets: add `set:` with the fields and their new values.')
          else {
            values(node.set, join(kp, 'set'), set)
            if (isObject(node.set) && Object.keys(node.set).length === 0) issues.add('DBD-106', join(kp, 'set'), 'An edit sets at least one field.')
          }
        } else if (node.set !== undefined) issues.add('DBD-106', join(kp, 'set'), 'A delete sets nothing.')
      }
    }
  } else {
    for (const k of Object.keys(node)) if (!['package', 'task', 'inputs'].includes(k)) issues.add('DBD-104', join(kp, k), 'Unknown key `' + k + '` in a task.')
    pkg = word(node, 'package', kp, MODEL, 'a package name', issues)
    task = word(node, 'task', kp, MODEL, 'a task name', issues)
    if (node.inputs !== undefined) values(node.inputs, join(kp, 'inputs'), set)
  }

  const confirm = readConfirm(raw, path, asks, issues)

  let thenKind: ActionLinkRead['thenKind'] = 'refresh'
  const thenWidgets: string[] = []
  if (raw.then !== undefined) {
    const tp = join(path, 'then')
    if (Array.isArray(raw.then)) {
      thenKind = 'widgets'
      if (raw.then.length < 1 || raw.then.length > 100) issues.add('DBD-106', tp, raw.then.length < 1 ? '`then` must have at least 1 entry.' : '`then` has at most 100 entries.')
      else raw.then.forEach((w, i) => {
        if (typeof w !== 'string' || !NAME.test(w)) issues.add('DBD-106', at(tp, i), 'A widget in `then` is a name.')
        else if (context.widgetIds && !context.widgetIds.has(w)) issues.add(VIEW('025'), at(tp, i), `\`then\` names \`${w}\`, which is not a widget of this document.`)
        else thenWidgets.push(w)
      })
    } else if (raw.then === 'refresh' || raw.then === 'none') thenKind = raw.then
    else issues.add('DBD-106', tp, '`then` (or a list of widgets) is one of: refresh, none.')
  }
  const choice = (k: 'print' | 'image'): LinkPrint | undefined => {
    if (raw[k] === undefined) return undefined
    if (['hidden', 'normal', 'link'].includes(raw[k] as string)) return raw[k] as LinkPrint
    issues.add('DBD-106', join(path, k), `\`${k}\` is one of: hidden, normal, link.`)
    return undefined
  }
  const print = choice('print')
  const image = choice('image')
  let label: string | undefined
  if (raw.label !== undefined) {
    if (oneLine(raw.label, 200)) label = raw.label
    else issues.add('DBD-106', join(path, 'label'), '`label` is one line of text, 1 to 200 characters.')
  }
  if (print === 'link') issues.add(VIEW('046'), join(path, 'print'), "`print: link` needs a target with an address of its own, and an action has none: its popup is the shell's.")

  const action: ActionInfo = { kind, operation, model, entity, package: pkg, task, key, version, set, confirm, ask: asks }
  return { action, thenKind, thenWidgets, print, image, label }
}

function word(node: Record<string, unknown>, key: string, path: string, rule: RegExp, what: string, issues: Issues): string | undefined {
  const v = node[key]
  if (v === undefined) { issues.add('DBD-105', join(path, key), `Required key \`${key}\` is missing.`); return undefined }
  if (typeof v === 'string' && rule.test(v)) return v
  issues.add('DBD-106', join(path, key), `\`${key}\` is ${what}.`)
  return undefined
}

function actionValue(raw: unknown, path: string, context: { declared: Set<string> }, asks: Record<string, ActionAsk>, issues: Issues): LinkValue | undefined {
  const one: Record<string, LinkValue> = {}
  const sub = new Issues()
  readLinkValues({ v: raw }, '', context, sub, one)
  for (const i of sub.list) issues.add(i.code, path, i.message, i.severity)
  const v = one.v
  if (!v) return undefined
  if (v.kind !== 'field' || !v.text.startsWith('ask.')) return v
  const name = v.text.slice(4)
  if (name in asks) return { ...v, kind: 'ask', text: name }
  issues.add(VIEW('064'), path, `\`$${v.text}\` names a field the action does not ask for.`)
  return undefined
}

function keyAndVersion(raw: unknown, path: string, operation: string, key: Record<string, LinkValue>, context: { declared: Set<string> }, asks: Record<string, ActionAsk>, issues: Issues): LinkValue | undefined {
  if (!isObject(raw)) { issues.add('DBD-106', path, `A ${operation}'s row must be a mapping.`); return undefined }
  for (const k of Object.keys(raw)) if (k !== 'key' && k !== 'version') issues.add('DBD-104', join(path, k), `Unknown key \`${k}\` in an ${operation}.`)
  if (raw.key === undefined) issues.add('DBD-105', join(path, 'key'), 'The row is named by its `key`.')
  else if (isObject(raw.key)) {
    for (const [name, value] of Object.entries(raw.key)) {
      const vp = join(join(path, 'key'), name)
      if (!VALUE_NAME.test(name)) { issues.add('DBD-106', vp, "A name is a field of the entity (or an input of the task): letters, digits, '-' and '_', starting with a letter."); continue }
      const v = actionValue(value, vp, context, asks, issues)
      if (v) key[name] = v
    }
  } else {
    const v = actionValue(raw.key, join(path, 'key'), context, asks, issues)
    if (v) key.key = v
  }
  return raw.version === undefined ? undefined : actionValue(raw.version, join(path, 'version'), context, asks, issues)
}

function readAsks(raw: Record<string, unknown>, path: string, issues: Issues): Record<string, ActionAsk> {
  const result: Record<string, ActionAsk> = {}
  if (raw.ask === undefined) return result
  const ap = join(path, 'ask')
  if (!isObject(raw.ask)) { issues.add('DBD-106', ap, '`ask` must be a mapping.'); return result }
  const entries = Object.entries(raw.ask)
  if (entries.length > MAX_ASKS) { issues.add('DBD-106', ap, `An action asks for at most ${MAX_ASKS} fields.`); return result }
  for (const [name, field] of entries) {
    const fp = join(ap, name)
    if (!VALUE_NAME.test(name)) { issues.add('DBD-106', fp, "An asked field has a name of letters, digits, '-' and '_', starting with a letter."); continue }
    if (!isObject(field)) { issues.add('DBD-106', fp, `The asked field \`${name}\` must be a mapping.`); continue }
    for (const k of Object.keys(field)) if (!['label', 'type', 'choices', 'required', 'default'].includes(k)) issues.add('DBD-104', join(fp, k), `Unknown key \`${k}\` in an asked field.`)
    let type: AskType = 'string'
    if (field.type !== undefined) {
      if ((ASK_TYPES as readonly string[]).includes(field.type as string)) type = field.type as AskType
      else issues.add('DBD-106', join(fp, 'type'), `\`type\` is one of: ${ASK_TYPES.join(', ')}.`)
    }
    const choices: string[] = []
    if (field.choices !== undefined) {
      const cp = join(fp, 'choices')
      if (!Array.isArray(field.choices)) issues.add('DBD-106', cp, '`choices` must be a list.')
      else if (field.choices.length < 1 || field.choices.length > 200) issues.add('DBD-106', cp, field.choices.length < 1 ? '`choices` must have at least 1 entry.' : '`choices` has at most 200 entries.')
      else field.choices.forEach((c, i) => { if (oneLine(c, 200)) choices.push(c); else issues.add('DBD-106', at(cp, i), 'A choice is one line of text, 1 to 200 characters.') })
      if (choices.length > 0 && !['string', 'integer', 'number'].includes(type)) issues.add('DBD-106', cp, '`choices` offers values of a text or a number field.')
    }
    let label = name
    if (field.label !== undefined) { if (oneLine(field.label, 200)) label = field.label; else issues.add('DBD-106', join(fp, 'label'), '`label` is one line of text, 1 to 200 characters.') }
    let required = true
    if (field.required !== undefined) { if (typeof field.required === 'boolean') required = field.required; else issues.add('DBD-106', join(fp, 'required'), '`required` is true or false.') }
    let def: string | undefined
    if (field.default !== undefined) {
      if (['string', 'number', 'boolean'].includes(typeof field.default)) def = String(field.default)
      else issues.add('DBD-106', join(fp, 'default'), 'A `default` is one value.')
    }
    if (def !== undefined && choices.length > 0 && !choices.includes(def)) issues.add('DBD-106', join(fp, 'default'), `The default \`${def}\` is not one of the choices.`)
    result[name] = { name, label, type, choices, required, default: def }
  }
  return result
}

function readConfirm(raw: Record<string, unknown>, path: string, asks: Record<string, ActionAsk>, issues: Issues): ActionConfirm {
  const empty: ActionConfirm = { show: [] }
  if (raw.confirm === undefined) return empty
  const cp = join(path, 'confirm')
  if (!isObject(raw.confirm)) { issues.add('DBD-106', cp, '`confirm` must be a mapping.'); return empty }
  for (const k of Object.keys(raw.confirm)) if (!['title', 'show', 'button'].includes(k)) issues.add('DBD-104', join(cp, k), `Unknown key \`${k}\` in a confirmation.`)
  const placeholder = (name: string, p: string) => {
    if (name.startsWith('ask.') && !(name.slice(4) in asks)) issues.add(VIEW('064'), p, `\`${name}\` names a field the action does not ask for.`)
  }
  let title: string | undefined
  if (raw.confirm.title !== undefined) {
    if (oneLine(raw.confirm.title, 300)) {
      title = raw.confirm.title
      for (const m of title.matchAll(PLACEHOLDER)) placeholder(m[1]!, join(cp, 'title'))
      if (title.includes('{{') && title.replace(PLACEHOLDER, '').includes('{{')) issues.add('DBD-106', join(cp, 'title'), 'A placeholder is `{{orders.number}}`: the name of a field of the row, or `{{ask.name}}`.')
    } else issues.add('DBD-106', join(cp, 'title'), '`title` is one line of text, 1 to 300 characters.')
  }
  const show: string[] = []
  if (raw.confirm.show !== undefined) {
    const sp = join(cp, 'show')
    if (!Array.isArray(raw.confirm.show)) issues.add('DBD-106', sp, '`show` must be a list.')
    else if (raw.confirm.show.length < 1 || raw.confirm.show.length > 20) issues.add('DBD-106', sp, raw.confirm.show.length < 1 ? '`show` must have at least 1 entry.' : '`show` has at most 20 entries.')
    else raw.confirm.show.forEach((f, i) => {
      if (typeof f === 'string' && FIELD.test(f)) { placeholder(f, at(sp, i)); show.push(f) } else issues.add('DBD-106', at(sp, i), 'A field shown is a field of the row (`orders.number`) or `ask.name`.')
    })
  }
  let button: string | undefined
  if (raw.confirm.button !== undefined) { if (oneLine(raw.confirm.button, 40)) button = raw.confirm.button; else issues.add('DBD-106', join(cp, 'button'), '`button` is one line of text, 1 to 40 characters.') }
  return { title, show, button }
}

// ---------------------------------------------------------------------------------------------------------------------------------------------
// the pure runtime: what the popup says, and what is sent

export type ActionScalar = string | number | boolean
/** What a person typed or picked for each `ask` field, by name (as text; a boolean as `true`/`false`). */
export type ActionAnswers = Record<string, string | undefined>

/** One value as the popup lists it: plain text, never markup. */
export const plain = (v: unknown): string => (v === null || v === undefined ? '' : v instanceof Date ? v.toISOString().slice(0, 10) : String(v))

/** What the confirmation popup shows and the request it sends for one clicked row (`resolveAction`). */
export interface ResolvedAction {
  link: string
  kind: ActionKind
  operation: ActionOperation
  /** The words, in the declaration's: the title with the row's values in it (plain text), the button. */
  title: string
  button: string
  /** A delete: Enter does not confirm it. */
  destructive: boolean
  /** `sales.orders` or `nightly.load`. */
  target: string
  /** The `show` fields with the row's values. */
  shows: { field: string; value: string }[]
  /** Everything that will be written (or given to the task), name and value verbatim, in the declaration's order. */
  writes: { name: string; value: string }[]
  /** The row's key (a change that names a row) with its values. */
  key: { name: string; value: string }[]
  /** The version the change is made over, or null for "the one at the moment of the call". */
  version: string | null
  /** The values the row (or the parameters) did not give: an action with any of these cannot be sent. */
  missing: string[]
  asks: ActionAsk[]
}

/** The default words when the declaration gives none. */
export function defaultTitle(action: Pick<ActionInfo, 'operation' | 'entity' | 'package' | 'task'>, label?: string): string {
  if (label) return label
  switch (action.operation) {
    case 'insert': return `Add a row to ${action.entity}`
    case 'edit': return `Change a row of ${action.entity}`
    case 'delete': return `Delete a row of ${action.entity}`
    default: return `Run ${action.package}.${action.task}`
  }
}

export function defaultButton(operation: ActionOperation): string {
  return operation === 'insert' ? 'Add the row' : operation === 'edit' ? 'Apply the change' : operation === 'delete' ? 'Delete the row' : 'Run the task'
}

function fromRow(name: string, row: LinkRow): unknown {
  return name in row ? row[name] : row[name.slice(name.lastIndexOf('.') + 1)]
}

/** One value of the action for a row: the literal, the row's field, the parameter or what was typed; undefined when there is none. */
export function actionValueOf(v: LinkValue, row: LinkRow, params: LinkParams, answers: ActionAnswers): unknown {
  if (v.kind === 'literal') return v.value
  if (v.kind === 'param') return params[v.text]
  if (v.kind === 'ask') return answers[v.text]
  return fromRow(v.text, row)
}

/** The text of a title or a `show` placeholder: a field of the row, or `ask.name`. */
function textOf(name: string, row: LinkRow, answers: ActionAnswers): string {
  return name.startsWith('ask.') ? plain(answers[name.slice(4)]) : plain(fromRow(name, row))
}

/**
 * For a clicked row: the confirmation popup's words (the title's placeholders and the `show` fields substituted as plain text), every value that will be written listed verbatim, and what is missing. A value that is an
 * `$ask.` answer is what the person typed; a value the row or the parameters do not give is `missing` (the popup says so and does not send). The widget supplies only the row.
 */
export function resolveAction(name: string, link: { action: ActionInfo; label?: string }, row: LinkRow = {}, params: LinkParams = {}, answers: ActionAnswers = {}): ResolvedAction {
  const a = link.action
  const missing: string[] = []
  const value = (label: string, v: LinkValue): string | undefined => {
    const raw = actionValueOf(v, row, params, answers)
    if (raw === undefined || raw === null || raw === '') { if (v.kind !== 'ask') missing.push(label); return v.kind === 'ask' ? '' : undefined }
    if (Array.isArray(raw)) return raw.map(plain).join(', ')
    return plain(raw)
  }
  const key: { name: string; value: string }[] = []
  for (const [k, v] of Object.entries(a.key)) { const t = value(k === 'key' ? 'the row key' : `key ${k}`, v); if (t !== undefined) key.push({ name: k, value: t }) }
  let version: string | null = null
  if (a.version) { const raw = actionValueOf(a.version, row, params, answers); version = raw === undefined || raw === null ? null : plain(raw) }
  const writes: { name: string; value: string }[] = []
  for (const [k, v] of Object.entries(a.set)) { const t = value(k, v); if (t !== undefined) writes.push({ name: k, value: t }) }
  const shows = a.confirm.show.map((field) => ({ field, value: textOf(field, row, answers) }))
  const title = a.confirm.title !== undefined ? a.confirm.title.replace(PLACEHOLDER, (_m, f: string) => textOf(f, row, answers)) : defaultTitle(a, link.label)
  return {
    link: name, kind: a.kind, operation: a.operation, title, button: a.confirm.button ?? defaultButton(a.operation), destructive: a.operation === 'delete',
    target: a.kind === 'change' ? `${a.model}.${a.entity}` : `${a.package}.${a.task}`, shows, writes, key, version, missing, asks: Object.values(a.ask),
  }
}

/** What is wrong with the answers the person gave: a required field left empty, a value that is not of the field's type or not one of its choices. Empty when all is well. */
export function checkAnswers(asks: ActionAsk[], answers: ActionAnswers): Record<string, string> {
  const problems: Record<string, string> = {}
  for (const f of asks) {
    const raw = (answers[f.name] ?? '').trim()
    if (raw === '') { if (f.required) problems[f.name] = `${f.label} is needed.`; continue }
    if (f.choices.length > 0 && !f.choices.includes(raw)) problems[f.name] = `${f.label} is one of: ${f.choices.join(', ')}.`
    else if (f.type === 'integer' && !/^-?\d+$/.test(raw)) problems[f.name] = `${f.label} is a whole number.`
    else if (f.type === 'number' && !Number.isFinite(Number(raw))) problems[f.name] = `${f.label} is a number.`
    else if (f.type === 'boolean' && raw !== 'true' && raw !== 'false') problems[f.name] = `${f.label} is true or false.`
    else if (f.type === 'date' && !/^\d{4}-\d{2}-\d{2}$/.test(raw)) problems[f.name] = `${f.label} is a date, yyyy-MM-dd.`
  }
  return problems
}

/** Reads the action of a link document that has `change` or `task`, without the document's parameters (a name in `$params.` is taken as declared): the checks are the publish's, this is for a link already accepted. */
export function describeActionDoc(raw: Record<string, unknown>, kind: ActionKind): ActionLinkRead {
  const anything = new (class extends Set<string> { override has(): boolean { return true } })()
  return checkAction(raw, kind, [], 'link', { declared: anything, widgetIds: null }, new Issues())!
}
