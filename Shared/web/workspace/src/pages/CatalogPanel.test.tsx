import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { readCatalogCache } from '../catalogCache'
import { CatalogView } from './CatalogPanel'

const sound = [
  'schema: catalog-cache/1', 'generated_at: 2026-11-11T10:00:00Z', 'source: {server: prod-gateway, product: gateway}', 'connections:',
  '  src:', '    engine: postgres', '    tables:', '      - {schema: public, name: orders, kind: table, columns: [{name: id, type: integer, key: true}, {name: total, type: numeric}]}', '',
].join('\n')

describe('the catalog panel', () => {
  it('says there is no cache and what one is', () => {
    const html = renderToStaticMarkup(<CatalogView read={null} present={false} onOpen={() => {}} />)
    expect(html).toContain('wsp-catalog-none')
    expect(html).toContain('never a row and never a secret')
  })

  it('lists the connections and tables of a sound cache and where it came from', () => {
    const html = renderToStaticMarkup(<CatalogView read={readCatalogCache(sound)} present onOpen={() => {}} />)
    expect(html).toContain('from prod-gateway (gateway)')
    expect(html).toContain('data-connection="src"')
    expect(html).toContain('data-table="public.orders"')
    expect(html).toContain('table, 2 columns')
    expect(html).not.toContain('wsp-catalog-finding')
  })

  it('shows the strict reader\'s findings with their key path and lists no table of a damaged file', () => {
    const html = renderToStaticMarkup(<CatalogView read={readCatalogCache(sound.replace('key: true}', 'key: true, rows: []}'))} present onOpen={() => {}} />)
    expect(html).toContain('data-code="DBD-CAT-002"')
    expect(html).toContain('connections.src.tables[0].columns[0].rows')
    expect(html).not.toContain('wsp-catalog-table')
  })
})
