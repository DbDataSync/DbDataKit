import { Issues, NAME, VIEW, at, isObject, join } from './common'
import { ACTION_TARGETS, checkAction, describeActionDoc, type ActionInfo, type ActionKind } from './actions'

/**
 * The link vocabulary of plan 32 section 9f (DBR35), one for standard and custom widgets: a document's `links` map names each link, a widget's `on_click` (or a custom widget's `links` list) names the one it follows.
 * This file has two halves. `checkLinks` and `resolveLinkRefs` are the publish checks, the same codes and paths as `ViewsLoader.ReadLinks` in .NET (`conformance/views` runs one set of documents through both). The
 * rest is pure: `describeLink` reads a link, `linkValues` resolves its values for a clicked row, `linkQuery` writes them as the route's query string, `linkTypeFits` is the rule a value's type is checked by,
 * and `printTreatment` says what an export draws. Nothing here touches the DOM, the network or the shell; the shell opens a link (`as`) and the exporter draws it (`print`).
 */

export const LINK_TARGETS = ['dashboard', 'report', 'view', 'page', 'form'] as const
export type LinkTargetKind = (typeof LINK_TARGETS)[number]
export const LINK_AS = ['navigate', 'dialog', 'panel'] as const
export type LinkAs = (typeof LINK_AS)[number]
export const LINK_PRINT = ['hidden', 'normal', 'link'] as const
export type LinkPrint = (typeof LINK_PRINT)[number]
export const LINK_KEYS = ['dashboard', 'report', 'view', 'page', 'form', 'change', 'task', 'confirm', 'ask', 'with', 'new', 'edit', 'as', 'then', 'lock', 'print', 'image', 'label']
export const MAX_LINKS = 100

/** A parameter or field of a link's target, as an app check tells it: a type (the core's names, plus `date`), whether it is a list, and whether the link must give it. */
export interface LinkTargetParam { name: string; type?: string; many?: boolean; required?: boolean; default?: unknown }

/** A value a link gives its target: a literal, `$field` of the clicked row, or `$params.name` of the document. */
export interface LinkValue { kind: 'literal' | 'field' | 'param' | 'ask'; text: string; literalType: string; value?: string | number | boolean }

/** A link as written: the closed set of its keys. */
export interface LinkDoc {
  dashboard?: string; report?: string; view?: string; page?: string; form?: string
  /** An action (DBR53): an insert, edit or delete of a Store row, or a runner task, with the popup's `confirm` words and the `ask` fields (`views/actions.ts`). */
  change?: unknown; task?: unknown; confirm?: unknown; ask?: unknown
  with?: Record<string, string | number | boolean>; new?: Record<string, string | number | boolean>; edit?: Record<string, string | number | boolean>
  as?: LinkAs; then?: 'refresh' | 'none' | string[]; lock?: string[]; print?: LinkPrint; image?: LinkPrint; label?: string
}

/** A link read: its target, the values it gives and every default applied. */
export interface LinkInfo {
  name: string
  /** `change` or `task` for an action link (then `action` is set). */
  targetKind: LinkTargetKind | ActionKind
  target: string
  action?: ActionInfo
  valuesKey: 'with' | 'new' | 'edit'
  values: Record<string, LinkValue>
  as: LinkAs
  thenKind: 'refresh' | 'none' | 'widgets'
  thenWidgets: string[]
  lock: string[]
  /** `undefined` means "by the element's meaning" (see `printTreatment`). */
  print?: LinkPrint
  image?: LinkPrint
  label?: string
  /** Whether the target has an address of its own, so a PDF can hold a hyperlink to it: not where the shell shows it in a dialog or a side panel. */
  hasAddress: boolean
  /** The key of the target in the `targets` a check is given: `form:orders`. */
  targetKey: string
}

const VALUE_NAME = /^[A-Za-z][A-Za-z0-9_-]{0,63}$/
const ROW_FIELD = /^\$[A-Za-z_][A-Za-z0-9_.]*$/

/**
 * Whether a value of type `given` (a literal's JSON type, or a document parameter's type) fits a parameter or field of type `wanted`: the same type, an integer for a number, a string for a date (written
 * `yyyy-MM-dd`); a list only for a list. `ViewsLoader.LinkTypeFits` in .NET is this rule.
 */
export function linkTypeFits(given: string, givenMany: boolean, wanted: string, wantedMany: boolean): boolean {
  return givenMany === wantedMany && (wanted === 'any' || given === wanted || (wanted === 'number' && given === 'integer') || (wanted === 'date' && given === 'string'))
}

