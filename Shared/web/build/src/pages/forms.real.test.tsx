import { readFileSync, readdirSync, statSync } from 'node:fs'
import { join, relative } from 'node:path'
import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { SchemaForm, SchemaSet, schemaFor, setAt, parseYaml, type SchemaAssociation, type SchemaNode } from '@dbdatakit/shell'

// The schemas and files the console really serves and edits: the Build repository's own (plan 13, W05).
const BUILD = join(__dirname, '../../../../../DbDataBuild')
const ASSOCIATIONS: SchemaAssociation[] = [
  { pattern: 'models/**/_dbdatabuild.yml', schema: 'folder.schema.json', kind: 'folder settings' },
  { pattern: 'models/**/*.yml', schema: 'model.schema.json', kind: 'model' },
  { pattern: 'tests/models/**/*.yml', schema: 'model-test.schema.json', kind: 'model test' },
  { pattern: 'dbdatabuild.yml', schema: 'config.schema.json', kind: 'project' },
]

const schemas = new SchemaSet(Object.fromEntries(readdirSync(join(BUILD, 'schemas')).filter((f) => f.endsWith('.schema.json') && f !== 'output.schema.json').map((f) => [f, JSON.parse(readFileSync(join(BUILD, 'schemas', f), 'utf8')) as SchemaNode])))

function* files(dir: string): Generator<string> {
  for (const name of readdirSync(dir)) {
    const full = join(dir, name)
    if (statSync(full).isDirectory()) yield* files(full)
    else if (name.endsWith('.yml')) yield full
  }
}

const templates = readdirSync(join(BUILD, 'templates')).filter((t) => statSync(join(BUILD, 'templates', t)).isDirectory())
const real = templates.flatMap((t) => [...files(join(BUILD, 'templates', t))].map((f) => ({ template: t, path: relative(join(BUILD, 'templates', t), f), text: readFileSync(f, 'utf8') })))

describe('the forms for the files of the real templates', () => {
  it('has files to try', () => {
    expect(real.length).toBeGreaterThan(20)
    expect(new Set(real.map((f) => schemaFor(ASSOCIATIONS, f.path)?.schema))).toContain('model.schema.json')
  })

  for (const file of real) {
    const association = schemaFor(ASSOCIATIONS, file.path)
    if (!association) continue
    it(`draws ${file.template}/${file.path} as a ${association.kind}`, () => {
      const html = renderToStaticMarkup(<SchemaForm schemas={schemas} schema={association.schema} text={file.text} onText={() => {}} />)
      expect(html).toContain('data-testid="schema-form"')
      expect(html).not.toContain('form-unparsed')
      // a file the tool accepts has nothing the schema calls wrong
      expect(schemas.problems(association.schema, parseYaml(file.text).value ?? {}).map((p) => `${p.path.join('.')}: ${p.message}`)).toEqual([])
    })
  }

  it('changes one value of a real model and nothing else', () => {
    const model = real.find((f) => f.path.startsWith('models/') && f.text.includes('columns:'))!
    const before = parseYaml(model.text).value as { columns: { name: string }[] }
    const after = setAt(model.text, ['columns', 0, 'name'], 'renamed_by_form')
    expect(after.split('\n').length).toBe(model.text.split('\n').length)
    expect((parseYaml(after).value as { columns: { name: string }[] }).columns[0].name).toBe('renamed_by_form')
    expect(after.replace('renamed_by_form', before.columns[0].name)).toBe(model.text)
  })
})
