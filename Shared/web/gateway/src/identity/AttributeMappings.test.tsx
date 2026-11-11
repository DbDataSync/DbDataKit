import { isValidElement, type ReactElement, type ReactNode } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it, vi } from 'vitest'
import type { AttributeMapping } from './api'
import { AttributeMappingsView, type AttributeMappingsViewProps } from './AttributeMappings'

const east: AttributeMapping = { id: 'a', source: 'entra', group: 'g-east', attribute: 'region', value: 'east' }

function props(over: Partial<AttributeMappingsViewProps> = {}): AttributeMappingsViewProps {
  return { rows: [east], loading: false, loadError: null, form: { source: 'entra', group: 'g-west', attribute: 'region', value: 'west' }, busy: false, refusal: null, onForm: () => {}, onAdd: () => {}, onRemove: () => {}, ...over }
}

function find(node: ReactNode, testId: string): ReactElement<Record<string, unknown>> | undefined {
  if (Array.isArray(node)) { for (const child of node) { const found = find(child, testId); if (found) return found } return undefined }
  if (!isValidElement(node)) return undefined
  const element = node as ReactElement<Record<string, unknown>>
  if (element.props['data-testid'] === testId) return element
  return find(element.props.children as ReactNode, testId)
}

const tree = (over?: Partial<AttributeMappingsViewProps>) => AttributeMappingsView(props(over))
const html = (over?: Partial<AttributeMappingsViewProps>) => renderToStaticMarkup(tree(over))

describe('the attribute mappings card', () => {
  it('lists the mappings and what each attribute can be', () => {
    const text = html()
    expect(text).toContain('data-testid="identity-attribute-region-east"')
    expect(text).toContain('g-east')
    expect(text).toContain('region: east')
  })

  it('says so while it reads, when it cannot, and when there is none', () => {
    expect(html({ loading: true, rows: undefined })).toContain('identity-attribute-loading')
    expect(html({ loadError: 'There is no such call.', rows: undefined })).toContain('could not be read: There is no such call.')
    const empty = html({ rows: [] })
    expect(empty).toContain('identity-attribute-empty')
    expect(html({ loadError: 'x', rows: [] })).not.toContain('identity-attribute-empty')
  })

  it('maps when the form is complete and not before, saying what is missing', () => {
    expect(find(tree(), 'identity-attribute-add')!.props.disabled).toBe(false)
    const missing = tree({ form: { source: 'entra', group: '', attribute: 'region', value: 'west' } })
    expect(find(missing, 'identity-attribute-add')!.props.disabled).toBe(true)
    expect(find(missing, 'identity-attribute-add')!.props.title).toBe('Name the external group.')
    expect(html({ form: { source: 'entra', group: '', attribute: 'region', value: 'west' } })).toContain('data-testid="identity-attribute-why"')
  })

  it('shows an attribute name the home would refuse, and keeps Map off', () => {
    const bad = tree({ form: { source: 'entra', group: 'g', attribute: 'Region', value: 'x' } })
    expect(find(bad, 'identity-attribute-add')!.props.disabled).toBe(true)
    expect(renderToStaticMarkup(bad)).toContain('identity-attribute-problem')
  })

  it('sends the add (only when the form is complete) and the remove it is asked for', () => {
    const submit = (over: Partial<AttributeMappingsViewProps>) => (find(tree(over), 'identity-attribute-form')!.props.onSubmit as (e: { preventDefault: () => void }) => void)({ preventDefault: () => {} })
    const onAdd = vi.fn()
    submit({ onAdd })
    expect(onAdd).toHaveBeenCalledTimes(1)
    submit({ onAdd, form: { source: 'entra', group: '', attribute: 'region', value: 'west' } })
    expect(onAdd).toHaveBeenCalledTimes(1)
    const onRemove = vi.fn()
    ;(find(tree({ onRemove }), 'identity-attribute-remove')!.props.onClick as () => void)()
    expect(onRemove).toHaveBeenCalledWith('a')
  })

  it('turns the buttons off while a change is on its way and shows the server\'s refusal in its words', () => {
    expect(find(tree({ busy: true }), 'identity-attribute-add')!.props.disabled).toBe(true)
    expect(find(tree({ busy: true }), 'identity-attribute-remove')!.props.disabled).toBe(true)
    expect(html({ refusal: 'Administering the identity home needs the admin role.' })).toContain('needs the admin role')
  })
})
