// @vitest-environment happy-dom
import { describe, expect, it } from 'vitest'
import { isDrawn } from './ready'

const page = (html: string) => {
  const root = document.createElement('div')
  root.innerHTML = html
  return root
}

describe('isDrawn (the print page says when it is drawn)', () => {
  it('waits for every widget the layout places', () => {
    expect(isDrawn(page('<section data-testid="vz-widget-a"></section>'), 2)).toBe(false)
    expect(isDrawn(page('<section data-testid="vz-widget-a"></section><section data-testid="vz-widget-b"></section>'), 2)).toBe(true)
  })

  it('waits for a busy widget', () => {
    expect(isDrawn(page('<section data-testid="vz-widget-a" aria-busy="true"></section>'), 1)).toBe(false)
  })

  it('waits for a chart until its SVG is there, or it says why it has none', () => {
    const chart = (inner: string) => page(`<section data-testid="vz-widget-c"><div data-testid="vz-chart-c"><div role="img">${inner}</div></div></section>`)
    expect(isDrawn(chart(''), 1)).toBe(false)
    expect(isDrawn(chart('<svg></svg>'), 1)).toBe(true)
    expect(isDrawn(page('<section data-testid="vz-widget-c"><div data-testid="vz-chart-c"><p role="alert">The chart could not be drawn</p></div></section>'), 1)).toBe(true)
  })
})
