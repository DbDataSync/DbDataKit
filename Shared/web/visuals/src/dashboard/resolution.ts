import type { ViewKind } from '../views/document'
import type { ParameterValues, ViewsDoc } from '../views/model'
import type { RenderedWidget } from '../views/rendered'
import type { ServerSort, ViewsClient } from '../client/types'

/** What a dashboard has resolved so far: the results by widget id, and the parameters they were resolved for. */
export interface Resolution {
  /** The parameters the results are for, as a stable string. */
  key: string
  /** The parameters the results are for (the partial re-resolution compares them with the next). */
  parameters: ParameterValues
  results: Record<string, RenderedWidget>
  asOf: string | null
  /** When each widget's result was read (the source's `as_of` of the resolution that gave it): a partial re-resolution or a refresh moves only the widgets it asked for. */
  asOfBy: Record<string, string>
}

/** A parameter value set as a stable string: its keys in order, so the same values are the same key. */
export const parametersKey = (parameters: ParameterValues): string => JSON.stringify(Object.keys(parameters).sort().map((k) => [k, parameters[k]]))

/** The values a document's parameters have before anyone changes them: each parameter's `default` (a list for `many`, else nothing). */
export function defaultParameters(document: Pick<ViewsDoc, 'parameters'>): ParameterValues {
  const values: ParameterValues = {}
  for (const p of document.parameters ?? []) values[p.name] = p.default !== undefined ? p.default : p.many ? [] : null
  return values
}

/** The parameter names a widget uses: `{param: name}` in its query and `params.name` in its text or titles. A control's own `parameter` is not a use (what it lists does not depend on its value). */
export function widgetParameters(widget: unknown): Set<string> {
  const json = JSON.stringify(widget ?? null)
  const names = new Set<string>()
  for (const m of json.matchAll(/"param":"([^"]+)"/g)) names.add(m[1]!)
  for (const m of json.matchAll(/params\.([A-Za-z_][A-Za-z0-9_]*)/g)) names.add(m[1]!)
  return names
}

/** The ids of the widgets that use any of the named parameters, in document order: the ones a change of those parameters resolves again (plan 32 section 3.5). */
export function dependentWidgets(document: Pick<ViewsDoc, 'widgets'>, changed: Iterable<string>): string[] {
  const wanted = new Set(changed)
  if (wanted.size === 0) return []
  return Object.entries(document.widgets).filter(([, w]) => [...widgetParameters(w)].some((n) => wanted.has(n))).map(([id]) => id)
}

/** The names whose value differs between two parameter sets. */
export function changedParameters(before: ParameterValues, after: ParameterValues): string[] {
  const same = (a: unknown, b: unknown) => JSON.stringify(a ?? null) === JSON.stringify(b ?? null)
  return [...new Set([...Object.keys(before), ...Object.keys(after)])].filter((n) => !same(before[n], after[n])).sort()
}

/**
 * Resolves the widgets a layout places (plan 32 section 2 item 6 (c), section 3.2, 3.5): through the client, as the viewer. **Switching** keeps the results of widgets both layouts place and asks only for the
 * newly placed ones (with the same parameters). **A change of parameters** asks again only for the placed widgets that use a changed parameter (`dependentWidgets`) and the ones with no result yet; every other
 * result is kept, and the results of dependent widgets the layout does not place are forgotten (they are stale). Results of other widgets the layout does not place are kept, for the way back. The client is
 * not asked at all when nothing is missing.
 */
export async function resolveLayout(args: {
  client: ViewsClient
  ref: { kind: ViewKind; name: string }
  layout: string
  placed: string[]
  parameters: ParameterValues
  previous: Resolution | null
  /** The document, to know which widgets use which parameter; without it a change of parameters asks for every placed widget again. */
  document?: Pick<ViewsDoc, 'widgets'>
  /** Ask again for every placed widget, whatever is kept (a refresh); the results of widgets this layout does not place stay. */
  force?: boolean
  /** The viewer's sorts of tables by widget id (DBR45), sent with every request so a re-resolution keeps them. */
  order?: Record<string, ServerSort>
  signal?: AbortSignal
}): Promise<Resolution> {
  const { client, ref, layout, placed, parameters, previous, document, signal, force = false, order } = args
  const key = parametersKey(parameters)
  const same = previous !== null && previous.key === key
  let have: Record<string, RenderedWidget> = {}
  if (force) have = Object.fromEntries(Object.entries(previous?.results ?? {}).filter(([id]) => !placed.includes(id)))
  else if (same) have = previous.results
  else if (previous !== null && document) {
    const stale = new Set(dependentWidgets(document, changedParameters(previous.parameters, parameters)))
    have = Object.fromEntries(Object.entries(previous.results).filter(([id]) => !stale.has(id)))
  }
  const needed = placed.filter((id) => !(id in have))
  if (needed.length === 0 && same && !force) return previous
  const fresh = needed.length === 0 ? null : await client.resolve(ref, layout, parameters, { only: needed.length === placed.length ? undefined : needed, ...(order && Object.keys(order).length > 0 ? { order } : {}), signal })
  const asOfBy: Record<string, string> = Object.fromEntries(Object.entries(previous?.asOfBy ?? {}).filter(([id]) => id in have))
  if (fresh) for (const id of Object.keys(fresh.widgets)) asOfBy[id] = fresh.as_of
  return { key, parameters, results: { ...have, ...fresh?.widgets }, asOf: fresh?.as_of ?? previous?.asOf ?? null, asOfBy }
}
