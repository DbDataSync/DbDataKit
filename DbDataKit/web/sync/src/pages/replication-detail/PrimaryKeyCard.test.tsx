import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { PrimaryKeyCard } from './PrimaryKeyCard'

const mappings = ['A', 'B', 'C'].map((c) => ({ sourceColumn: c, targetColumn: c }))

function render(value: string[]): string {
  return renderToStaticMarkup(
    <PrimaryKeyCard columnMappings={mappings as never} sourceColumns={undefined} value={value} onChange={() => {}} />)
}

describe('PrimaryKeyCard', () => {
  it('lists a composite key in the order it was stated, not column order', () => {
    const html = render(['C', 'A'])

    expect(html).toContain('data-testid="primary-key-order"')
    expect(html.indexOf('primary-key-up-C')).toBeLessThan(html.indexOf('primary-key-up-A'))
  })

  it('offers no ordering for a single-column key', () => {
    expect(render(['A'])).not.toContain('primary-key-order')
  })
})
