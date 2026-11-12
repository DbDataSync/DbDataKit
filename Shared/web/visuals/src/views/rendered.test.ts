import Ajv2020 from 'ajv/dist/2020'
import type { ValidateFunction } from 'ajv'
import { describe, expect, it } from 'vitest'
import renderedSchema from '../../schemas/rendered-widgets.schema.json'
import { fixtureClient } from '../client/fixtureClient'
import { sampleBundle } from '../testing/sampleBundle'
import type { RenderedWidget, RenderedWidgets } from './rendered'

/**
 * The recorded widget results (`conformance/views/widget-results`) are what the .NET `WidgetResolver` gave over the retail sample (`ResolveTests`, `DbDataKit.Views.Tests`, which also checks them against this same
 * schema), so .NET and TypeScript agree on the one shape: every file validates against `rendered-widgets.schema.json`, and the schema's widget types are exactly the TypeScript union's.
 */
const AjvCtor = ((Ajv2020 as unknown as { default?: typeof Ajv2020 }).default ?? Ajv2020) as typeof Ajv2020
const ajv = new AjvCtor({ strict: false, allErrors: true })
const id = (renderedSchema as { $id: string }).$id
ajv.addSchema(renderedSchema as object)
const whole = ajv.getSchema(id) as ValidateFunction
const widget = ajv.getSchema(`${id}#/$defs/widget`) as ValidateFunction
const problems = (v: ValidateFunction) => (v.errors ?? []).map((e) => `${e.instancePath} ${e.message}`)

const bundle = sampleBundle()

// the union of the TypeScript types, written so that adding a type to one place and not the other fails to compile or fails the test
const TYPES = ['metric', 'table', 'matrix', 'chart', 'text', 'image', 'control', 'embed', 'custom', 'error'] as const satisfies ReadonlyArray<RenderedWidget['type']>
type Covered = (typeof TYPES)[number]
const everyTypeIsListed: Exclude<RenderedWidget['type'], Covered> extends never ? true : false = true

describe('rendered-widgets', () => {
  it('a custom widget carries the rows of its declared queries by name; an extra key or a row that is not an object is refused', () => {
    const good = { type: 'custom', component: 'sales-portal/RegionGrid', params: { region: ['East'] }, data: { by_region: { columns: ['customers_region'], rows: [{ customers_region: 'East' }] } } }
    expect(widget(good), problems(widget).join('\n')).toBe(true)
    expect(widget({ ...good, html: '<b>x</b>' })).toBe(false)
    expect(widget({ ...good, data: { q: { columns: [], rows: ['x'] } } })).toBe(false)
    expect(widget({ type: 'custom', component: 'a/B' })).toBe(false)
  })


  it('lists exactly the widget types of the TypeScript union', () => {
    expect(everyTypeIsListed).toBe(true)
    const listed = (renderedSchema.$defs.widget.oneOf as Array<{ $ref: string }>).map((o) => (renderedSchema.$defs as unknown as Record<string, { properties: { type: { const: string } } }>)[o.$ref.split('/').pop()!]!.properties.type.const)
    expect([...listed].sort()).toEqual([...TYPES].sort())
  })

  it.each(Object.entries(bundle.results!).flatMap(([doc, widgets]) => Object.entries(widgets).map(([name, w]) => [`${doc}/${name}`, w] as const)))('the recorded result %s validates', (_name, recorded) => {
    expect(widget(recorded), problems(widget).join('\n')).toBe(true)
  })

  it('the bundle has a recorded result for every widget that runs a query, and a control that lists values', () => {
    expect(Object.values(bundle.results!).flatMap((r) => Object.keys(r)).sort()).toEqual(
      ['by_month', 'by_region', 'margin', 'margin', 'net', 'net', 'products', 'regions', 'top_products', 'units'],
    )
  })

  it.each([
    [{ kind: 'dashboard', name: 'retail-sales-today' }, 'desk'],
    [{ kind: 'dashboard', name: 'retail-sales-today' }, 'phone'],
    [{ kind: 'report', name: 'retail-sales-monthly' }, 'paged'],
  ] as const)('a resolution of %j in %s validates as a whole', async (ref, layout) => {
    const resolved: RenderedWidgets = await fixtureClient(bundle).resolve(ref, layout, {})
    expect(whole(resolved), problems(whole).join('\n')).toBe(true)
  })

  it('a server resolution, with the substituted text and an error entry, validates', () => {
    const resolved: RenderedWidgets = {
      kind: 'rendered_widgets',
      layout: 'desk',
      as_of: '2026-11-08T09:00:00Z',
      widgets: {
        note: { type: 'text', text: 'Showing **West, East**' },
        net: { type: 'metric', value: { v: 5244133.06, text: '5.2M' }, compare: { v: 4719719.75, text: '4.7M', change: { text: '+11.1%', direction: 'up' } } },
        lost: { type: 'error', code: 'denied', message: 'You may not read retail_sales.', path: 'widgets.lost.query.model', suggestions: null },
      },
    }
    expect(whole(resolved), problems(whole).join('\n')).toBe(true)
  })

  it('refuses what the renderers do not read', () => {
    const bad = (widgets: unknown) => whole({ kind: 'rendered_widgets', layout: 'desk', as_of: '2026-11-08T09:00:00Z', widgets })
    expect(bad({ a: { type: 'metric', value: { text: 3 } } })).toBe(false)
    expect(bad({ a: { type: 'image', src: 'https://example.com/x.png' } })).toBe(false)
    expect(bad({ a: { type: 'table', columns: [], rows: [{ cells: [{ v: { nested: 1 }, text: '' }] }] } })).toBe(false)
    expect(bad({ a: { type: 'gauge' } })).toBe(false)
    expect(whole({ kind: 'rendered_widgets', layout: 'desk', as_of: 'yesterday', widgets: {} })).toBe(false)
  })
})
