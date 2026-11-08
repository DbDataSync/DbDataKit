import type { ParameterValue, ParameterValues } from '../views/model'

/** What the running header and footer text can say (plan 32 section 4: `{title} {page} {pages} {asof} {viewer} {params.x}`). */
export interface RunningContext {
  title: string
  page: number
  pages: number
  /** When the rows were read (ISO 8601), as the client gave it. */
  asOf?: string | null
  viewer?: string | null
  parameters?: ParameterValues
}

/** A parameter value as text: a list joined, an empty list or no value as `all` (no filter), a boolean as yes or no. */
export function describeParameter(value: ParameterValue | undefined): string {
  if (value === undefined || value === null || value === '') return 'all'
  if (Array.isArray(value)) return value.length === 0 ? 'all' : value.join(', ')
  if (typeof value === 'boolean') return value ? 'yes' : 'no'
  return String(value)
}

/** `2026-11-08T09:00:00Z` as `2026-11-08 09:00 UTC`; anything else as it came. */
export function formatAsOf(asOf: string | null | undefined): string {
  if (!asOf) return ''
  const m = /^(\d{4}-\d{2}-\d{2})T(\d{2}:\d{2})(?::\d{2}(?:\.\d+)?)?Z$/.exec(asOf)
  return m ? `${m[1]} ${m[2]} UTC` : asOf
}

const TOKEN = /\{([^{}]*)\}/g

/** Text with its tokens replaced. A token the vocabulary does not have stays as written (the validator refuses it in a document; here it is just text). The result is text: it is never read as markup. */
export function expandTokens(text: string, context: RunningContext): string {
  return text.replace(TOKEN, (whole, name: string) => {
    switch (name) {
      case 'title': return context.title
      case 'page': return String(context.page)
      case 'pages': return String(context.pages)
      case 'asof': return formatAsOf(context.asOf)
      case 'viewer': return context.viewer ?? ''
      default:
        if (name.startsWith('params.')) return describeParameter(context.parameters?.[name.slice(7)])
        return whole
    }
  })
}

export interface RunningText { left: string; center: string; right: string }

/** A header or footer (`{left, center, right}`) with its tokens expanded; `null` when the layout has none or every part is empty. */
export function runningText(part: Record<string, string> | undefined, context: RunningContext): RunningText | null {
  if (!part) return null
  const out = { left: expandTokens(part.left ?? '', context), center: expandTokens(part.center ?? '', context), right: expandTokens(part.right ?? '', context) }
  return out.left || out.center || out.right ? out : null
}
