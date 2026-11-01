import { isValidElement, type ReactElement, type ReactNode } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { InitialiseView, initialiseNote } from './DeployTabs'
import { NewFileView, OpenFileToolsView, fileFailure, filePathProblem, type OpenFileToolsViewProps } from './FileTools'
import { CONNECTION_ENGINES, NewConnectionView, connectionPath, connectionProblem, connectionReady, connectionYaml, emptyConnection, type ConnectionForm } from './NewConnectionPanel'
import type { CommandAnswer } from '../types'

function find(node: ReactNode, testId: string): ReactElement<Record<string, unknown>> | undefined {
  if (Array.isArray(node)) { for (const child of node) { const found = find(child, testId); if (found) return found } return undefined }
  if (!isValidElement(node)) return undefined
  const element = node as ReactElement<Record<string, unknown>>
  if (element.props['data-testid'] === testId) return element
  if (typeof element.type === 'function') return find((element.type as (p: unknown) => ReactNode)(element.props), testId)
  return find(element.props.children as ReactNode, testId)
}

const answer = (over: Partial<CommandAnswer<unknown>>): CommandAnswer<unknown> => ({ exit: 0, ...over }) as CommandAnswer<unknown>

describe('New file', () => {
  it('takes a path that stays inside the project and says why when it does not', () => {
    for (const ok of ['connections/postgres.yaml', 'models/staging/orders.sql', 'notes.md']) expect(filePathProblem(ok)).toBeNull()
    for (const bad of ['/etc/passwd', 'C:\\x', 'a\\b', '../x', 'a/../b', 'a//b', 'folder/']) expect(filePathProblem(bad), bad).not.toBeNull()
    expect(filePathProblem('')).toBeNull()
  })

  it('makes nothing without a good path, and not twice at once', () => {
    const disabled = (path: string, busy = false) => find(NewFileView({ path, busy, failure: null, onPath: () => {}, onCreate: () => {}, onCancel: () => {} }), 'build-new-file-create')?.props.disabled
    expect(disabled('')).toBe(true)
    expect(disabled('../x')).toBe(true)
    expect(disabled('a.yml', true)).toBe(true)
    expect(disabled('a.yml')).toBe(false)
  })

  it('reports the tool’s own words when it refused', () => {
    expect(fileFailure(answer({ document: { ok: false, diagnostics: [{ severity: 'error', found: 'the file exists already' }] } as never }), 'x')).toBe('the file exists already')
    expect(fileFailure(answer({ document: { ok: true } as never }), 'x')).toBeNull()
    expect(fileFailure(answer({ exit: 2, text: 'first line\nsecond' }), 'x')).toBe('first line')
    expect(fileFailure(answer({ exit: 2 }), 'The file could not be made.')).toBe('The file could not be made.')
  })
})

describe('The open file', () => {
  const props = (over: Partial<OpenFileToolsViewProps> = {}): OpenFileToolsViewProps => ({ path: 'a.yml', to: 'a.yml', confirming: false, dirty: false, busy: false, failure: null, onTo: () => {}, onRename: () => {}, onAskDelete: () => {}, onDelete: () => {}, onKeep: () => {}, ...over })

  it('renames to another good path, and neither renames nor deletes a file with unsaved changes', () => {
    const rename = (over: Partial<OpenFileToolsViewProps>) => find(OpenFileToolsView(props(over)), 'build-file-rename')?.props.disabled
    expect(rename({})).toBe(true)                                   // the same path
    expect(rename({ to: 'b.yml' })).toBe(false)
    expect(rename({ to: '../b.yml' })).toBe(true)
    expect(rename({ to: 'b.yml', dirty: true })).toBe(true)
    expect(find(OpenFileToolsView(props({ dirty: true })), 'build-file-delete')?.props.disabled).toBe(true)
  })

  it('asks twice before it deletes', () => {
    expect(find(OpenFileToolsView(props()), 'build-file-delete-confirm')).toBeUndefined()
    const asking = OpenFileToolsView(props({ confirming: true }))
    expect(find(asking, 'build-file-delete')).toBeUndefined()
    expect(find(asking, 'build-file-delete-confirm')).toBeDefined()
    expect(renderToStaticMarkup(asking)).toContain('Delete a.yml from your workspace?')
  })
})

describe('New connection', () => {
  const form = (over: Partial<ConnectionForm> = {}): ConnectionForm => ({ ...emptyConnection, name: 'warehouse', engine: 'postgres', ...over })
  const view = (f: ConnectionForm) => NewConnectionView({ form: f, busy: false, failure: null, onChange: () => {}, onCreate: () => {}, onCancel: () => {} })

  it('is the connection/1 file for the form, with no address and no login in it', () => {
    expect(connectionYaml(form({ encrypt: false, trustCertificate: true }))).toBe('schema: connection/1\nname: warehouse\nengine: postgres\ntls:\n  encrypt: false\n  trust_server_certificate: true\n')
    expect(connectionYaml(form())).not.toMatch(/address|host|password|user|auth/)
    expect(connectionPath(form())).toBe('connections/warehouse.yaml')
  })

  it('makes a DuckDB file a read-only origin with nothing else in the file', () => {
    const origin = form({ engine: 'duckdb' })
    expect(connectionYaml(origin)).toBe('schema: connection/1\nname: warehouse\nengine: duckdb\naccess: readonly\n')
    const html = renderToStaticMarkup(view(origin))
    expect(html).not.toContain('build-new-connection-encrypt')
    expect(html).toContain('the login is the path of the file')
  })

  it('needs only a name, and says what is wrong with one', () => {
    expect(connectionReady(form())).toBe(true)
    expect(connectionReady(form({ name: '' }))).toBe(false)
    expect(connectionProblem(form({ name: 'Warehouse' }))).toContain('lower-case')
    expect(connectionProblem(form({ name: '1st' }))).toContain('lower-case')
    expect(connectionProblem(emptyConnection)).toBeNull()
  })

  it('offers the engines the tool builds on and a DuckDB origin, and names the variable the login comes from', () => {
    expect(CONNECTION_ENGINES.map((e) => e.key)).toEqual(['sqlserver', 'postgres', 'fabric', 'duckdb'])
    expect(renderToStaticMarkup(view(form()))).toContain('DBDATA_BUILD_WAREHOUSE_READ')
    expect(find(view(form({ name: '' })), 'build-new-connection-create')?.props.disabled).toBe(true)
    expect(find(view(form()), 'build-new-connection-create')?.props.disabled).toBe(false)
  })
})

describe('Prepare the target', () => {
  it('has a button for each step, both off while one runs, and lists what was done', () => {
    const view = (busy: 'init' | 'seed' | null, done: string[] = []) => InitialiseView({ connection: '', busy, done, answer: undefined, onInit: () => {}, onSeed: () => {} })
    expect(find(view(null), 'build-initialise-tracking')?.props.disabled).toBe(false)
    expect(find(view('init'), 'build-initialise-seed')?.props.disabled).toBe(true)
    expect(renderToStaticMarkup(view(null, ['The tracking tables are on the target.']))).toContain('The tracking tables are on the target.')
    expect(renderToStaticMarkup(view(null))).toContain('the project’s default connection')
  })

  it('says what the command printed last', () => {
    expect(initialiseNote('init', answer({ text: 'script\nCreated 4 objects.\n' }))).toBe('The tracking tables are on the target. Created 4 objects.')
    expect(initialiseNote('seed', answer({}))).toBe('The tables the models read are on the target.')
  })
})
