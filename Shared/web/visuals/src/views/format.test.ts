import { readFileSync } from 'node:fs'
import { join } from 'node:path'
import { describe, expect, it } from 'vitest'
import { checkFormat, digits, formatValue, LOCALES, localeData } from './format'
import type { CatalogFormat, ViewFormatValue } from './format'

/** The goldens shared with the .NET suite (`ViewFormat`): `Shared/conformance/views/format/cases.json`. */
const golden = JSON.parse(readFileSync(join(import.meta.dirname, '../../../../conformance/views/format/cases.json'), 'utf8')) as {
  formats: { name: string; value: unknown; format?: ViewFormatValue; catalog?: CatalogFormat; locale?: string; text: string }[]
  bad: { format: unknown; key: string; message: string }[]
}

describe('formatValue', () => {
  for (const c of golden.formats)
    it(c.name, () => {
      expect(formatValue(c.value, c.format, c.catalog, c.locale)).toBe(c.text)
    })

  it('the locale table has the seven locales with twelve months each', () => {
    expect([...LOCALES]).toEqual(['en', 'de', 'fr', 'es', 'it', 'pt', 'nl'])
    for (const code of LOCALES) {
      const l = localeData(code)
      expect(l.months).toHaveLength(12)
      expect(l.short).toHaveLength(12)
      expect(l.decimal).not.toBe(l.group)
    }
    expect(localeData('klingon')).toBe(localeData('en'))
  })

  it('every good golden is a format the check accepts', () => {
    for (const c of golden.formats) if (c.format !== undefined) expect(checkFormat(c.format), c.name).toBeNull()
  })
})

describe('checkFormat', () => {
  for (const c of golden.bad)
    it(`refuses ${JSON.stringify(c.format).slice(0, 60)}`, () => {
      expect(checkFormat(c.format)).toEqual({ key: c.key, message: c.message })
    })

  it('the four presets of the first release stay valid', () => {
    for (const p of ['catalog', 'full', 'compact', 'percent']) expect(checkFormat(p)).toBeNull()
  })
})

describe('digits', () => {
  it('rounds half away from zero on the written value and groups thousands', () => {
    expect(digits(1.005, null, 2, true)).toBe('1.01')
    expect(digits(0.5, 0, 0, true)).toBe('1')
    expect(digits(999.995, 2, 2, true)).toBe('1,000.00')
    expect(digits(0, 2, 2, true)).toBe('0.00')
    expect(digits(1234567.5, null, 2, true)).toBe('1,234,567.5')
    expect(digits(0.000001, null, 2, true)).toBe('0')
  })
})
