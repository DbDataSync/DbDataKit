import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { rows } from './fixtures'
import { rowKey } from './keys'
import { DashboardBoard } from './parts'
import { SchemeContext } from './scheme'
import type { DashboardSpec } from './spec'

describe('the key a view addresses a row by (AP28)', () => {
  it('uses the view key parameter for one key attribute, as before', () => {
    expect(rowKey({ name: 'territory', key: ['code'] }, 'id', { id: 'EU' })).toEqual({ key: { code: 'EU' }, label: 'EU' })
  })

  it('addresses a two-attribute key by one parameter per attribute, named as the attribute', () => {
    const got = rowKey({ name: 'quota', key: ['region', 'year'] }, 'region', { region: 'emea', year: '2026' })
    expect(got).toEqual({ key: { region: 'emea', year: '2026' }, label: 'emea, 2026' })
  })

  it('refuses a composite key the view does not supply in full, naming what is missing', () => {
    const got = rowKey({ name: 'quota', key: ['region', 'year'] }, 'region', { region: 'emea' })
    expect(got).toBeInstanceOf(Error)
    expect((got as Error).message).toContain("lacks 'year'")
    expect((got as Error).message).toContain('region, year')
  })
})

describe('the charts follow the shell scheme', () => {
  const spec: DashboardSpec = {
    kind: 'dashboard', title: 'D', model: 'territories', params: [], links: [],
    widgets: [{ kind: 'bar', title: 'By region', entity: 'territory', aggregate: 'sum', measure: { name: 'quota', label: null }, by: { name: 'region', label: null }, columns: [], limit: 10 }],
  }

  it('draws in the scheme the shell fixed, whatever the system says', () => {
    const dark = renderToStaticMarkup(<SchemeContext.Provider value="dark"><DashboardBoard spec={spec} rows={{ territory: rows }} /></SchemeContext.Provider>)
    const light = renderToStaticMarkup(<SchemeContext.Provider value="light"><DashboardBoard spec={spec} rows={{ territory: rows }} /></SchemeContext.Provider>)
    expect(dark).toContain('data-vz-theme="dark"')
    expect(light).toContain('data-vz-theme="light"')
  })
})
