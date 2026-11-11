import { createElement } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { HostLink, PageBreak, ViewHost, definePage, defineWidget, linkHref, linkValues, registered, registeredNames, themeStyle, useView } from './index'
import type { HostLinkInfo, HostPort, ToHost, ViewProps } from './index'

const links: Record<string, HostLinkInfo> = {
  'region-page': { route: '/views/regional.yaml', as: 'navigate', values: { region: { kind: 'field', text: 'customers.region' }, year: { kind: 'param', text: 'year' }, top: { kind: 'literal', text: '10', value: 10 } }, print: 'link' },
  'order-form': { route: '/views/orders.yaml', as: 'dialog', values: { customer: { kind: 'field', text: 'customers.id' } }, print: 'hidden' },
  'open-arrow': { route: '/views/orders.yaml', as: 'navigate', values: {} },
}
const props = (mode: ViewProps['mode']): ViewProps => ({ data: {}, params: { year: 2026 }, theme: { scheme: 'light', tokens: { 'sui-text': '#111' } }, size: { width: 400, height: 'auto' }, mode, links })
const port = (): HostPort & { sent: ToHost[] } => { const sent: ToHost[] = []; return { sent, send: (m) => sent.push(m), subscribe: () => () => {} } }
const render = (mode: ViewProps['mode'], child: () => ReturnType<typeof createElement>) => {
  const Inner = () => child()
  return renderToStaticMarkup(createElement(ViewHost, { port: port(), props: props(mode), children: createElement(Inner) }))
}

describe('registration', () => {
  it('registers a widget and a page by name, refuses a name twice and a bad name', () => {
    const C = () => null
    expect(defineWidget('OrdersGrid', C)).toBe(C)
    definePage('RegionalDetail', C)
    expect(registered('OrdersGrid')?.kind).toBe('widget')
    expect(registered('RegionalDetail')?.kind).toBe('page')
    expect(registeredNames()).toEqual(['OrdersGrid', 'RegionalDetail'])
    expect(() => defineWidget('OrdersGrid', C)).toThrow(/registered twice/)
    expect(() => defineWidget('1bad', C)).toThrow(/not a name/)
  })
})

describe('what a link carries', () => {
  it('reads a field of the row by its full name or its alias, a parameter and a literal; leaves out what has no value', () => {
    expect(linkValues(links['region-page']!, { 'customers.region': 'EMEA' }, { year: 2026 })).toEqual({ region: 'EMEA', year: 2026, top: 10 })
    expect(linkValues(links['region-page']!, { region: 'APAC' }, {})).toEqual({ region: 'APAC', top: 10 })
    expect(linkValues(links['region-page']!, { 'customers.region': null }, { year: undefined as never })).toEqual({ top: 10 })
  })
  it('is the same rule as the host: the address has the values as a sorted, encoded query', () => {
    const href = (row: Record<string, unknown>) => renderToStaticMarkup(createElement(ViewHost, { port: port(), props: props('screen'), children: createElement(() => createElement('i', null, String(linkHref('region-page', row)))) }))
    expect(href({ 'customers.region': 'North & South' })).toBe('<i>/views/regional.yaml?region=North%20%26%20South&amp;top=10&amp;year=2026</i>')
    const none = renderToStaticMarkup(createElement(ViewHost, { port: port(), props: props('screen'), children: createElement(() => createElement('i', null, String(linkHref('order-form', {})))) }))
    expect(none).toBe('<i>null</i>')   // a dialog has no address of its own
  })
})

describe('a link drawn by the document developer\'s choice', () => {
  const draw = (mode: ViewProps['mode'], name: string, extra: { print?: 'hidden' | 'normal' | 'link'; meaning?: 'value' | 'affordance' } = {}) =>
    render(mode, () => createElement(HostLink, { name, row: { 'customers.region': 'EMEA' }, ...extra, children: 'Go' }))
  it('on screen it is an anchor with its address', () => {
    expect(draw('screen', 'region-page')).toContain('href="/views/regional.yaml?region=EMEA&amp;top=10&amp;year=2026"')
  })
  it('print: link is an anchor with the address and the address is not text; hidden leaves nothing; normal leaves the text only', () => {
    const link = draw('print', 'region-page')
    expect(link).toMatch(/^<a href="\/views\/regional\.yaml\?[^"]*">Go<\/a>$/)
    expect(link.replace(/href="[^"]*"/, '')).not.toContain('regional')
    expect(draw('print', 'order-form')).toBe('')
    expect(draw('print', 'region-page', { print: 'normal' })).toBe('<span>Go</span>')
  })
  it('the default follows the meaning: a value is drawn, an open arrow is not; a PNG draws link as normal', () => {
    expect(draw('print', 'open-arrow')).toBe('')
    expect(draw('print', 'open-arrow', { meaning: 'value' })).toBe('<span>Go</span>')
    expect(draw('image', 'region-page')).toBe('<span>Go</span>')
  })
  it('a link with no address is never a link: a dialog says normal or hidden', () => {
    expect(draw('print', 'order-form', { print: 'link', meaning: 'value' })).toBe('<span>Go</span>')
    expect(draw('print', 'order-form', { print: 'link' })).toBe('')
  })
})

describe('the rest', () => {
  it('a page break is a break after, not drawn', () => {
    expect(renderToStaticMarkup(createElement(PageBreak))).toContain('break-after:page')
  })
  it('the theme becomes custom properties', () => {
    expect(themeStyle(props('screen').theme)).toEqual({ '--sui-text': '#111' })
  })
  it('the props are the host\'s', () => {
    expect(render('print', () => createElement('b', null, String(useView().size.height)))).toBe('<b>auto</b>')
  })
})