/** One value as written: `$params.name`, `$field` of the row, or a literal. */
export function describeValue(raw: string | number | boolean): LinkValue {
  if (typeof raw === 'string') {
    if (raw.startsWith('$params.')) return { kind: 'param', text: raw.slice(8), literalType: '' }
    if (raw.startsWith('$')) return { kind: 'field', text: raw.slice(1), literalType: '' }
    return { kind: 'literal', text: raw, literalType: 'string', value: raw }
  }
  if (typeof raw === 'boolean') return { kind: 'literal', text: String(raw), literalType: 'boolean', value: raw }
  return { kind: 'literal', text: String(raw), literalType: Number.isInteger(raw) ? 'integer' : 'number', value: raw }
}

/** Reads a link that passed `checkLinks` (or any object of the right shape). The target is the one of the five keys that is present. */
export function describeLink(name: string, doc: LinkDoc): LinkInfo {
  const actionKind = ACTION_TARGETS.find((k) => doc[k] !== undefined)
  if (actionKind) {
    const read = describeActionDoc(doc as Record<string, unknown>, actionKind)
    return actionLinkInfo(name, read.action, read)
  }
  const targetKind = LINK_TARGETS.find((k) => typeof doc[k] === 'string')!
  const isForm = targetKind === 'form'
  const valuesKey = isForm ? (doc.edit !== undefined && doc.new === undefined ? 'edit' : 'new') : 'with'
  const values: Record<string, LinkValue> = {}
  for (const [k, v] of Object.entries(doc[valuesKey] ?? {})) values[k] = describeValue(v)
  const as = doc.as ?? 'navigate'
  const list = Array.isArray(doc.then)
  return {
    name, targetKind, target: doc[targetKind]!, valuesKey, values, as,
    thenKind: list ? 'widgets' : (doc.then as 'refresh' | 'none' | undefined) ?? (isForm ? 'refresh' : 'none'),
    thenWidgets: list ? [...(doc.then as string[])] : [], lock: [...(doc.lock ?? [])],
    print: doc.print, image: doc.image, label: doc.label,
    hasAddress: as === 'navigate', targetKey: `${targetKind}:${doc[targetKind]}`,
  }
}

/** The link of an action read: its target is `model.entity` or `package.task`; `as` is the shell's popup, which has no address. */
function actionLinkInfo(name: string, action: ActionInfo, read: { thenKind: 'refresh' | 'none' | 'widgets'; thenWidgets: string[]; print?: LinkPrint; image?: LinkPrint; label?: string }): LinkInfo {
  const target = action.kind === 'change' ? `${action.model}.${action.entity}` : `${action.package}.${action.task}`
  return {
    name, targetKind: action.kind, target, action, valuesKey: action.kind === 'change' ? 'edit' : 'with', values: action.set, as: 'dialog',
    thenKind: read.thenKind, thenWidgets: read.thenWidgets, lock: [], print: read.print, image: read.image, label: read.label, hasAddress: false, targetKey: `${action.kind}:${target}`,
  }
}

// ---------------------------------------------------------------------------------------------------------------------------------------------
// the publish checks

/** What the checks need of the document around a link. */
export interface LinkContext {
  declared: Set<string>
  /** The type and list-ness of each declared parameter. */
  parameterTypes: Map<string, { type: string; many: boolean }>
  /** The widget ids, once the widgets are read; null skips the check of `then`'s widgets. */
  widgetIds: Set<string> | null
  /** The parameters or fields of the targets a link may lead to, keyed `dashboard:name`, `report:name`, `view:name`, `page:name`, `form:name`; null skips every check that needs the app. */
  targets?: Record<string, LinkTargetParam[]>
}

/** The `links` map of a document: shape, values, `then`, `lock`, `print`, and (with `targets`) the checks that need the app. Returns the links read. */
export function checkLinks(raw: unknown, context: LinkContext, issues: Issues, base = 'links'): Record<string, LinkInfo> {
  const found: Record<string, LinkInfo> = {}
  if (!isObject(raw)) { issues.add('DBD-106', base, '`links` must be a mapping.'); return found }
  const entries = Object.entries(raw)
  if (entries.length > MAX_LINKS) { issues.add('DBD-106', base, `A document has at most ${MAX_LINKS} links.`); return found }
  for (const [name, link] of entries) {
    if (!NAME.test(name)) { issues.add('DBD-106', join(base, name), `\`${name}\` is not a link name: lower-case letters, digits, '-' and '_', starting with a letter.`); continue }
    const info = checkLink(name, link, join(base, name), context, issues)
    if (info) found[name] = info
  }
  return found
}

