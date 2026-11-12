// A small app that registers custom views with @dbdatakit/views-sdk, for the exporter's tests (plan 32 DBR32): built by `npm run build:fixture-app -w @dbdatakit/visuals` into visuals/dist/fixture-app
// (an index.html and one classic script, as a frame loads it). Each view is one thing the export must handle: draw rows, be taller than a page, fail, never say it is ready, paginate.
import { createElement as h, useEffect } from 'react'
import { PageBreak, defineWidget, definePage, startViewFrame, themeStyle, useReady, useSetParam, useView } from '../src/index'

function RegionGrid() {
  const { data, theme, mode, size } = useView()
  const { ready } = useReady()
  const set = useSetParam()
  const rows = data.by_region?.rows ?? []
  useEffect(() => { void ready() })
  return h('div', { style: { ...themeStyle(theme), font: '12px sans-serif', color: 'var(--sui-text)' }, 'data-mode': mode },
    h('b', null, `RegionGrid ${mode} ${Math.round(size.width)}x${size.height}`),
    h('table', null, h('tbody', null, rows.map((r, i) => h('tr', { key: i, onClick: () => set('region', [String(r.customers_region)]) }, h('td', null, String(r.customers_region)), h('td', null, String(r.orders_net_revenue)))))))
}

function TallWidget() {
  const { ready } = useReady()
  useEffect(() => { void ready() })
  return h('div', { style: { height: 6000, font: '12px sans-serif' } }, 'a widget taller than any page')
}

function FailingWidget() {
  const { fail } = useReady()
  useEffect(() => fail('the chart library did not load'))
  return h('div', null, 'failing')
}

function NeverReady() { return h('div', null, 'never says it is ready') }

function RegionalPage() {
  const { data, theme } = useView()
  const { ready } = useReady()
  const rows = data.by_region?.rows ?? []
  useEffect(() => { void ready() })
  const section = (n: number) => h('section', { key: n }, h('h1', null, `Section ${n}`), h('p', null, `Rows: ${rows.map((r) => `${String(r.customers_region)}=${String(r.orders_net_revenue)}`).join(', ')}`), n < 3 ? h(PageBreak) : null)
  return h('div', { style: { ...themeStyle(theme), font: '12pt sans-serif' } }, [1, 2, 3].map(section))
}

defineWidget('RegionGrid', RegionGrid)
defineWidget('TallWidget', TallWidget)
defineWidget('FailingWidget', FailingWidget)
defineWidget('NeverReady', NeverReady)
definePage('RegionalPage', RegionalPage)
startViewFrame()
