import type { LinkInfo, LinkRow, LinkScalar } from '../views/links'
import type { ParameterDefinition, ParameterValue } from '../views/model'
import { chartPalette } from '../theme'

/**
 * The conversation between the host (the console's views screens, the apps shell, the print page) and the sandboxed frame of a custom view (plan 32 section 9f, DBR31). The code's side is `@dbdatakit/views-sdk`
 * (`ToHost`, `FromHost`, `ViewProps`); this file is the host's: what it sends, and a strict reader of what a frame sends, since the frame is app code and its messages are data. A frame can only say that it is
 * ready, that it failed, how tall its content is, that a parameter of the document changed, or that a link the widget declares was followed for a row. It cannot choose a target, a route or a request.
 */

export type ViewMode = 'screen' | 'print' | 'image'
export type Scalar = LinkScalar
export type Row = LinkRow

/** The rows of each query the view declared, by the query's name. */
export type ViewData = Record<string, { columns?: string[]; rows: Row[]; error?: { code: string; message: string } }>

/** A link as the code sees it: where it goes (the target's address, when it has one), how it is shown and what it gives. */
export interface HostLinkInfo {
  route: string | null
  as: 'navigate' | 'dialog' | 'panel'
  values: Record<string, { kind: 'literal' | 'field' | 'param' | 'ask'; text: string; value?: Scalar }>
  print?: 'hidden' | 'normal' | 'link'
  label?: string
}

export interface ViewProps {
  data: ViewData
  params: Record<string, Scalar | Scalar[] | null>
  theme: { scheme: 'light' | 'dark'; tokens: Record<string, string> }
  size: { width: number; height: number | 'auto' }
  mode: ViewMode
  links: Record<string, HostLinkInfo>
}

/** What the code sends the host. */
export type ToHost =
  | { type: 'views:hello' }
  | { type: 'views:ready' }
  | { type: 'views:error'; message: string }
  | { type: 'views:setParam'; name: string; value: Scalar | Scalar[] | null }
  | { type: 'views:open'; link: string; row: Row }
  | { type: 'views:size'; height: number }

/** What the host sends the code. */
export type FromHost =
  | { type: 'views:init'; name: string; props: ViewProps }
  | { type: 'views:props'; props: ViewProps }
  | { type: 'views:closed'; link: string; saved: boolean }

const isScalar = (v: unknown): v is Scalar => typeof v === 'string' || typeof v === 'number' || typeof v === 'boolean'
const MAX_TEXT = 2000
const MAX_ROW_KEYS = 200

/** A message from a frame read as one of the shapes above, or null: an unknown type, a wrong field type, a huge value and a parameter value that is not a scalar, a list of scalars or null are all dropped. */
export function readToHost(data: unknown): ToHost | null {
  if (typeof data !== 'object' || data === null) return null
  const m = data as Record<string, unknown>
  switch (m.type) {
    case 'views:hello': return { type: 'views:hello' }
    case 'views:ready': return { type: 'views:ready' }
    case 'views:error': return typeof m.message === 'string' ? { type: 'views:error', message: m.message.slice(0, MAX_TEXT) } : null
    case 'views:size': return typeof m.height === 'number' && Number.isFinite(m.height) && m.height >= 0 && m.height < 1e7 ? { type: 'views:size', height: Math.round(m.height) } : null
    case 'views:setParam': {
      if (typeof m.name !== 'string' || m.name.length > 100) return null
      const v = m.value
      if (v === null || isScalar(v)) return { type: 'views:setParam', name: m.name, value: v as Scalar | null }
      if (Array.isArray(v) && v.length <= 1000 && v.every(isScalar)) return { type: 'views:setParam', name: m.name, value: v as Scalar[] }
      return null
    }
    case 'views:open': {
      if (typeof m.link !== 'string' || m.link.length > 100) return null
      const row = m.row === undefined ? {} : m.row
      if (typeof row !== 'object' || row === null || Array.isArray(row)) return null
      const entries = Object.entries(row as Record<string, unknown>)
      if (entries.length > MAX_ROW_KEYS) return null
      return { type: 'views:open', link: m.link, row: Object.fromEntries(entries.filter(([, v]) => v === null || isScalar(v))) as Row }
    }
    default: return null
  }
}

/** A link of the document as the code sees it (`route` is the target's address, which only the host that serves the app knows). */
export function hostLinkInfo(link: LinkInfo, route: string | null): HostLinkInfo {
  return {
    route: link.hasAddress ? route : null,
    as: link.as,
    values: Object.fromEntries(Object.entries(link.values).map(([k, v]) => [k, { kind: v.kind, text: v.text, ...(v.value === undefined ? {} : { value: v.value }) }])),
    ...(link.print ? { print: link.print } : {}),
    ...(link.label ? { label: link.label } : {}),
  }
}

/** The theme tokens the code styles with (`var(--sui-text)`): the chart palette's own colours, so a custom view and the standard widgets beside it are the same ink. */
export function themeTokens(scheme: 'light' | 'dark'): Record<string, string> {
  const p = chartPalette(scheme)
  return { 'sui-text': p.text, 'sui-muted': p.muted, 'sui-grid': p.grid, 'sui-axis': p.axis, 'sui-accent': p.categories[0]!, 'sui-bg': scheme === 'dark' ? '#0d1117' : '#ffffff' }
}

/** What the host does with a parameter the code asks to set: the change, or the reason it is refused (the document does not declare it, or the value is not of its type). */
export type SetParamOutcome = { ok: true; value: ParameterValue } | { ok: false; message: string }

export function checkSetParam(definitions: ParameterDefinition[] | undefined, name: string, value: Scalar | Scalar[] | null): SetParamOutcome {
  const definition = (definitions ?? []).find((p) => p.name === name)
  if (!definition) return { ok: false, message: `The view asked to set \`${name}\`, which the document does not declare.` }
  if (value === null) return { ok: true, value: definition.many ? [] : null }
  const list = Array.isArray(value) ? value : [value]
  if (!definition.many && Array.isArray(value)) return { ok: false, message: `\`${name}\` takes one value, not a list.` }
  const fits = (v: Scalar) => (definition.type === 'integer' ? typeof v === 'number' && Number.isInteger(v) : definition.type === 'number' ? typeof v === 'number' : definition.type === 'boolean' ? typeof v === 'boolean' : typeof v === 'string' || typeof v === 'number' || typeof v === 'boolean')
  if (!list.every(fits)) return { ok: false, message: `The view asked to set \`${name}\` to a value that is not a ${definition.type}.` }
  return { ok: true, value: definition.many ? list : (list[0] as Scalar) }
}
