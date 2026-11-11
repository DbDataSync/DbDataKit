/**
 * The format vocabulary of the shared core and its one formatter (plan 32 section 9g, DBR40): the twin of `ViewFormat` in `DbDataKit.Views` (.NET), run against the same goldens
 * (`Shared/conformance/views/format/cases.json`) so the screen, the PDF and an email read alike. A format is a preset (`catalog`, `full`, `compact`, `percent`), a number mapping
 * `{style, decimals, currency, grouping}` or a date mapping `{date: preset | pattern}` over a closed token set. Separators and month names are the invariant (English) ones.
 */

export const FORMAT_PRESETS = ['catalog', 'full', 'compact', 'percent'] as const
export const NUMBER_STYLES = ['number', 'currency', 'percent', 'compact'] as const
export const DATE_PRESETS = ['year', 'quarter', 'month', 'month-year', 'date', 'datetime'] as const
export const MAX_DECIMALS = 10
export const MAX_PATTERN_LENGTH = 40

/** A catalog's own format of a field (`x_format` of the result's descriptor). */
export interface CatalogFormat { style?: string; decimals?: number; currency?: string }
export type ViewFormatValue = string | { style?: string; decimals?: number; currency?: string; grouping?: boolean; date?: string }
/** What is wrong with a format: the key inside it (empty for the format itself) and the words. */
export interface FormatProblem { key: string; message: string }

const DATE_PATTERNS: Record<string, string> = {
  year: 'yyyy', quarter: "'Q'Q yyyy", month: 'MMM', 'month-year': 'MMM yyyy', date: 'yyyy-MM-dd', datetime: 'yyyy-MM-dd HH:mm',
}
const MONTHS = ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December']
const TOKEN_LIST = 'yyyy, yy, Q, MMMM, MMM, MM, dd, HH, mm, ss'

const isObject = (v: unknown): v is Record<string, unknown> => typeof v === 'object' && v !== null && !Array.isArray(v)
const presetWords = FORMAT_PRESETS.join(', ')

// ---- the check ----

/** The first thing wrong with a format, or null when the formatter reads it; the words are the .NET loader's. */
export function checkFormat(format: unknown): FormatProblem | null {
  if (typeof format === 'string')
    return (FORMAT_PRESETS as readonly string[]).includes(format) ? null : { key: '', message: `\`format\` is one of: ${presetWords}, or a mapping with \`style\` or \`date\`.` }
  if (!isObject(format)) return { key: '', message: `\`format\` is a preset (${presetWords}) or a mapping with \`style\` or \`date\`.` }
  const hasStyle = 'style' in format
  const hasDate = 'date' in format
  if (hasStyle === hasDate) return { key: '', message: 'A format mapping has `style` (a number format) or `date` (a date format), not both and not neither.' }
  if (hasDate) {
    for (const key of Object.keys(format)) if (key !== 'date') return { key, message: `\`${key}\` is not a key of a date format (date).` }
    const message = typeof format.date === 'string' ? parse(format.date).problem : `\`date\` is a preset (${DATE_PRESETS.join(', ')}) or a pattern of the tokens ${TOKEN_LIST} and literal text.`
    return message ? { key: 'date', message } : null
  }
  for (const key of Object.keys(format)) if (!['style', 'decimals', 'currency', 'grouping'].includes(key)) return { key, message: `\`${key}\` is not a key of a number format (style, decimals, currency, grouping).` }
  const style = format.style
  if (typeof style !== 'string' || !(NUMBER_STYLES as readonly string[]).includes(style)) return { key: 'style', message: `\`style\` is one of: ${NUMBER_STYLES.join(', ')}.` }
  if ('decimals' in format && !(typeof format.decimals === 'number' && Number.isInteger(format.decimals) && format.decimals >= 0 && format.decimals <= MAX_DECIMALS))
    return { key: 'decimals', message: `\`decimals\` is a whole number from 0 to ${MAX_DECIMALS}.` }
  if ('grouping' in format && typeof format.grouping !== 'boolean') return { key: 'grouping', message: '`grouping` is true or false.' }
  if (style === 'currency') {
    if (!('currency' in format)) return { key: '', message: 'A currency format needs `currency` (an ISO 4217 code such as USD).' }
    if (!(typeof format.currency === 'string' && /^[A-Z]{3}$/.test(format.currency))) return { key: 'currency', message: '`currency` is an ISO 4217 code of three capital letters (USD).' }
  } else if ('currency' in format) return { key: 'currency', message: '`currency` belongs to the style `currency`.' }
  return null
}

