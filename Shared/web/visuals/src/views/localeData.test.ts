import { createHash } from 'node:crypto'
import { readFileSync } from 'node:fs'
import { join } from 'node:path'
import { describe, expect, it } from 'vitest'
import { formatValue, LOCALES, localeData } from './format'
import locales from './locales.json'

/**
 * The hand-written locale data of the formatter (`locales.json`, and the currency symbols and compact suffixes in `format.ts`) against the vendored CLDR snapshot
 * (`Shared/conformance/locale/cldr-snapshot.json`, pinned by `cldr-snapshot.pin`) and, where it is stable, against the ICU data of this Node. The twin of `LocaleDataTests` in
 * `DbDataKit.Views.Tests`: the same snapshot and the same allow-list, the same fields. A failure names the locale and the field; a deliberate difference is a row with a
 * reason in `allowed-deviations.json`. What a person must still judge is in `docs/locale-data.md`.
 */
const dir = join(import.meta.dirname, '../../../../conformance/locale')
const readText = (name: string) => readFileSync(join(dir, name), 'utf8')
interface Snapshot {
  source: { version: string; licence: string }
  locales: Record<string, { decimal: string; group: string; months: string[]; short: string[] }>
  currencySymbols: { symbols: Record<string, string> }
  compactSuffixes: Record<'thousand' | 'million' | 'billion', string>
}
interface Deviation { against: string; locale: string; field: string; ours: string; reference: string; reason?: string }
const snapshot = JSON.parse(readText('cldr-snapshot.json')) as Snapshot
const deviations = (JSON.parse(readText('allowed-deviations.json')) as { deviations: Deviation[] }).deviations

interface Diff { against: string; locale: string; field: string; ours: string; reference: string }
const hex = (s: string) => [...s].map((c) => `U+${c.codePointAt(0)!.toString(16).toUpperCase().padStart(4, '0')}`).join(' ')
// eslint-disable-next-line no-control-regex
const show = (s: string) => (/^[!-~]*$/.test(s) ? s : `${s} (${hex(s)})`)
const describeDiff = (d: Diff) => `${d.against}: locale ${d.locale}, field ${d.field}: ours '${d.ours}', reference '${d.reference}'`

const ours = locales as Record<string, { decimal: string; group: string; months: string[]; short: string[] }>

function cldrDiffs(): Diff[] {
  const diffs: Diff[] = []
  for (const [code, ref] of Object.entries(snapshot.locales)) {
    const l = localeData(code)
    const same = (field: string, o: string | undefined, r: string) => { if (o !== r) diffs.push({ against: 'cldr', locale: code, field, ours: show(o ?? ''), reference: show(r) }) }
    same('decimal', l.decimal, ref.decimal)
    same('group', l.group, ref.group)
    for (let i = 0; i < 12; i++) {
      same(`months[${i}]`, l.months[i], ref.months[i]!)
      same(`short[${i}]`, l.short[i], ref.short[i]!)
    }
  }
  for (const [iso, symbol] of Object.entries(snapshot.currencySymbols.symbols)) {
    const text = formatValue(1, { style: 'currency', currency: iso, decimals: 0 })
    const o = text.endsWith('1') ? text.slice(0, -1) : text
    if (o !== symbol) diffs.push({ against: 'cldr', locale: 'en', field: `currency.${iso}`, ours: show(o), reference: show(symbol) })
  }
  for (const [name, value] of [['thousand', 1e3], ['million', 1e6], ['billion', 1e9]] as const) {
    const o = formatValue(value, { style: 'compact', decimals: 0 }).replace(/^1/, '')
    const r = snapshot.compactSuffixes[name].replace(/^0/, '')
    if (o !== r) diffs.push({ against: 'cldr', locale: 'en', field: `compact.${name}`, ours: show(o), reference: show(r) })
  }
  return diffs
}

/** Separators and stand-alone month names as Node's ICU writes them; abbreviations and symbols differ between ICU versions and are not compared here. */
function icuDiffs(): Diff[] {
  const diffs: Diff[] = []
  for (const code of LOCALES) {
    const l = localeData(code)
    const parts = new Intl.NumberFormat(code, { useGrouping: true, minimumGroupingDigits: 1 } as Intl.NumberFormatOptions).formatToParts(1234567.5)
    const same = (field: string, o: string, r: string) => { if (o !== r) diffs.push({ against: 'icu', locale: code, field, ours: show(o), reference: show(r) }) }
    same('decimal', l.decimal, parts.find((p) => p.type === 'decimal')!.value)
    same('group', l.group, parts.find((p) => p.type === 'group')!.value)
    const month = new Intl.DateTimeFormat(code, { month: 'long', timeZone: 'UTC' })
    for (let i = 0; i < 12; i++) same(`months[${i}]`, l.months[i]!, month.format(new Date(Date.UTC(2026, i, 15))))
  }
  return diffs
}