function checkLink(name: string, raw: unknown, path: string, context: LinkContext, issues: Issues): LinkInfo | null {
  if (!isObject(raw)) { issues.add('DBD-106', path, `The link \`${name}\` must be a mapping.`); return null }
  for (const key of Object.keys(raw)) if (!LINK_KEYS.includes(key)) issues.add('DBD-104', join(path, key), `Unknown key \`${key}\` in a link.`)
  const actionTargets = ACTION_TARGETS.filter((k) => raw[k] !== undefined)
  if (actionTargets.length > 0) {
    const others = [...actionTargets.slice(1), ...LINK_TARGETS.filter((k) => raw[k] !== undefined)]
    const read = checkAction(raw, actionTargets[0]!, others, path, { declared: context.declared, widgetIds: context.widgetIds }, issues)
    return read ? actionLinkInfo(name, read.action, read) : null
  }
  const targets = LINK_TARGETS.filter((k) => raw[k] !== undefined)
  if (targets.length !== 1) {
    issues.add('DBD-106', path, targets.length === 0
      ? 'A link names its target: one of `dashboard`, `report`, `view`, `page`, `form`, `change` or `task`.'
      : `A link has one target: ${targets.map((t) => `\`${t}\``).join(', ')} are written together.`)
    return null
  }
  const kind = targets[0]!
  const targetName = typeof raw[kind] === 'string' && NAME.test(raw[kind] as string) ? (raw[kind] as string) : undefined
  if (targetName === undefined) issues.add('DBD-106', join(path, kind), `A link's \`${kind}\` is a name: lower-case letters, digits, '-' and '_', starting with a letter (at most 64).`)
  const isForm = kind === 'form'

  for (const key of ['with', 'new', 'edit'] as const)
    if (raw[key] !== undefined && (isForm ? key === 'with' : key !== 'with'))
      issues.add('DBD-106', join(path, key), isForm ? 'A form is given its fields with `new` (a row to create) or `edit` (a row to change), not `with`.' : `\`${key}\` belongs to a form link; a ${kind} is given its parameters with \`with\`.`)
  if (isForm && raw.new !== undefined && raw.edit !== undefined) issues.add('DBD-106', join(path, 'edit'), 'A form link creates a row (`new`) or changes one (`edit`), not both.')

  const valuesKey = isForm ? (raw.edit !== undefined && raw.new === undefined ? 'edit' : 'new') : 'with'
  const values: Record<string, LinkValue> = {}
  if (raw[valuesKey] !== undefined) readLinkValues(raw[valuesKey], join(path, valuesKey), context, issues, values)

  let as: LinkAs = 'navigate'
  if (raw.as !== undefined) {
    if ((LINK_AS as readonly string[]).includes(raw.as as string)) as = raw.as as LinkAs
    else issues.add('DBD-106', join(path, 'as'), `\`as\` is one of: ${LINK_AS.join(', ')}.`)
  }

  let thenKind: LinkInfo['thenKind'] = isForm ? 'refresh' : 'none'
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

  const lock: string[] = []
  if (raw.lock !== undefined) {
    const lp = join(path, 'lock')
    if (!isForm) issues.add('DBD-106', lp, '`lock` belongs to a form link: it makes a prefilled field read-only.')
    else if (!Array.isArray(raw.lock)) issues.add('DBD-106', lp, '`lock` must be a list.')
    else if (raw.lock.length < 1 || raw.lock.length > 100) issues.add('DBD-106', lp, raw.lock.length < 1 ? '`lock` must have at least 1 entry.' : '`lock` has at most 100 entries.')
    else raw.lock.forEach((f, i) => {
      if (typeof f !== 'string' || !VALUE_NAME.test(f)) issues.add('DBD-106', at(lp, i), 'A locked field is a field name of the form.')
      else if (!(f in values)) issues.add('DBD-106', at(lp, i), `\`${f}\` is locked but not given: lock a field that \`${valuesKey}\` fills.`)
      else lock.push(f)
    })
  }

  const choice = (key: 'print' | 'image'): LinkPrint | undefined => {
    if (raw[key] === undefined) return undefined
    if ((LINK_PRINT as readonly string[]).includes(raw[key] as string)) return raw[key] as LinkPrint
    issues.add('DBD-106', join(path, key), `\`${key}\` is one of: ${LINK_PRINT.join(', ')}.`)
    return undefined
  }
  const print = choice('print')
  const image = choice('image')
  let label: string | undefined
  if (raw.label !== undefined) {
    if (typeof raw.label === 'string' && raw.label.length >= 1 && raw.label.length <= 200 && ![...raw.label].some((c) => c.charCodeAt(0) < 32 || c.charCodeAt(0) === 127)) label = raw.label
    else issues.add('DBD-106', join(path, 'label'), '`label` is one line of text, 1 to 200 characters.')
  }
  if (print === 'link' && as !== 'navigate')
    issues.add(VIEW('046'), join(path, 'print'), `\`print: link\` needs a target with an address of its own, and \`as: ${as}\` shows it in a frame of the shell.`)

  if (targetName === undefined) return null
  const info: LinkInfo = {
    name, targetKind: kind, target: targetName, valuesKey, values, as, thenKind, thenWidgets, lock, print, image, label,
    hasAddress: as === 'navigate', targetKey: `${kind}:${targetName}`,
  }
  checkTarget(info, path, context, issues)
  return info
}

