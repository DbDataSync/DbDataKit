import { LINK_KEYS, LINK_TARGETS } from '../views/links'
import type { LinkDoc, LinkTargetKind } from '../views/links'
import type { ViewsDoc } from '../views/model'
import { EditError } from './errors'
import type { Op } from './yamlEdit'

/**
 * The commands of the link editor (plan 32 DBR38): a document's `links` (its target, the values it gives, how the shell presents it, what re-runs, what an export draws) and the link a widget follows on a click.
 * Like every command they are pure functions of the document that give the changes of its text (`Op`s); they refuse only what they cannot do (a name that is taken, a link that is not there) and leave the loader's
 * rules (a target the app lacks, a type that does not fit, `print: link` in a dialog) to the validator, which answers on every change.
 */
export type LinkCommand =
  | { type: 'addLink'; name: string; link: LinkDoc }
  /** Removes the link and, from every widget that followed it on a click, the `on_click`. */
  | { type: 'removeLink'; name: string }
  /** Renames the link and the `on_click` of every widget that follows it. */
  | { type: 'renameLink'; name: string; to: string }
  /** Points the link at another target. The values stay (they are the new target's to accept); moving to or from a form moves them between `with` and `new`, and leaving a form drops `lock`. */
  | { type: 'setLinkTarget'; name: string; kind: LinkTargetKind; target: string }
  /** One value the link gives (`$field` of the clicked row, `$params.name`, or a literal); undefined removes it (and the group, and a `lock` of that field). */
  | { type: 'setLinkValue'; name: string; param: string; value: string | number | boolean | undefined }
  /** `edit` instead of `new` on a form: the values move to the other group. */
  | { type: 'setFormMode'; name: string; mode: 'new' | 'edit' }
  /** `as`, `then`, `print`, `image` or `label` (and an action's `stage` and `for`); undefined removes it. */
  | { type: 'setLinkOption'; name: string; key: 'as' | 'then' | 'print' | 'image' | 'label' | 'stage' | 'for'; value: unknown }
  /** A new action link (plan 32 DBR53): a `change` (an insert, an edit or a delete of a Store row) or a `task`, with the values it writes; the popup's words are set after. */
  | { type: 'addActionLink'; name: string; action: NewAction }
  /** The popup's words of an action link (`title`, `button`) or `skip` (a field edit applies at once, `confirm: skip`); undefined removes it. */
  | { type: 'setActionConfirm'; name: string; key: 'title' | 'button' | 'skip'; value: string | boolean | undefined }
  /** One value an action writes (`set` of an edit, `insert`, a task's `inputs`): `$field` of the clicked row, `$params.name`, `$ask.name` or a literal; undefined removes it. */
  | { type: 'setActionValue'; name: string; field: string; value: string | number | boolean | undefined }
  /** Locks or unlocks one field of a form link. */
  | { type: 'setLinkLock'; name: string; field: string; locked: boolean }
  /** The link a widget follows on a click; undefined removes it. */
  | { type: 'setOnClick'; widget: string; link: string | undefined }

/** What `addActionLink` is made of: the change's model and entity and what it does to the row, or the task's package and name. */
export type NewAction =
  | { kind: 'change'; model: string; entity: string; operation: 'edit' | 'insert' | 'delete'; /** The row's key as a value (`$orders.id`); not for an insert. */ key?: string; /** The row's version as a value (`$orders.version`). */ version?: string; /** What an edit sets or an insert gives. */ values?: Record<string, string | number | boolean> }
  | { kind: 'task'; package: string; task: string; inputs?: Record<string, string | number | boolean> }

export const LINK_COMMANDS: ReadonlySet<string> = new Set(['addActionLink', 'setActionConfirm', 'setActionValue', 'addLink', 'removeLink', 'renameLink', 'setLinkTarget', 'setLinkValue', 'setFormMode', 'setLinkOption', 'setLinkLock', 'setOnClick'])

