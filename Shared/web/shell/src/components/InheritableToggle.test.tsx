import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { InheritableToggle } from './InheritableToggle'

const render = (value: boolean | null, inherited: boolean) => renderToStaticMarkup(
  <InheritableToggle label="Create target table if missing" description="d" value={value} inherited={inherited} onChange={() => {}} testId="t" />)

describe('a setting that inherits or overrides', () => {
  it('shows the broader answer, marked inherited, with its checkbox locked', () => {
    const html = render(null, true)
    expect(html).toContain('INHERITED')
    expect(html).toContain('On — from the replication')
    expect(html).toMatch(/data-testid="t-checkbox"[^>]*disabled|disabled[^>]*data-testid="t-checkbox"/)
  })
  it('shows its own answer, not the broader one, when it overrides', () => {
    const html = render(false, true)
    expect(html).not.toContain('INHERITED')
    expect(html).toContain('>Off<')
  })
})
