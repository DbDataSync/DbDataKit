import { describe, expect, it } from 'vitest'
import {
  CatalogError, afterChange, catalogClient, crumbs, documentAddress, fixtureCatalog, followable, framable, initialValues, missingRequired, openValues, parentFolder, readCatalogState, writeCatalogState,
  type CatalogParameter,
} from './catalog'

const country: CatalogParameter = { name: 'Country', type: 'String', many: false, required: true, validValues: [{ value: 'US' }, { value: 'DE' }] }
const region: CatalogParameter = { name: 'Region', type: 'String', many: true, required: false, dependsOn: ['Country'], default: 'East,West' }

describe('the address of the report browser', () => {
  it('writes only what is not the start and keeps other keys, and reads it back', () => {
    const base = new URLSearchParams('source=gw1&folder=%2FOld&q=x&item=a&p.Old=1')
    const next = writeCatalogState(base, { folder: '/Sales', search: '', item: 'ssrs:r1', values: { Country: ['US'], Region: ['East', 'West'] } })
    expect(next.toString()).toBe('source=gw1&folder=%2FSales&item=ssrs%3Ar1&p.Country=US&p.Region=East&p.Region=West')
    expect(readCatalogState(next)).toEqual({ folder: '/Sales', search: '', item: 'ssrs:r1', values: { Country: ['US'], Region: ['East', 'West'] } })
    expect(writeCatalogState(new URLSearchParams(), {}).toString()).toBe('')
    expect(readCatalogState(new URLSearchParams()).folder).toBe('/')
  })

  it('drops the values with the item', () => {
    expect(writeCatalogState(new URLSearchParams('item=a&p.X=1'), { item: null }).toString()).toBe('')
  })

  it('gives the folders from the root and the folder above', () => {
    expect(crumbs('/A/B')).toEqual([{ name: 'A', path: '/A' }, { name: 'B', path: '/A/B' }])
    expect(crumbs('/')).toEqual([])
    expect([parentFolder('/A/B'), parentFolder('/A'), parentFolder('/')]).toEqual(['/A', '/', '/'])
  })
})

describe('the parameters pane', () => {
  it('starts from the address, else the default, and sends one value as it is and several as a list', () => {
    expect(initialValues([country, region], { Country: ['DE'] })).toEqual({ Country: ['DE'], Region: ['East', 'West'] })
    expect(openValues([country, region], { Country: ['DE'], Region: ['East', 'West'] })).toEqual({ Country: 'DE', Region: 'East,West' })
    expect(openValues([country, region], { Country: [''], Region: [] })).toEqual({})
  })

  it('names a required parameter with no value', () => {
    expect(missingRequired([country, region], { Country: [], Region: [] })).toEqual(['Country'])
    expect(missingRequired([country, region], { Country: ['US'] })).toEqual([])
  })

  it('empties what depends on a parameter that changed', () => {
    expect(afterChange([country, region], { Country: ['US'], Region: ['East'] }, 'Country', ['DE'])).toEqual({ Country: ['DE'], Region: [] })
    expect(afterChange([country, region], { Country: ['US'], Region: ['East'] }, 'Region', ['West'])).toEqual({ Country: ['US'], Region: ['West'] })
  })
})

describe('what the page frames and follows', () => {
  it('frames plain https only and follows http or https only', () => {
    expect(framable('https://reports.example.com/ReportServer?x')).toBe(true)
    for (const bad of ['http://reports.example.com', 'javascript:alert(1)', 'data:text/html,x', 'https://u:p@reports.example.com', 'not a url', null, undefined]) expect(framable(bad)).toBe(false)
    expect(followable('http://portal.example.org/x')).toBe(true)
    for (const bad of ['javascript:alert(1)', 'file:///etc/passwd', 'https://u:p@h', '', null]) expect(followable(bad)).toBe(false)
  })

  it('addresses one of our own documents in the Views area with the source, the layout and the values', () => {
    const opening = { key: 'views:dashboard/sales', mode: 'document' as const, title: 'Sales', kind: 'dashboard' as const, name: 'sales', layout: 'kpi' }
    expect(documentAddress(opening, 'gw1', { region: ['West', 'East'], empty: [''] })).toBe('/views/dashboards/sales?source=gw1&layout=kpi&p.region=West&p.region=East')
    expect(documentAddress({ ...opening, kind: 'report' }, null, {})).toBe('/views/reports/sales?layout=kpi')
    expect(documentAddress({ ...opening, mode: 'frame' }, null, {})).toBeNull()
  })
})

