import { isValidElement, type ReactElement, type ReactNode } from 'react'
import { describe, expect, it } from 'vitest'
import { ConnectionGrantsView, groupProblem, isConnectionFile, noGrants, readGrants, writeGrants, type ConnectionGrantsViewProps } from './ConnectionGrants'

function find(node: ReactNode, testId: string): ReactElement<Record<string, unknown>> | undefined {
  if (Array.isArray(node)) { for (const child of node) { const found = find(child, testId); if (found) return found } return undefined }
  if (!isValidElement(node)) return undefined
  const element = node as ReactElement<Record<string, unknown>>
  if (element.props['data-testid'] === testId) return element
  if (typeof element.type === 'function') return find((element.type as (p: unknown) => ReactNode)(element.props), testId)
  return find(element.props.children as ReactNode, testId)
}

const FILE = 'schema: connection/1\nname: warehouse\nengine: sqlserver\ntls:\n  encrypt: true\n'

describe('The grants of a connection file', () => {
  it('are read from the file, none when it has none', () => {
    expect(readGrants(FILE)).toEqual({ grants: noGrants, problem: null })
    expect(readGrants(`${FILE}grants:\n  groups: [Finance, auditors]\n`).grants).toEqual({ groups: ['Finance', 'auditors'], everyone: false })
    expect(readGrants(`${FILE}grants: { everyone: true }\n`).grants).toEqual({ groups: [], everyone: true })
  })

  it('say why they cannot be read instead of showing none', () => {
    expect(readGrants('a: [').problem).toMatch(/not valid YAML/)
    expect(readGrants(`${FILE}grants: nope\n`).problem).toMatch(/mapping/)
    expect(readGrants(`${FILE}grants:\n  groups: 3\n`).problem).toMatch(/list of group names/)
    expect(readGrants(`${FILE}grants:\n  everyone: maybe\n`).problem).toMatch(/true or false/)
  })

  it('are written with the schema of format 2, and the schema goes back to format 1 when the last is taken away', () => {
    const one = writeGrants(FILE, { groups: ['Finance'], everyone: false })
    expect(one.startsWith('schema: connection/2\n')).toBe(true)
    expect(readGrants(one).grants).toEqual({ groups: ['Finance'], everyone: false })
    expect(one).toContain('tls:\n  encrypt: true')                   // the rest of the file is as it was
    const two = writeGrants(one, { groups: ['Finance', 'auditors'], everyone: false })
    expect(readGrants(two).grants.groups).toEqual(['Finance', 'auditors'])
    const open = writeGrants(two, { groups: [], everyone: true })
    expect(readGrants(open).grants).toEqual({ groups: [], everyone: true })
    expect(open).not.toContain('Finance')
    const closed = writeGrants(open, noGrants)
    expect(closed.startsWith('schema: connection/1\n')).toBe(true)
    expect(closed).not.toContain('grants')
    expect(closed).toBe(FILE)
  })

  it('belong to the connection files of a project and nothing else', () => {
    expect(isConnectionFile('connections/warehouse.yaml')).toBe(true)
    for (const other of ['models/a.yml', 'connections/a/b.yaml', 'connections/a.yml', 'dbdatabuild.yml']) expect(isConnectionFile(other), other).toBe(false)
  })

  it('refuse a name that is a repeat or has stray spaces', () => {
    expect(groupProblem('', [])).toBeNull()
    expect(groupProblem('finance', ['Finance'])).toMatch(/listed already/)
    expect(groupProblem(' finance', [])).toMatch(/leading or trailing/)
    expect(groupProblem('auditors', ['Finance'])).toBeNull()
  })
})

describe('The grants editor', () => {
  const props = (over: Partial<ConnectionGrantsViewProps> = {}): ConnectionGrantsViewProps => ({ grants: { groups: ['Finance'], everyone: false }, problem: null, draft: '', readOnly: false, onDraft: () => {}, onAdd: () => {}, onRemove: () => {}, onEveryone: () => {}, ...over })

  it('says a publish replaces the Gateway’s grants and lists the groups with a way to remove each', () => {
    const view = ConnectionGrantsView(props())
    expect(JSON.stringify(find(view, 'build-grants-note')?.props.children)).toMatch(/replaces the Gateway.s grants completely/)
    expect(find(view, 'build-grants-group-Finance')).toBeDefined()
    expect(find(view, 'build-grants-remove-Finance')?.props.disabled).toBe(false)
    expect(find(ConnectionGrantsView(props({ grants: noGrants })), 'build-grants-none')).toBeDefined()
  })

  it('adds a good name only, and offers no groups while everyone is on', () => {
    expect(find(ConnectionGrantsView(props({ draft: '' })), 'build-grants-add')?.props.disabled).toBe(true)
    expect(find(ConnectionGrantsView(props({ draft: 'finance' })), 'build-grants-add')?.props.disabled).toBe(true)      // a repeat
    expect(find(ConnectionGrantsView(props({ draft: 'auditors' })), 'build-grants-add')?.props.disabled).toBe(false)
    const open = ConnectionGrantsView(props({ grants: { groups: [], everyone: true } }))
    expect(find(open, 'build-grants-everyone')?.props.checked).toBe(true)
    expect(find(open, 'build-grants-group-input')).toBeUndefined()
  })

  it('changes nothing for a reader and when the file’s grants cannot be read', () => {
    for (const over of [{ readOnly: true }, { problem: 'broken' }]) {
      const view = ConnectionGrantsView(props({ ...over, draft: 'auditors' }))
      expect(find(view, 'build-grants-add')?.props.disabled).toBe(true)
      expect(find(view, 'build-grants-everyone')?.props.disabled).toBe(true)
    }
  })
})
