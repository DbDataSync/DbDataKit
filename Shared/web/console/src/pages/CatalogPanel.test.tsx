import { renderToStaticMarkup } from 'react-dom/server'
import { MemoryRouter } from 'react-router-dom'
import { describe, expect, it } from 'vitest'
import { Opened, ParametersPane } from './CatalogPanel'
import type { CatalogOpening, CatalogParameter } from '../catalog'

const html = (node: React.ReactElement) => renderToStaticMarkup(<MemoryRouter>{node}</MemoryRouter>)

describe('the parameters pane', () => {
  const params: CatalogParameter[] = [
    { name: 'Country', type: 'String', label: 'Country', many: false, required: true, validValues: [{ value: 'US', label: 'United States' }, { value: 'DE' }] },
    { name: 'Region', type: 'String', many: true, required: false, dependsOn: ['Country'], validValues: [{ value: 'East' }] },
    { name: 'Year', type: 'Integer', many: false, required: false },
  ]

  it('draws a choice for a parameter with listed values, a multiple choice for a many one and a field for the rest, saying what depends on what', () => {
    const out = html(<ParametersPane parameters={params} values={{ Country: ['US'], Region: [], Year: ['2026'] }} onChange={() => {}} />)
    expect(out).toContain('data-testid="catalog-param-Country"')
    expect(out).toContain('United States')
    expect(out).toContain('multiple')
    expect(out).toContain('depends on Country')
    expect(out).toContain('Country *')
    expect(out).toContain('value="2026"')
  })

  it('says an item has no parameters', () => {
    expect(html(<ParametersPane parameters={[]} values={{}} onChange={() => {}} />)).toContain('catalog-params-none')
  })
})

describe('an opened item', () => {
  const base = { key: 'ssrs:r', title: 'Quarterly' }

  it('frames an https address in a sandbox with no referrer', () => {
    const out = html(<Opened opening={{ ...base, mode: 'frame', url: 'https://reports.example.com/ReportServer?x' } as CatalogOpening} source="gw1" values={{}} />)
    expect(out).toContain('data-testid="catalog-frame"')
    expect(out).toContain('sandbox=')
    expect(out).toContain('referrerPolicy="no-referrer"')
  })

  it('never frames an address that is not plain https, and follows a link with no opener', () => {
    expect(html(<Opened opening={{ ...base, mode: 'frame', url: 'javascript:alert(1)' } as CatalogOpening} source={null} values={{}} />)).not.toContain('<iframe')
    const link = html(<Opened opening={{ ...base, mode: 'link', url: 'https://portal.example.org/x' } as CatalogOpening} source={null} values={{}} />)
    expect(link).toContain('rel="noopener noreferrer"')
    expect(link).toContain('target="_blank"')
    expect(html(<Opened opening={{ ...base, mode: 'link', url: 'javascript:alert(1)' } as CatalogOpening} source={null} values={{}} />)).toContain('catalog-no-address')
  })

  it('sends one of our documents to its board with the source and the values', () => {
    const out = html(<Opened opening={{ key: 'views:dashboard/sales', mode: 'document', title: 'Sales', kind: 'dashboard', name: 'sales' }} source="gw1" values={{ region: ['West'] }} />)
    expect(out).toContain('href="/views/dashboards/sales?source=gw1&amp;p.region=West"')
  })
})
