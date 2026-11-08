import type { ViewKind } from '../views/document'
import type { ParameterValues, ViewsDoc } from '../views/model'
import type { RenderedWidget } from '../views/rendered'
import type { ViewsClient } from '../client/types'

/** What a dashboard has resolved so far: the results by widget id, and the parameters they were resolved for. */
export interface Resolution {
  /** The parameters the results are for, as a stable string. */
  key: string
  results: Record<string, RenderedWidget>
  asOf: string | null
}

/** A parameter value set as a stable string: its keys in order, so the same values are the same key. */
export const parametersKey = (parameters: ParameterValues): string => JSON.stringify(Object.keys(parameters).sort().map((k) => [k, parameters[k]]))

/** The values a document's parameters have before anyone changes them: each parameter's `default` (a list for `many`, else nothing). */
export function defaultParameters(document: Pick<ViewsDoc, 'parameters'>): ParameterValues {
  const values: ParameterValues = {}
  for (const p of document.parameters ?? []) values[p.name] = p.default !== undefined ? p.default : p.many ? [] : null
  return values
}

/**
 * Resolves the widgets a layout places (plan 32 section 2 item 6 (c), section 3.2): through the client, as the viewer. **Switching** keeps the results of widgets both layouts place and asks only for the
 * newly placed ones (with the same parameters); a change of parameters asks for every placed widget again. Results of a widget the new layout does not place are kept, for the way back. The client is
 * not asked at all when nothing is missing.
 */
export async function resolveLayout(args: {
  client: ViewsClient
  ref: { kind: ViewKind; name: string }
  layout: string
  placed: string[]
  parameters: ParameterValues
  previous: Resolution | null
  signal?: AbortSignal
}): Promise<Resolution> {
  const { client, ref, layout, placed, parameters, previous, signal } = args
  const key = parametersKey(parameters)
  const keep = previous !== null && previous.key === key
  const have = keep ? previous.results : {}
  const needed = placed.filter((id) => !(id in have))
  if (needed.length === 0 && keep) return previous
  const fresh = await client.resolve(ref, layout, parameters, { only: needed.length === placed.length ? undefined : needed, signal })
  return { key, results: { ...have, ...fresh.widgets }, asOf: fresh.as_of }
}
