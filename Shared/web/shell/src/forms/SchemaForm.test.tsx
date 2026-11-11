import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { defaultFor } from './defaults'
import { SchemaForm, type FormOverride } from './SchemaForm'
import { SchemaSet, type SchemaNode } from './schemaSet'

const SCHEMA: SchemaNode = {
  $id: 'thing.schema.json',
  type: 'object',
  additionalProperties: false,
  required: ['name'],
  properties: {
    name: { description: 'What it is called. More words follow.', type: 'string', minLength: 1 },
    size: { type: 'integer' },
    active: { type: 'boolean' },
    mode: { enum: ['fast', 'slow'] },
    tags: { type: 'array', items: { type: 'string' } },
    columns: { type: 'array', items: { $ref: '#/$defs/column' } },
    kind: { oneOf: [{ title: 'plain', const: 'view' }, { title: 'with settings', type: 'object', required: ['full'], additionalProperties: false, properties: { full: { type: 'object', properties: {} } } }] },
    labels: { type: 'object', additionalProperties: { type: 'string' } },
    free: {},
    cases: { type: 'array', items: { type: 'object', required: ['name'], anyOf: [{ required: ['expect'] }, { required: ['assert'] }], properties: { name: { type: 'string' }, expect: { type: 'array' }, assert: { type: 'string' } } } },
  },
  $defs: { column: { type: 'object', required: ['name', 'type'], properties: { name: { type: 'string' }, type: { type: 'string' } } } },
}

const schemas = () => new SchemaSet({ 'thing.schema.json': SCHEMA })
const render = (text: string, overrides: FormOverride[] = []) =>
  renderToStaticMarkup(<SchemaForm schemas={schemas()} schema="thing.schema.json" text={text} onText={() => {}} overrides={overrides} />)

describe('a form drawn from a schema', () => {
  it('draws the fields the document has, with their values, and offers the others to add', () => {
    const html = render('name: orders\nsize: 5\nmode: slow\nactive: true\n')

    expect(html).toContain('data-testid="form-field-name"')
    expect(html).toContain('value="orders"')
    expect(html).toContain('value="5"')
    expect(html).toContain('<option value="slow" selected="">slow</option>')
    expect(html).toContain('data-testid="form-field-active" checked=""')
    expect(html).toContain('data-testid="form-add-tags"')           // tags is not in the file: offered, not drawn
    expect(html).not.toContain('data-testid="form-field-tags"')
    expect(html).toContain('>What it is called.</span>')              // the first sentence of the description is the hint, the whole text its tooltip
    expect(html).not.toContain('>What it is called. More words follow.</span>')
  })

  it('always draws the required fields, empty when the file lacks them, and marks them', () => {
    const html = render('size: 1\n')

    expect(html).toContain('data-testid="form-field-name"')
    expect(html).toContain('title="required"')
    expect(html).toContain('`name` is required.')
  })

  it('draws lists with an item each, following references into definitions', () => {
    const html = render('name: x\ncolumns:\n  - {name: a, type: INT}\n  - {name: b, type: TEXT}\ntags: [one, two]\n')

    expect(html).toContain('data-testid="form-list-columns"')
    expect(html).toContain('data-testid="form-field-columns.1.type"')
    expect(html).toContain('value="TEXT"')
    expect(html).toContain('data-testid="form-field-tags.1"')
    expect(html).toContain('data-testid="form-append-columns"')
  })

  it('draws a choice between shapes with the one the value fits chosen', () => {
    const plain = render('name: x\nkind: view\n')
    expect(plain).toContain('data-testid="form-variant-kind"')
    expect(plain).toContain('<option value="0" selected="">plain</option>')

    const settings = render('name: x\nkind: {full: {}}\n')
    expect(settings).toContain('<option value="1" selected="">with settings</option>')
    expect(settings).toContain('data-testid="form-object-kind.full"')
  })

  it('draws an object with a rule that needs one of several keys as an object, not as a choice of shapes', () => {
    const html = render('name: x\ncases:\n  - {name: first, assert: select 1}\n')

    expect(html).toContain('data-testid="form-field-cases.0.name"')
    expect(html).not.toContain('form-variant-cases.0')
  })

  it('shows a value that fits no shape as YAML in place rather than guessing', () => {
    const html = render('name: x\nkind: [nope]\n')

    expect(html).toContain('data-testid="form-raw-kind"')
    expect(html).toContain('- nope')
  })

  it('draws mappings of one kind of value as name and value rows', () => {
    const html = render('name: x\nlabels: {team: finance, owner: ana}\n')

    expect(html).toContain('data-testid="form-field-labels.team"')
    expect(html).toContain('value="ana"')
    expect(html).toContain('data-testid="form-addkey-labels"')
  })

  it('draws a part the schema leaves open as YAML, and keys the schema does not know with a way to remove them', () => {
    const open = render('name: x\nfree: {a: 1}\n')
    expect(open).toContain('data-testid="form-raw-free"')

    const unknown = render('name: x\nsurprise: 1\n')
    expect(unknown).toContain('data-testid="form-raw-surprise"')
    expect(unknown).toContain('`surprise` is not a setting here.')
  })

  it('says why there is no form when the YAML does not parse', () => {
    const html = render('name: [x\n')

    expect(html).toContain('data-testid="form-unparsed"')
    expect(html).toContain('Fix it in the YAML tab')
    expect(html).not.toContain('data-testid="schema-form"')
  })

  it('lets an override draw a part its own way while the rest stays generic', () => {
    const override: FormOverride = {
      match: (ctx) => ctx.path.join('.') === 'mode',
      render: (props) => <b data-testid="custom-mode">{String(props.value)} for {props.label}</b>,
    }
    const html = render('name: x\nmode: fast\n', [override])

    expect(html).toContain('<b data-testid="custom-mode">fast for mode</b>')
    expect(html).toContain('data-testid="form-field-name"')
  })

  it('starts new parts from what the schema requires', () => {
    expect(defaultFor(schemas(), { base: 'thing.schema.json', pointer: '/$defs/column' })).toEqual({ name: '', type: '' })
    expect(defaultFor(schemas(), { base: 'thing.schema.json', pointer: '/properties/tags' })).toEqual([])
    expect(defaultFor(schemas(), { base: 'thing.schema.json', pointer: '/properties/active' })).toBe(false)
  })
})