type Part = { kind: 'lit'; text: string } | { kind: 'y4' | 'y2' | 'q' | 'mname' | 'mshort' | 'm2' | 'd2' | 'h2' | 'mi2' | 's2' }

const TOKENS: Record<string, Part['kind']> = { y4: 'y4', y2: 'y2', Q1: 'q', M4: 'mname', M3: 'mshort', M2: 'm2', d2: 'd2', H2: 'h2', m2: 'mi2', s2: 's2' }

function parse(input: string): { problem?: string; parts: Part[] } {
  const parts: Part[] = []
  const text = DATE_PATTERNS[input] ?? input
  if (text.length === 0 || text.length > MAX_PATTERN_LENGTH) return { problem: `\`date\` is a preset (${DATE_PRESETS.join(', ')}) or a pattern of 1 to ${MAX_PATTERN_LENGTH} characters.`, parts }
  // eslint-disable-next-line no-control-regex
  if (/[\u0000-\u001f\u007f-\u009f]/.test(text)) return { problem: '`date` is one line of text.', parts }
  let tokens = 0
  for (let i = 0; i < text.length;) {
    const c = text[i]!
    if (c === "'") {
      const end = text.indexOf("'", i + 1)
      if (end < 0) return { problem: "`date` pattern has a quote that is never closed ('...' holds literal text).", parts }
      parts.push({ kind: 'lit', text: end === i + 1 ? "'" : text.slice(i + 1, end) })
      i = end + 1
      continue
    }
    if (!/[A-Za-z]/.test(c)) { parts.push({ kind: 'lit', text: c }); i++; continue }
    let run = 1
    while (i + run < text.length && text[i + run] === c) run++
    const kind = TOKENS[`${c}${run}`]
    if (!kind) return { problem: `\`date\` pattern has the unknown token \`${c.repeat(run)}\` (tokens: ${TOKEN_LIST}; put literal letters in single quotes).`, parts }
    parts.push({ kind } as Part)
    tokens++
    i += run
  }
  return tokens === 0 ? { problem: `\`date\` pattern has no token (tokens: ${TOKEN_LIST}).`, parts } : { parts }
}

// ---- the formatter ----

/**
 * A value as text under a format: a number takes a preset or a number mapping (the catalog's own format supplies the default decimals and what `catalog` and `full` read); a text takes a date mapping
 * (an ISO date or date-time, read as written, no time zone applied) and is otherwise returned as it is.
 */
export function formatValue(value: unknown, format?: ViewFormatValue | null, catalog?: CatalogFormat | null): string {
  if (typeof value === 'string') return isObject(format) && typeof format.date === 'string' ? formatDate(value, format.date) : value
  if (typeof value === 'number') return formatNumber(value, format, catalog)
  return value === null || value === undefined ? '' : String(value)
}

export function formatNumber(v: number, format?: ViewFormatValue | null, catalog?: CatalogFormat | null): string {
  if (!Number.isFinite(v)) return String(v)
  if (isObject(format) && typeof format.style === 'string') {
    const grouping = format.grouping !== false
    return styled(v, format.style, format.decimals ?? catalog?.decimals ?? null, format.currency ?? null, grouping)
  }
  return preset(v, typeof format === 'string' ? format : 'full', catalog)
}

function preset(v: number, name: string, catalog?: CatalogFormat | null): string {
  const style = catalog?.style ?? 'number'
  const decimals = catalog?.decimals ?? null
  const symbol = style === 'currency' ? currencySymbol(catalog?.currency) : ''
  if (name === 'percent' || style === 'percent') return signed(v, digits(Math.abs(v) * 100, decimals ?? 0, decimals ?? 0, false), '', '%')
  if (name === 'compact') return compact(v, null, symbol)
  return signed(v, digits(Math.abs(v), decimals, decimals ?? 2, true), symbol)
}

