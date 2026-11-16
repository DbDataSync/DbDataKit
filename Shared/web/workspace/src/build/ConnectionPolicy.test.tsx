import { isValidElement, type ReactElement, type ReactNode } from 'react'
import { describe, expect, it } from 'vitest'
import { blockedFields, ConnectionPolicyView, policyFields, readPolicy, valueProblem, writePolicy } from './ConnectionPolicy'

function find(node: ReactNode, testId: string): ReactElement<Record<string, unknown>> | undefined {
  if (Array.isArray(node)) { for (const child of node) { const found = find(child, testId); if (found) return found } return undefined }
  if (!isValidElement(node)) return undefined
  const element = node as ReactElement<Record<string, unknown>>
  if (element.props['data-testid'] === testId) return element
  if (typeof element.type === 'function') return find((element.type as (p: unknown) => ReactNode)(element.props), testId)
  return find(element.props.children as ReactNode, testId)
}

const FILE = 'schema: connection/1\nname: warehouse\nengine: sqlserver\ntls:\n  encrypt: true\n'

describe('The serving policy of a connection file', () => {
  it('is read from extensions.gateway, none when the file has none', () => {
    expect(readPolicy(FILE)).toEqual({ values: {}, others: [], blocked: [], problem: null })
    const policy = readPolicy(`${FILE}extensions:\n  build:\n    version: 16\n  gateway:\n    batch_size: 1000\n    classifier: strict\n    pass_through: true\n    compression:\n      enabled: true\n`)
    expect(policy.values).toEqual({ batch_size: 1000, classifier: 'strict', pass_through: true })
    expect(policy.others).toEqual(['compression'])
    expect(policy.problem).toBeNull()
  })

  it('names the two fields a definition never carries, and says why it cannot show a block it cannot read', () => {
    expect(readPolicy(`${FILE}extensions:\n  gateway:\n    allow_writes: true\n    allowed_directories: [/data]\n    batch_size: 5\n`).blocked).toEqual(['allow_writes', 'allowed_directories'])
    expect(readPolicy('a: [').problem).toMatch(/not valid YAML/)
    expect(readPolicy(`${FILE}extensions: nope\n`).problem).toMatch(/mapping/)
    expect(readPolicy(`${FILE}extensions:\n  gateway: nope\n`).problem).toMatch(/mapping of serving policy fields/)
    expect(readPolicy(`${FILE}extensions:\n  gateway:\n    batch_size: [1]\n`).problem).toMatch(/single value/)
  })

  it('is written into the file and taken out again, and nothing else of the file changes', () => {
    const one = writePolicy(FILE, 'batch_size', 1000)
    expect(readPolicy(one).values).toEqual({ batch_size: 1000 })
    expect(one).toContain('tls:\n  encrypt: true')
    const two = writePolicy(writePolicy(one, 'classifier', 'strict'), 'pass_through', true)
    expect(readPolicy(two).values).toEqual({ batch_size: 1000, classifier: 'strict', pass_through: true })
    expect(readPolicy(writePolicy(two, 'batch_size', 2000)).values.batch_size).toBe(2000)
    const less = writePolicy(two, 'classifier', undefined)
    expect(readPolicy(less).values).toEqual({ batch_size: 1000, pass_through: true })
    // the last field takes the block, and the extensions with it when nothing else is in them
    expect(writePolicy(one, 'batch_size', undefined)).toBe(FILE)
    const withBuild = `${FILE}extensions:\n  build:\n    version: 16\n`
    expect(writePolicy(writePolicy(withBuild, 'batch_size', 5), 'batch_size', undefined)).toBe(withBuild)
  })

  it('cannot be asked to write allow_writes or allowed_directories, or a key it does not know', () => {
    for (const key of blockedFields) expect(() => writePolicy(FILE, key, true)).toThrow(/not part of a definition/)
    expect(() => writePolicy(FILE, 'groups', 'a')).toThrow(/not a policy field/)
    expect(policyFields.map((f) => f.key)).not.toContain('allow_writes')
    expect(policyFields.map((f) => f.key)).not.toContain('allowed_directories')
  })

  it('refuses a value out of range or of the wrong kind with a sentence', () => {
    const batch = policyFields.find((f) => f.key === 'batch_size')!
    expect(valueProblem(batch, 0)).toMatch(/from 1 to 1000000/)
    expect(valueProblem(batch, 5)).toBeNull()
    expect(valueProblem(batch, undefined)).toBeNull()
    expect(valueProblem(policyFields.find((f) => f.key === 'classifier')!, 'sometimes')).toMatch(/one of warn, strict, off/)
  })
})

describe('The policy editor', () => {
  const props = (text: string, over: { readOnly?: boolean } = {}) => ({ policy: readPolicy(text), readOnly: over.readOnly ?? false, onChange: () => {} })

  it('says a publish replaces the Gateway’s policy and that allow_writes and the directories are not part of a definition', () => {
    const view = ConnectionPolicyView(props(FILE))
    const note = JSON.stringify(find(view, 'build-policy-note')?.props.children)
    expect(note).toMatch(/replaces the Gateway.s policy for this data source completely/)
    expect(note).toMatch(/not part of a definition/)
  })

  it('draws every offered field and never one for the two blocked fields', () => {
    const view = ConnectionPolicyView(props(`${FILE}extensions:\n  gateway:\n    batch_size: 1000\n`))
    for (const field of policyFields) expect(find(view, `build-policy-field-${field.key}`), field.key).toBeDefined()
    for (const key of blockedFields) expect(find(view, `build-policy-field-${key}`), key).toBeUndefined()
    expect(find(view, 'build-policy-input-batch_size')?.props.value).toBe('1000')
  })

  it('warns when the file carries a blocked field, and shows what it does not draw', () => {
    const view = ConnectionPolicyView(props(`${FILE}extensions:\n  gateway:\n    allow_writes: true\n    compression:\n      enabled: true\n`))
    expect(JSON.stringify(find(view, 'build-policy-blocked')?.props.children)).toMatch(/allow_writes/)
    expect(JSON.stringify(find(view, 'build-policy-others')?.props.children)).toMatch(/compression/)
  })

  it('changes nothing for a reader and when the file’s policy cannot be read', () => {
    for (const view of [ConnectionPolicyView(props(FILE, { readOnly: true })), ConnectionPolicyView(props('a: ['))]) {
      expect(find(view, 'build-policy-input-batch_size')?.props.disabled).toBe(true)
      expect(find(view, 'build-policy-input-pass_through')?.props.disabled).toBe(true)
    }
  })
})
