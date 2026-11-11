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

describe('the shape preview (SC20k)', () => {
  const sound2 = sound.replace('{name: total, type: numeric}', '{name: total, type: "numeric(12,2)", nullable: false}, {name: note, type: text}, {name: placed, type: timestamp}, {name: active, type: boolean}, {name: ref, type: uuid}, {name: blob, type: geometry}')

  it('offers a preview per table and draws none until one is chosen', () => {
    const html = renderToStaticMarkup(<CatalogView read={readCatalogCache(sound2)} present onOpen={() => {}} preview={null} onPreview={() => {}} />)
    expect(html).toContain('Preview the shape')
    expect(html).not.toContain('wsp-shape-table')
  })

  it('draws the chosen table: the types as headers, generated values, one null for a nullable column, and the note that they are not rows', () => {
    const html = renderToStaticMarkup(<CatalogView read={readCatalogCache(sound2)} present onOpen={() => {}} preview="src/public.orders" onPreview={() => {}} />)
    expect(html).toContain('Hide the shape')
    expect(html).toContain('not rows of the database')
    expect(html).toContain('id (key)')
    expect(html).toContain('>1<')
    expect(html).toContain('>21.00<')
    expect(html).toContain('note_1')
    expect(html).toContain('2026-01-01 08:00:00')
    expect(html).toContain('00000000-0000-4000-8000-000000000001')
    expect(html).toContain('&lt;geometry&gt;')
    expect(html).toContain('>null<')
  })
})
