import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { parseYaml } from '@dbdatakit/shell'
import { BUILD_ASSOCIATIONS, BUILD_SCHEMAS, buildSchemaOf, buildSchemaSet, modelFilesOf, modelLayoutOf, newModelClash, newModelFiles } from '../buildSchemas'
import { editorKindOf } from '../editors'
import type { LiveCheck } from '../useLiveCheck'
import { BuildFileEditor, NewBuildConnection, NewBuildModel, connectionsOfDraft } from './BuildFilePanel'

const idle: LiveCheck = { diagnostics: [], checkedText: null, running: false, failed: null }
const noop = () => {}
const root = fileURLToPath(new URL('../../../../../DbDataBuild/', import.meta.url))

describe("Build's schemas, imported", () => {
  it('are the files of DbDataBuild/schemas, without the command output schema', () => {
    expect(Object.keys(BUILD_SCHEMAS)).toEqual(expect.arrayContaining(['model.schema.json', 'declared-model.schema.json', 'connection.schema.json', 'semantic.schema.json', 'folder.schema.json', 'config.schema.json']))
    expect(Object.keys(BUILD_SCHEMAS)).not.toContain('output.schema.json')
  })
  it('are associated with files as the Build server does', () => {
    const text = readFileSync(`${root}src/DbDataBuild.Cli/Server/FileSchemas.cs`, 'utf8')
    const server = [...text.matchAll(/new\("([^"]+)", "([^"]+)", "([^"]+)"\)/g)].map((m) => ({ pattern: m[1], schema: m[2], kind: m[3] }))
    expect(server.length).toBeGreaterThan(5)
    expect([...BUILD_ASSOCIATIONS]).toEqual(server)
  })
  it('say what a file is by where it is', () => {
    expect(buildSchemaOf('models/marts/fct.yml')?.kind).toBe('model')
    expect(buildSchemaOf('models/marts/_dbdatabuild.yml')?.kind).toBe('folder settings')
    expect(buildSchemaOf('semantic/orders.yml')?.kind).toBe('semantic model')
    expect(buildSchemaOf('connections/warehouse.yaml')?.kind).toBe('connection')
    expect(buildSchemaOf('models/marts/fct.sql')).toBeUndefined()
    expect(buildSchemaOf('rendered/a/loads.yml')).toBeUndefined()
  })
  it('give a Build project its structured editors and no other kind', () => {
    expect(editorKindOf('build', 'models/marts/fct.yml')).toBe('build-file')
    expect(editorKindOf('build', 'connections/w.yaml')).toBe('build-file')
    expect(editorKindOf('build', 'models/marts/fct.sql')).toBeNull()
    expect(editorKindOf('store', 'connections/w.yaml')).toBeNull()
  })
})

describe('a new model by kind', () => {
  it.each(['view', 'full', 'incremental_by_unique_key', 'incremental_by_time_range'])('writes a definition of the %s kind that the schema accepts', (kind) => {
    const files = newModelFiles('marts.fct_returns', kind, [], 'nested')
    expect(files.map((f) => f.path)).toEqual(['models/marts/fct_returns.yml', 'models/marts/fct_returns.sql'])
    const parsed = parseYaml(files[0].text)
    expect(parsed.errors).toEqual([])
    expect(buildSchemaSet.problems('model.schema.json', parsed.value)).toEqual([])
  })
  it('names the connections it lives on and the key of an incremental model', () => {
    const [yml] = newModelFiles('marts.fct', 'incremental_by_unique_key', ['wh', 'lake'], 'nested')
    expect(yml.text).toContain('connections: [wh, lake]')
    expect(yml.text).toContain('kind: {incremental_by_unique_key: {unique_key: [id]}}')
    expect(yml.text).toContain('ux_fct_id')
  })
  it('puts the files where the project keeps them, and refuses a name that is taken', () => {
    expect(modelLayoutOf(['models/marts.fct.sql'])).toBe('dotted')
    expect(modelLayoutOf(['models/marts/fct.sql'])).toBe('nested')
    expect(modelFilesOf('marts.fct', 'dotted')).toEqual({ yml: 'models/marts.fct.yml', sql: 'models/marts.fct.sql' })
    expect(newModelClash('marts.fct', 'nested', ['models/marts/fct.sql'])).toContain('exists already')
    expect(newModelClash('marts.other', 'nested', ['models/marts/fct.sql'])).toBeNull()
  })
  it('draws its name box, the kinds and a connection choice when there are several', () => {
    const html = renderToStaticMarkup(<NewBuildModel paths={['connections/a.yaml', 'connections/b.yaml']} busy={false} onCreate={noop} />)
    expect(html).toContain('data-testid="wsp-build-new-model-name"')
    expect(html).toContain('Incremental by key')
    expect(html).toContain('data-testid="wsp-build-new-model-on-a"')
    expect(connectionsOfDraft(['connections/a.yaml', 'models/x.yml'])).toEqual(['a'])
  })
})

describe('a new connection', () => {
  it('draws the name, the engines and how it is secured', () => {
    const html = renderToStaticMarkup(<NewBuildConnection paths={[]} busy={false} onCreate={noop} />)
    expect(html).toContain('data-testid="wsp-build-new-connection-name"')
    expect(html).toContain('PostgreSQL')
    expect(html).toContain('data-testid="wsp-build-new-connection-encrypt"')
  })
})

describe('the forms for Build files', () => {
  const connection = 'schema: connection/2\nname: warehouse\nengine: postgres\ngrants:\n  groups: [analysts]\nextensions:\n  gateway:\n    classifier: strict\n'
  it('draws a connection as a form with its grants and serving policy', () => {
    const html = renderToStaticMarkup(<BuildFileEditor path="connections/warehouse.yaml" text={connection} onText={noop} readOnly={false} live={idle} />)
    expect(html).toContain('data-testid="schema-form"')
    expect(html).toContain('data-testid="build-grants"')
    expect(html).toContain('analysts')
    expect(html).toContain('data-testid="wsp-build-connection-note"')
  })
  it('draws a model definition and a semantic file as forms, a folder file too', () => {
    const model = newModelFiles('marts.fct', 'full', [], 'nested')[0]
    expect(renderToStaticMarkup(<BuildFileEditor path={model.path} text={model.text} onText={noop} readOnly={false} live={idle} />)).toContain('data-testid="schema-form"')
    expect(renderToStaticMarkup(<BuildFileEditor path="semantic/orders.yml" text={'name: orders\n'} onText={noop} readOnly={false} live={idle} />)).toContain('data-testid="schema-form"')
    expect(renderToStaticMarkup(<BuildFileEditor path="models/marts/_dbdatabuild.yml" text="" onText={noop} readOnly={false} live={idle} />)).toContain('data-testid="wsp-structured"')
  })
  it('does not draw a connection editor over a model', () => {
    const html = renderToStaticMarkup(<BuildFileEditor path="models/marts/fct.yml" text={'name: marts.fct\n'} onText={noop} readOnly live={idle} />)
    expect(html).not.toContain('data-testid="build-grants"')
  })
})