/** The values a link (or a custom widget) gives, read with the codes and paths of the .NET loader: a name that is a parameter or field of the target, a value that is a row field, a declared parameter or a literal. */
export function readLinkValues(raw: unknown, path: string, context: Pick<LinkContext, 'declared'>, issues: Issues, values: Record<string, LinkValue>): void {
  if (!isObject(raw)) { issues.add('DBD-106', path, "A link's values must be a mapping."); return }
  for (const [name, value] of Object.entries(raw)) {
    const vp = join(path, name)
    if (!VALUE_NAME.test(name)) { issues.add('DBD-106', vp, "A name is a parameter or field of the target: letters, digits, '-' and '_', starting with a letter."); continue }
    if (typeof value !== 'string' && typeof value !== 'number' && typeof value !== 'boolean') { issues.add('DBD-106', vp, 'A value is `$field` of the clicked row, `$params.name`, or a literal.'); continue }
    const v = describeValue(value)
    if (v.kind === 'param') {
      if (!context.declared.has(v.text)) { issues.add(VIEW('008'), vp, `\`${value}\` names a parameter the document does not declare.`); continue }
    } else if (v.kind === 'field' && !ROW_FIELD.test(value as string)) { issues.add('DBD-106', vp, '`$` starts a field of the clicked row (`$orders.id`) or a parameter (`$params.name`).'); continue }
    values[name] = v
  }
}

function checkTarget(link: LinkInfo, path: string, context: LinkContext, issues: Issues): void {
  if (!context.targets || link.action) return
  const theirs = context.targets[link.targetKey]
  if (!theirs) {
    issues.add(VIEW('039'), join(path, link.targetKind), `The link \`${link.name}\` leads to the ${link.targetKind} \`${link.target}\`, which this app does not have: a link stays in the app instance.`)
    return
  }
  const byName = new Map(theirs.map((p) => [p.name, p]))
  const valuesPath = join(path, link.valuesKey)
  for (const [name, value] of Object.entries(link.values)) {
    const vp = join(valuesPath, name)
    const param = byName.get(name)
    if (!param) { issues.add(VIEW('044'), vp, `The ${link.targetKind} \`${link.target}\` has no ${link.targetKind === 'form' ? 'field' : 'parameter'} \`${name}\`.`); continue }
    if (value.kind === 'field') continue // the clicked row's type is the catalog's to say
    const own = value.kind === 'param' ? context.parameterTypes.get(value.text) : undefined
    const given = value.kind === 'literal' ? value.literalType : own?.type ?? 'string'
    const givenMany = own?.many ?? false
    const wanted = param.type ?? 'string'
    const wantedMany = param.many ?? false
    if (!linkTypeFits(given, givenMany, wanted, wantedMany))
      issues.add(VIEW('045'), vp, `\`${name}\` of the ${link.targetKind} \`${link.target}\` is ${wantedMany ? 'a list of ' : ''}${wanted}, and this value is ${givenMany ? 'a list of ' : ''}${given}.`)
  }
  link.lock.forEach((f, i) => { if (!byName.has(f)) issues.add(VIEW('044'), at(join(path, 'lock'), i), `The form \`${link.target}\` has no field \`${f}\` to lock.`) })
  if (link.valuesKey !== 'new')
    for (const p of theirs.filter((p) => p.required === true && p.default === undefined && !(p.name in link.values)))
      issues.add(VIEW('037'), path, `The link \`${link.name}\` to the ${link.targetKind} \`${link.target}\` does not give its required ${link.targetKind === 'form' ? 'key' : 'parameter'} \`${p.name}\`.`)
}