const NAME = /^[a-z][a-z0-9_-]{0,63}$/

function linkOf(doc: ViewsDoc, name: string): LinkDoc {
  const link = (doc.links as Record<string, LinkDoc> | undefined)?.[name]
  if (!link) throw new EditError('unknown_link', `There is no link named \`${name}\`; the links are ${Object.keys(doc.links ?? {}).map((l) => `\`${l}\``).join(', ') || 'none'}.`)
  return link
}

const targetOf = (link: LinkDoc): LinkTargetKind | undefined => LINK_TARGETS.find((k) => typeof link[k] === 'string')
const valuesKeyOf = (link: LinkDoc): 'with' | 'new' | 'edit' => (targetOf(link) === 'form' ? (link.edit !== undefined && link.new === undefined ? 'edit' : 'new') : 'with')

/** The widgets that follow a link on a click. */
export const followers = (doc: ViewsDoc, name: string): string[] => Object.entries(doc.widgets ?? {}).filter(([, w]) => (w as { on_click?: unknown }).on_click === name).map(([id]) => id)

/** The link as the file writes it: the keys of the vocabulary in its own order. */
const written = (link: Record<string, unknown>): Record<string, unknown> => {
  const out: Record<string, unknown> = {}
  for (const k of LINK_KEYS) if (k in link) out[k] = link[k]
  for (const k of Object.keys(link)) if (!(k in out)) out[k] = link[k]
  return out
}

export function linkOps(doc: ViewsDoc, command: LinkCommand): Op[] {
  switch (command.type) {
    case 'addLink': {
      if (!NAME.test(command.name)) throw new EditError('bad_name', `\`${command.name}\` is not a name for a link: lower-case letters, digits, \`_\` or \`-\`, starting with a letter.`)
      if ((doc.links as Record<string, unknown> | undefined)?.[command.name]) throw new EditError('name_taken', `There is already a link named \`${command.name}\`.`)
      return [{ op: 'set', path: ['links', command.name], value: written(command.link as Record<string, unknown>) }]
    }
    case 'addActionLink': {
      if (!NAME.test(command.name)) throw new EditError('bad_name', `\`${command.name}\` is not a name for a link: lower-case letters, digits, \`_\` or \`-\`, starting with a letter.`)
      if ((doc.links as Record<string, unknown> | undefined)?.[command.name]) throw new EditError('name_taken', `There is already a link named \`${command.name}\`.`)
      const a = command.action
      let body: Record<string, unknown>
      if (a.kind === 'task') {
        if (a.package.length === 0 || a.task.length === 0) throw new EditError('bad_name', 'A task link names its package and its task.')
        body = { task: { package: a.package, task: a.task, ...(a.inputs && Object.keys(a.inputs).length > 0 ? { inputs: a.inputs } : {}) } }
      } else {
        if (a.model.length === 0 || a.entity.length === 0) throw new EditError('bad_name', 'A change link names its model and its entity.')
        const change: Record<string, unknown> = { model: a.model, entity: a.entity }
        if (a.operation === 'insert') change.insert = a.values ?? {}
        else {
          if (!a.key) throw new EditError('bad_name', `A ${a.operation} names the row by its key: give a value such as \`$orders.id\`.`)
          change[a.operation] = { key: a.key, ...(a.version ? { version: a.version } : {}) }
          if (a.operation === 'edit') change.set = a.values ?? {}
        }
        body = { change }
      }
      return [{ op: 'set', path: ['links', command.name], value: written(body) }]
    }
    case 'setActionConfirm': {
      const link = linkOf(doc, command.name) as Record<string, unknown>
      if (link.change === undefined && link.task === undefined) throw new EditError('not_an_action', `The link \`${command.name}\` is not an action.`)
      if (command.key === 'skip') {
        if (command.value === true) return link.confirm === 'skip' ? [] : [{ op: 'set', path: ['links', command.name, 'confirm'], value: 'skip' }]
        return link.confirm === 'skip' ? [{ op: 'remove', path: ['links', command.name, 'confirm'] }] : []
      }
      const confirm: Record<string, unknown> = typeof link.confirm === 'object' && link.confirm !== null ? { ...(link.confirm as Record<string, unknown>) } : {}
      if (command.value === undefined || command.value === '') delete confirm[command.key]
      else confirm[command.key] = command.value
      if (Object.keys(confirm).length === 0) return link.confirm === undefined ? [] : [{ op: 'remove', path: ['links', command.name, 'confirm'] }]
      return [{ op: 'set', path: ['links', command.name, 'confirm'], value: confirm }]
    }
    case 'setActionValue': {
      const link = linkOf(doc, command.name) as Record<string, unknown>
      const change = link.change as Record<string, unknown> | undefined
      const task = link.task as Record<string, unknown> | undefined
      if (!change && !task) throw new EditError('not_an_action', `The link \`${command.name}\` is not an action.`)
      const section = task ? 'inputs' : change!.insert !== undefined ? 'insert' : change!.delete !== undefined ? '' : 'set'
      if (section === '') throw new EditError('nothing_to_set', 'A delete writes no value.')
      const holder = (task ?? change) as Record<string, unknown>
      const values = { ...((holder[section] as Record<string, unknown> | undefined) ?? {}) }
      if (command.value === undefined) { if (!(command.field in values)) return []; delete values[command.field] }
      else {
        if (!/^[A-Za-z][A-Za-z0-9_-]{0,63}$/.test(command.field)) throw new EditError('bad_name', `\`${command.field}\` is not a field name.`)
        values[command.field] = command.value
      }
      const path = ['links', command.name, task ? 'task' : 'change', section]
      return Object.keys(values).length === 0 && section === 'inputs' ? [{ op: 'remove', path }] : [{ op: 'set', path, value: values }]
    }
    case 'removeLink': {
      linkOf(doc, command.name)
      const ops: Op[] = followers(doc, command.name).map((id) => ({ op: 'remove', path: ['widgets', id, 'on_click'] }) as Op)
      ops.push({ op: 'remove', path: Object.keys(doc.links ?? {}).length === 1 ? ['links'] : ['links', command.name] })
      return ops
    }
    case 'renameLink': {
      linkOf(doc, command.name)
      if (!NAME.test(command.to)) throw new EditError('bad_name', `\`${command.to}\` is not a name for a link: lower-case letters, digits, \`_\` or \`-\`, starting with a letter.`)
      if (command.to === command.name) return []
      if ((doc.links as Record<string, unknown>)[command.to]) throw new EditError('name_taken', `There is already a link named \`${command.to}\`.`)
      return [
        { op: 'renameKey', path: ['links'], from: command.name, to: command.to },
        ...followers(doc, command.name).map((id) => ({ op: 'set', path: ['widgets', id, 'on_click'], value: command.to }) as Op),
      ]
    }
    case 'setLinkTarget': {
      const link = linkOf(doc, command.name)
      if (!NAME.test(command.target)) throw new EditError('bad_name', `\`${command.target}\` is not a name for a target: lower-case letters, digits, \`_\` or \`-\`, starting with a letter.`)
      const was = targetOf(link)
      const values = (link[valuesKeyOf(link)] ?? undefined) as Record<string, unknown> | undefined
      const next: Record<string, unknown> = {}
      next[command.kind] = command.target
      if (values) next[command.kind === 'form' ? 'new' : 'with'] = values
      for (const k of ['as', 'then', 'lock', 'print', 'image', 'label'] as const) {
        if (k === 'lock' && command.kind !== 'form') continue
        if (link[k] !== undefined) next[k] = link[k]
      }
      if (was === command.kind && link[command.kind] === command.target) return []
      if (was === 'form' && command.kind !== 'form') { /* lock and new/edit are dropped above */ }
      return [{ op: 'set', path: ['links', command.name], value: written(next) }]
    }
    case 'setFormMode': {
      const link = linkOf(doc, command.name)
      if (targetOf(link) !== 'form') throw new EditError('not_a_form', `The link \`${command.name}\` does not lead to a form.`)
      const from = valuesKeyOf(link)
      if (from === command.mode) return []
      const values = link[from]
      const next: Record<string, unknown> = { ...link }
      delete next[from]
      if (values !== undefined) next[command.mode] = values
      return [{ op: 'set', path: ['links', command.name], value: written(next) }]
    }
    case 'setLinkValue': {
      const link = linkOf(doc, command.name)
      const key = valuesKeyOf(link)
      const values = { ...((link[key] as Record<string, unknown> | undefined) ?? {}) }
      if (command.value === undefined) {
        if (!(command.param in values)) return []
        delete values[command.param]
      } else {
        if (!/^[A-Za-z][A-Za-z0-9_-]{0,63}$/.test(command.param)) throw new EditError('bad_name', `\`${command.param}\` is not a name of a parameter or field.`)
        values[command.param] = command.value
      }
      const ops: Op[] = []
      const lock = link.lock
      if (command.value === undefined && Array.isArray(lock) && lock.includes(command.param)) {
        const left = lock.filter((f) => f !== command.param)
        ops.push(left.length === 0 ? { op: 'remove', path: ['links', command.name, 'lock'] } : { op: 'set', path: ['links', command.name, 'lock'], value: left })
      }
      ops.push(Object.keys(values).length === 0 ? { op: 'remove', path: ['links', command.name, key] } : { op: 'set', path: ['links', command.name, key], value: values })
      return ops
    }
    case 'setLinkOption': {
      const link = linkOf(doc, command.name) as Record<string, unknown>
      if (command.value === undefined) return command.key in link ? [{ op: 'remove', path: ['links', command.name, command.key] }] : []
      if (JSON.stringify(link[command.key]) === JSON.stringify(command.value)) return []
      return [{ op: 'set', path: ['links', command.name, command.key], value: command.value }]
    }
    case 'setLinkLock': {
      const link = linkOf(doc, command.name)
      if (targetOf(link) !== 'form') throw new EditError('not_a_form', `The link \`${command.name}\` does not lead to a form: only a form has fields to lock.`)
      const lock = Array.isArray(link.lock) ? [...link.lock] : []
      const has = lock.includes(command.field)
      if (command.locked === has) return []
      const left = command.locked ? [...lock, command.field] : lock.filter((f) => f !== command.field)
      return left.length === 0 ? [{ op: 'remove', path: ['links', command.name, 'lock'] }] : [{ op: 'set', path: ['links', command.name, 'lock'], value: left }]
    }
    case 'setOnClick': {
      const widget = doc.widgets?.[command.widget]
      if (!widget) throw new EditError('unknown_widget', `There is no widget named \`${command.widget}\`; the widgets are ${Object.keys(doc.widgets ?? {}).map((w) => `\`${w}\``).join(', ') || 'none'}.`)
      if (widget.type !== 'chart' && widget.type !== 'matrix' && widget.type !== 'table') throw new EditError('not_clickable', `A ${widget.type} widget has no click: only a chart, a matrix or a table follows a link.`)
      const has = (widget as { on_click?: unknown }).on_click
      if (command.link === undefined) return has === undefined ? [] : [{ op: 'remove', path: ['widgets', command.widget, 'on_click'] }]
      linkOf(doc, command.link)
      if (has === command.link) return []
      // a widget follows `on_click` or `drill`, not both: choosing a link replaces the drill written in place
      const ops: Op[] = (widget as { drill?: unknown }).drill !== undefined ? [{ op: 'remove', path: ['widgets', command.widget, 'drill'] }] : []
      ops.push({ op: 'set', path: ['widgets', command.widget, 'on_click'], value: command.link })
      return ops
    }
  }
}