function styled(v: number, style: string, decimals: number | null, currency: string | null, grouping: boolean): string {
  switch (style) {
    case 'percent': return signed(v, digits(Math.abs(v) * 100, decimals ?? 0, decimals ?? 0, grouping), '', '%')
    case 'currency': {
      const d = decimals ?? (currency === 'JPY' || currency === 'KRW' ? 0 : 2)
      return signed(v, digits(Math.abs(v), d, d, grouping), currencySymbol(currency))
    }
    case 'compact': return compact(v, decimals, '')
    default: return signed(v, digits(Math.abs(v), decimals, decimals ?? 2, grouping), '')
  }
}

function compact(v: number, decimals: number | null, symbol: string): string {
  const a = Math.abs(v)
  let body: string
  if (a >= 1e3) {
    const [scaled, suffix] = a >= 1e9 ? [a / 1e9, 'B'] : a >= 1e6 ? [a / 1e6, 'M'] : [a / 1e3, 'K']
    body = digits(scaled as number, null, decimals ?? 1, false) + suffix
  } else body = digits(a, null, decimals ?? 2, false)
  return signed(v, body, symbol)
}

/** The minus sign goes before the symbol, and only when the text shows a non-zero digit (no `-0`). */
const signed = (v: number, body: string, symbol: string, suffix = '') => (v < 0 && /[1-9]/.test(body) ? '-' : '') + symbol + body + suffix

const SYMBOLS: Record<string, string> = { USD: '$', EUR: '€', GBP: '£', JPY: '¥', CNY: '¥', INR: '₹', KRW: '₩' }
const currencySymbol = (code?: string | null) => (code == null ? '' : SYMBOLS[code] ?? `${code} `)

/**
 * A non-negative number rounded half away from zero to `fixed` places (or, with none given, to at most `atMost` places with trailing zeros dropped), the integer part grouped by thousands. The rounding reads the
 * number as its 15 significant digits, so 1.005 rounds the way it is written, as in .NET.
 */
export function digits(abs: number, fixed: number | null, atMost: number, grouping: boolean): string {
  const places = fixed ?? atMost
  const e = abs.toExponential(14) // d.dddddddddddddde+X
  const at = e.indexOf('e')
  const n = BigInt(e[0]! + e.slice(2, at))
  const exponent = Number(e.slice(at + 1))
  const k = exponent - 14 + places
  const scaled = k >= 0 ? n * 10n ** BigInt(k) : (n + 10n ** BigInt(-k) / 2n) / 10n ** BigInt(-k)
  const all = scaled.toString().padStart(places + 1, '0')
  let whole = places === 0 ? all : all.slice(0, all.length - places)
  let frac = places === 0 ? '' : all.slice(all.length - places)
  if (fixed === null) frac = frac.replace(/0+$/, '')
  if (grouping) whole = whole.replace(/\B(?=(\d{3})+$)/g, ',')
  return frac.length === 0 ? whole : `${whole}.${frac}`
}

const ISO = /^(\d{4})(?:-(\d{2})(?:-(\d{2})(?:[T ](\d{2}):(\d{2})(?::(\d{2})(?:\.\d+)?)?(?:Z|[+-]\d{2}:?\d{2})?)?)?)?$/

/** A date or date-time as text under a preset or a pattern; text that is not an ISO date is returned as it is. */
export function formatDate(text: string, patternOrPreset: string): string {
  const { problem, parts } = parse(patternOrPreset)
  const m = ISO.exec(text)
  if (problem || !m) return text
  const g = (i: number, dflt: number) => (m[i] === undefined ? dflt : Number(m[i]))
  const [y, mo, d, h, mi, s] = [g(1, 0), g(2, 1), g(3, 1), g(4, 0), g(5, 0), g(6, 0)]
  if (mo < 1 || mo > 12 || d < 1 || d > 31 || h > 23 || mi > 59 || s > 59) return text
  const two = (n: number) => String(n).padStart(2, '0')
  return parts.map((p) => {
    switch (p.kind) {
      case 'lit': return p.text
      case 'y4': return String(y).padStart(4, '0')
      case 'y2': return two(y % 100)
      case 'q': return String(Math.floor((mo - 1) / 3) + 1)
      case 'mname': return MONTHS[mo - 1]!
      case 'mshort': return MONTHS[mo - 1]!.slice(0, 3)
      case 'm2': return two(mo)
      case 'd2': return two(d)
      case 'h2': return two(h)
      case 'mi2': return two(mi)
      default: return two(s)
    }
  }).join('')
}