/** A widget's `on_click` (or a custom widget's `links`) names links the document declares. Not made for a widgets map read alone: it has no links. */
export function resolveLinkRefs(refs: { name: string; path: string }[], links: Record<string, LinkInfo>, issues: Issues): void {
  const names = Object.keys(links)
  for (const { name, path } of refs)
    if (!(name in links)) issues.add(VIEW('036'), path, `\`${name}\` is not a link of this document.${names.length === 0 ? ' Declare it under `links`.' : ` Links: ${names.join(', ')}.`}`)
}

// ---------------------------------------------------------------------------------------------------------------------------------------------
// the pure runtime: what a click carries, how it is written into the route, what an export draws

export type LinkScalar = string | number | boolean
/** The document's parameter values by name (a list for a `many` parameter); the values a link reads with `$params.name`. */
export type LinkParams = Record<string, LinkScalar | LinkScalar[] | null | undefined>
/** The row a link was followed from: field name (as the widget's query outputs it) to value. */
export type LinkRow = Record<string, unknown>

/**
 * The values a link gives its target for a clicked row: a literal as written, `$field` from the row (looked up by the full name `orders.id`, else by the part after the last dot, which is how a query's output
 * is aliased), `$params.name` from the parameters. A value that has nothing (a field the row lacks, a parameter with no value, null) is left out, never sent as the text `undefined`: the target then uses its own
 * default or reports it required. A value is a default and never authority (the target validates, the server enforces).
 */
export function linkValues(link: LinkInfo, row: LinkRow = {}, params: LinkParams = {}): Record<string, LinkScalar | LinkScalar[]> {
  const out: Record<string, LinkScalar | LinkScalar[]> = {}
  for (const [name, v] of Object.entries(link.values)) {
    let value: unknown
    if (v.kind === 'literal') value = v.value
    else if (v.kind === 'param') value = params[v.text]
    else value = v.text in row ? row[v.text] : row[v.text.slice(v.text.lastIndexOf('.') + 1)]
    if (value === undefined || value === null) continue
    if (Array.isArray(value)) { const list = value.filter((x): x is LinkScalar => ['string', 'number', 'boolean'].includes(typeof x)); if (list.length) out[name] = list; continue }
    if (value instanceof Date) { out[name] = value.toISOString().slice(0, 10); continue }
    if (typeof value === 'string' || typeof value === 'number' || typeof value === 'boolean') out[name] = value
    else out[name] = String(value)
  }
  return out
}

/** The values as a query string for the route (`region=EMEA&year=2026`): names in order, a list repeats its name, nothing for an empty value; no leading `?`. */
export function linkQuery(values: Record<string, LinkScalar | LinkScalar[]>): string {
  const parts: string[] = []
  for (const name of Object.keys(values).sort()) {
    const v = values[name]!
    for (const one of Array.isArray(v) ? v : [v]) parts.push(`${encodeURIComponent(name)}=${encodeURIComponent(String(one))}`)
  }
  return parts.join('&')
}

/** What an element of a page is to the person: a value she reads (text, a status icon chosen by a field's value, a badge) or only an affordance (a pencil, an open arrow, an action button). */
export type ElementMeaning = 'value' | 'affordance'
export type PrintMedium = 'print' | 'image'

/**
 * What an export draws for a link's element (DBD-37, decision D9). The developer's `print` (and `image`, for a PNG, which defaults to `print`) decides; where she said nothing the element's meaning does: a value
 * is `normal` (content a printed report needs), an affordance is `hidden`. Nothing is `link` unless she says so. A link whose target has no address is never `link` (the check refuses it; this is the same rule
 * for a link that arrives unchecked), and in a PNG `link` is `normal` because an image holds no annotation.
 */
export function printTreatment(link: Pick<LinkInfo, 'print' | 'image' | 'hasAddress'>, meaning: ElementMeaning, medium: PrintMedium = 'print'): LinkPrint {
  let t: LinkPrint = (medium === 'image' ? link.image ?? link.print : link.print) ?? (meaning === 'value' ? 'normal' : 'hidden')
  if (t === 'link' && !link.hasAddress) t = meaning === 'value' ? 'normal' : 'hidden'
  if (t === 'link' && medium === 'image') t = 'normal'
  return t
}

/** Whether a value is a name a link may be given where a link is named (a widget's `on_click`). */
export const isLinkName = (v: unknown): v is string => typeof v === 'string' && NAME.test(v)
