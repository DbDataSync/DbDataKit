import { describe, expect, it } from 'vitest'
import type { ViewsDoc } from '../views/model'
import type { RenderedWidgets } from '../views/rendered'
import { customProblem } from './custom'
import { isDrawn } from './ready'

const document_ = { widgets: { grid: { type: 'custom', component: 'sales-portal/RegionGrid' }, other: { type: 'custom', component: 'sales-portal/Other' } } } as unknown as ViewsDoc
const rendered = (widgets: RenderedWidgets['widgets']): RenderedWidgets => ({ kind: 'rendered_widgets', layout: 'desk', as_of: '2026-11-12T00:00:00Z', widgets })
const ok = { type: 'custom', component: 'sales-portal/RegionGrid', data: {} } as const

describe('what stops the export of a custom widget (DBR32)', () => {
  it('nothing when every frame is ready and fits a page', () => {
    expect(customProblem(document_, rendered({ grid: ok }), ['grid'], { grid: { kind: 'ready' } }, { grid: 700 }, 900)).toBeNull()
    expect(customProblem(document_, rendered({}), [], {}, {}, 900)).toBeNull()
  })
  it('a query that failed names the widget, the component and the server\'s code and words', () => {
    const problem = customProblem(document_, rendered({ grid: { type: 'error', code: 'denied', message: 'The query `by_region`: no.' } }), ['grid'], {}, {}, 900)!
    expect(problem).toContain('`grid`')
    expect(problem).toContain('sales-portal/RegionGrid')
    expect(problem).toContain('denied')
  })
  it('a frame that failed, and one that never reported, name the widget', () => {
    expect(customProblem(document_, rendered({ grid: ok }), ['grid'], { grid: { kind: 'error', message: 'the chart library did not load' } }, {}, 900)).toContain('the chart library did not load')
    expect(customProblem(document_, rendered({ grid: ok }), ['grid'], {}, {}, 900)).toContain('no app files')
  })
  it('a widget whose content is taller than a page is refused naming it and its height, the first one found', () => {
    const problem = customProblem(document_, rendered({ grid: ok, other: { ...ok, component: 'sales-portal/Other' } }), ['grid', 'other'], { grid: { kind: 'ready' }, other: { kind: 'ready' } }, { grid: 700, other: 6000 }, 900)!
    expect(problem).toContain('`other`')
    expect(problem).toContain('taller than a page')
    expect(problem).toContain('6000px')
    expect(problem).toContain('900px')
    // the page's height itself is allowed
    expect(customProblem(document_, rendered({ grid: ok }), ['grid'], { grid: { kind: 'ready' } }, { grid: 900 }, 900)).toBeNull()
  })
})

describe('a print page with a custom frame is drawn when the frame is', () => {
  const root = (state: string) => ({
    querySelector: (selector: string) => (selector.includes('vz-custom-frame') && state === 'loading' ? {} : null),
    querySelectorAll: (selector: string) => (selector.includes('vz-widget-') ? [{}] : []),
  }) as unknown as ParentNode
  it('waits while a frame is loading and goes on when it is ready or failed', () => {
    expect(isDrawn(root('loading'), 1)).toBe(false)
    expect(isDrawn(root('ready'), 1)).toBe(true)
  })
})
