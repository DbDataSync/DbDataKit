import { readFileSync } from 'node:fs'
import { join } from 'node:path'
import { describe, expect, it } from 'vitest'

/** The declarations of the first rule whose selector is exactly `selector`, as property to value. */
function rule(css: string, selector: string): Record<string, string> {
  const escaped = selector.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
  const body = new RegExp(`(^|\\n)${escaped}\\s*\\{([^}]*)\\}`).exec(css)?.[2]
  if (body === undefined) throw new Error(`styles.css has no rule ${selector}`)
  return Object.fromEntries(body.replace(/\/\*[\s\S]*?\*\//g, '').split(';').map(d => d.split(':').map(p => p.trim())).filter(d => d.length === 2 && d[0]).map(d => [d[0]!, d[1]!]))
}

/**
 * jsdom lays nothing out, so a page's vitest cannot see a column painted over another. These hold the rules that keep a detail page's rail
 * (`.detail-rail`, beside `.detail-main`) off the content: a card wider than its column scrolls inside it, and a row too narrow for both puts the rail
 * below the content. Without them a sync replication's rail covered the Table Mappings tab's Create mappings button and took its clicks.
 */
describe('the detail page layout', () => {
  const css = readFileSync(join(import.meta.dirname, 'styles.css'), 'utf8')

  it('keeps what the content holds inside its own column', () => {
    const main = rule(css, '.detail-main')
    expect(main['overflow']).toBe('auto')
    expect(main['min-width']).toBe('0')
  })

  it('wraps the rail below the content rather than over it', () => {
    expect(rule(css, '.detail-body')['flex-wrap']).toBe('wrap')
    expect(rule(css, '.detail-rail')['flex']).toBe('none')
    expect(rule(css, '.detail-rail')['position']).toBeUndefined()
  })
})