function unexplained(diffs: Diff[], against: string): string[] {
  const rows = deviations.filter((r) => r.against === against)
  const problems: string[] = []
  const used = new Set<Deviation>()
  for (const d of diffs) {
    const row = rows.find((r) => r.locale === d.locale && r.field === d.field && show(r.ours) === d.ours && show(r.reference) === d.reference)
    if (row) used.add(row)
    else problems.push(describeDiff(d))
  }
  for (const r of rows) {
    const where = `allowed-deviations.json, ${against}: locale ${r.locale}, field ${r.field}`
    if (!r.reason?.trim()) problems.push(`${where}: the row has no reason`)
    if (!used.has(r)) problems.push(`${where}: the row matches no difference any more (delete it, or its values are out of date)`)
  }
  return problems
}

const expectNone = (problems: string[]) => expect(problems, `Locale data differs from its reference:\n${problems.join('\n')}`).toEqual([])

describe('locale data', () => {
  it('the snapshot is the pinned one', () => {
    const pin = Object.fromEntries(readText('cldr-snapshot.pin').split(/\r?\n/).filter((l) => l.includes('=') && !l.startsWith('#')).map((l) => [l.slice(0, l.indexOf('=')), l.slice(l.indexOf('=') + 1)]))
    const hash = createHash('sha256').update(readFileSync(join(dir, 'cldr-snapshot.json'))).digest('hex')
    expect(hash, 'cldr-snapshot.json changed without cldr-snapshot.pin: regenerate it with scripts/fetch_cldr_snapshot.py --write, never edit it by hand').toBe(pin.SNAPSHOT_SHA256)
    expect(snapshot.source.version).toBe(pin.CLDR_VERSION)
    expect(snapshot.source.licence).toBe('Unicode-3.0')
  })

  it('the table has the locales and fields of the snapshot and no others', () => {
    const problems: string[] = []
    for (const code of Object.keys(snapshot.locales)) if (!(code in ours)) problems.push(`locale ${code}: missing from locales.json`)
    for (const code of Object.keys(ours)) if (!(code in snapshot.locales)) problems.push(`locale ${code}: in locales.json but not in the CLDR snapshot (add it to scripts/fetch_cldr_snapshot.py's input and run --write)`)
    expect(Object.keys(ours)).toEqual(Object.keys(snapshot.locales))
    for (const [code, l] of Object.entries(ours)) {
      for (const f of ['decimal', 'group', 'months', 'short']) if (!(f in l)) problems.push(`locale ${code}, field ${f}: missing`)
      for (const f of Object.keys(l)) if (!['decimal', 'group', 'months', 'short'].includes(f)) problems.push(`locale ${code}, field ${f}: not a field the snapshot checks`)
    }
    expectNone(problems)
  })

  it('every field is well formed', () => {
    const problems: string[] = []
    for (const code of LOCALES) {
      const l = localeData(code)
      const check = (field: string, v: string, separator = false) => {
        if (!v) { problems.push(`locale ${code}, field ${field}: empty`); return }
        if (v !== v.normalize('NFC')) problems.push(`locale ${code}, field ${field}: '${v}' is not in Unicode normal form C`)
        // eslint-disable-next-line no-control-regex
        if (/[\u0000-\u001f\u007f-\u009f]/.test(v)) problems.push(`locale ${code}, field ${field}: contains a control character (${hex(v)})`)
        if (separator) {
          if ([...v].length !== 1) problems.push(`locale ${code}, field ${field}: a separator is one character, this is ${hex(v)}`)
          else if (v === ' ' || (/\s/.test(v) && v !== ' ' && v !== ' ')) problems.push(`locale ${code}, field ${field}: ${hex(v)} is a breaking space (use U+00A0 or U+202F)`)
        } else if (v !== v.trim()) problems.push(`locale ${code}, field ${field}: '${v}' has a space at its start or end`)
      }
      check('decimal', l.decimal, true)
      check('group', l.group, true)
      if (l.decimal === l.group) problems.push(`locale ${code}: decimal and group separator are both '${l.decimal}'`)
      if (l.months.length !== 12) problems.push(`locale ${code}, field months: ${l.months.length} names, not 12`)
      if (l.short.length !== 12) problems.push(`locale ${code}, field short: ${l.short.length} names, not 12`)
      l.months.forEach((m, i) => check(`months[${i}]`, m))
      l.short.forEach((m, i) => check(`short[${i}]`, m))
      if (new Set(l.months).size !== l.months.length) problems.push(`locale ${code}, field months: two months have the same name`)
      if (new Set(l.short).size !== l.short.length) problems.push(`locale ${code}, field short: two months have the same abbreviation`)
    }
    expectNone(problems)
  })

  it('every field equals the CLDR snapshot or is an allowed deviation', () => expectNone(unexplained(cldrDiffs(), 'cldr')))

  it('the French group separator is the narrow no-break space', () => {
    expect(localeData('fr').group).toBe(' ')
    expect(formatValue(1234.5, { style: 'number', decimals: 1 }, undefined, 'fr')).toBe('1 234,5')
  })

  // Node bundles its ICU, so the answer depends on the Node version and not on the machine; a Node built without the locales (small-icu) has nothing to compare.
  const icuHasLocales = Intl.NumberFormat.supportedLocalesOf(['fr', 'de', 'es', 'it', 'pt', 'nl']).length === 6
  it.skipIf(!icuHasLocales)('separators and month names equal what ICU says where ICU is present', () => expectNone(unexplained(icuDiffs(), 'icu')))
})