describe('the client over the module routes', () => {
  const answer = (data: unknown, status = 200) => async () => new Response(JSON.stringify(data), { status })

  it('browses with the query and reads items, problems and what can be created', async () => {
    let asked = ''
    const client = catalogClient({ baseUrl: '/relay/gw1/', fetch: (async (url: string) => { asked = url; return new Response(JSON.stringify({ items: [{ key: 'ssrs:r1', provider: 'ssrs', id: 'r1', path: '/S/R', name: 'R', type: 'external-report' }], unavailable: [{ provider: 'pbi', code: 'unavailable', message: 'down' }], creatable: { views: ['dashboard'] } })) }) as unknown as typeof fetch })
    const listing = await client.browse({ folder: '/S', search: 'rev', types: ['report', 'dashboard'] })
    expect(asked).toBe('/relay/gw1/api/v1/catalog?folder=%2FS&search=rev&types=report%2Cdashboard')
    expect(listing.items[0]).toMatchObject({ key: 'ssrs:r1', provider: 'ssrs', type: 'external-report' })
    expect(listing.unavailable).toEqual([{ provider: 'pbi', code: 'unavailable', message: 'down' }])
    expect(listing.creatable).toEqual({ views: ['dashboard'] })
  })

  it('reads an item with its parameters and exports and opens with the values and a format', async () => {
    const bodies: unknown[] = []
    const client = catalogClient({
      baseUrl: '/relay/gw1',
      fetch: (async (_url: string, init?: RequestInit) => {
        if (init?.method === 'POST') { bodies.push(JSON.parse(String(init.body))); return new Response(JSON.stringify({ key: 'ssrs:r1', mode: 'link', title: 'R', url: 'https://x.example/f.pdf', parameters: { Country: 'US' } })) }
        return new Response(JSON.stringify({ item: { key: 'ssrs:r1', provider: 'ssrs', id: 'r1', path: '/R', name: 'R', type: 'external-report' }, parameters: [{ name: 'Country', type: 'String', required: true, many: false, valid_values: [{ value: 'US', label: 'United States' }], depends_on: null }], exports: [{ format: 'PDF', available: true }, { format: 'PPTX', available: false, reason: 'no' }], permissions: { known: true, principals: ['CORP\\A'] } }))
      }) as unknown as typeof fetch,
    })
    const detail = await client.item('ssrs:r1')
    expect(detail.parameters[0]).toMatchObject({ name: 'Country', required: true, validValues: [{ value: 'US', label: 'United States' }] })
    expect(detail.exports).toEqual([{ format: 'PDF', available: true, reason: null }, { format: 'PPTX', available: false, reason: 'no' }])
    const opening = await client.open('ssrs:r1', { parameters: { Country: 'US' }, format: 'PDF' })
    expect(bodies).toEqual([{ key: 'ssrs:r1', parameters: { Country: 'US' }, format: 'PDF' }])
    expect(opening).toMatchObject({ mode: 'link', url: 'https://x.example/f.pdf' })
  })

  it('says the module\'s words for a refusal and for no catalogs', async () => {
    const client = catalogClient({ baseUrl: '/relay/gw1', fetch: answer({ code: 'not_found', message: 'No report catalogs are configured on this instance.' }, 404) as unknown as typeof fetch })
    const e = await client.browse().catch((x) => x)
    expect(e).toBeInstanceOf(CatalogError)
    expect(e).toMatchObject({ code: 'not_found', status: 404 })
    expect(e.message).toContain('No report catalogs')
    const down = catalogClient({ baseUrl: '/r', fetch: (async () => { throw new Error('offline') }) as unknown as typeof fetch })
    expect(await down.browse().catch((x) => x)).toMatchObject({ code: 'unavailable' })
  })
})

describe('the sample catalogs', () => {
  it('list two catalogs at the root and a catalog\'s folder, and search across folders', async () => {
    const c = fixtureCatalog()
    expect((await c.browse()).items.map((i) => i.key)).toEqual(['views:folder/dashboards', 'ssrs:f-sales'])
    expect((await c.browse({ folder: '/Sales' })).items.map((i) => i.key)).toEqual(['ssrs:r-quarterly', 'ssrs:r-board'])
    expect((await c.browse({ search: 'revenue' })).items.map((i) => i.provider).sort()).toEqual(['ssrs', 'views'])
  })

  it('opens a framed item, a link, and our document, and an export is a link to a format the item offers only', async () => {
    const c = fixtureCatalog()
    expect(await c.open('ssrs:r-quarterly', { parameters: { Country: 'US' } })).toMatchObject({ mode: 'frame' })
    expect(await c.open('ssrs:r-board')).toMatchObject({ mode: 'link', url: 'https://portal.example.org/powerbi/Sales/Board%20pack' })
    expect(await c.open('views:dashboard/sales')).toMatchObject({ mode: 'document', kind: 'dashboard', name: 'sales' })
    const file = await c.open('ssrs:r-quarterly', { format: 'pdf' })
    expect(file).toMatchObject({ mode: 'link' })
    expect(file.url).toContain('rs:Format=pdf')
    await expect(c.open('ssrs:r-quarterly', { format: 'PPTX' })).rejects.toMatchObject({ code: 'invalid' })
    await expect(c.item('ssrs:nope')).rejects.toMatchObject({ code: 'not_found' })
  })
})
