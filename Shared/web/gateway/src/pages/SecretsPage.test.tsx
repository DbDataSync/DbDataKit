import { isValidElement, type ReactElement, type ReactNode } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it, vi } from 'vitest'
import { SecretsView, type SecretsViewProps } from './SecretsPage'

const have = [{ name: 'erp-login', reference: 'secret:dbdatakit:gateway:managed-console-1:erp-login' }]

function props(over: Partial<SecretsViewProps> = {}): SecretsViewProps {
  return { items: have, loading: false, loadError: null, form: { name: 'warehouse', value: 'hunter2' }, busy: false, written: null, refusal: null, confirming: null, onForm: () => {}, onSet: () => {}, onAskRemove: () => {}, onRemove: () => {}, onKeep: () => {}, ...over }
}

function find(node: ReactNode, testId: string): ReactElement<Record<string, unknown>> | undefined {
  if (Array.isArray(node)) { for (const child of node) { const found = find(child, testId); if (found) return found } return undefined }
  if (!isValidElement(node)) return undefined
  const element = node as ReactElement<Record<string, unknown>>
  if (element.props['data-testid'] === testId) return element
  return find(element.props.children as ReactNode, testId)
}

const tree = (over?: Partial<SecretsViewProps>) => SecretsView(props(over))
const html = (over?: Partial<SecretsViewProps>) => renderToStaticMarkup(tree(over))

describe('the secrets screen', () => {
  it('lists the names with the line to write in a data source, and shows no value', () => {
    const text = html({ form: { name: '', value: '' } })
    expect(text).toContain('data-testid="secret-erp-login"')
    expect(text).toContain("password_secret: &#x27;secret:dbdatakit:gateway:managed-console-1:erp-login&#x27;")
  })

  it('draws the value field as a password field that is never filled in by the list', () => {
    const value = find(tree({ form: { name: 'warehouse', value: '' } }), 'secret-value')!
    expect(value.props.type).toBe('password')
    expect(value.props.autoComplete).toBe('new-password')
    expect(html({ form: { name: '', value: '' } })).not.toContain('hunter2')
  })

  it('says it is reading, cannot read, and has none', () => {
    expect(html({ loading: true, items: undefined })).toContain('secrets-loading')
    expect(html({ loadError: 'This call needs the admin role.', items: undefined })).toContain('could not be listed: This call needs the admin role.')
    expect(html({ items: [], form: { name: '', value: '' } })).toContain('secrets-empty')
  })

  it('sets when there is a name and a value, and not before, saying what is missing', () => {
    expect(find(tree(), 'secret-set')!.props.disabled).toBe(false)
    const noValue = tree({ form: { name: 'warehouse', value: '' } })
    expect(find(noValue, 'secret-set')!.props.disabled).toBe(true)
    expect(find(noValue, 'secret-set')!.props.title).toBe('Type the value to keep.')
    expect(find(tree({ form: { name: '', value: '' } }), 'secret-why')!.props.children).toBe('Name the secret.')
    expect(find(tree({ form: { name: '9lives', value: 'x' } }), 'secret-set')!.props.disabled).toBe(true)
  })

  it('offers Replace, not Set, for a name that is kept, and still never shows the old value', () => {
    const replacing = tree({ form: { name: 'erp-login', value: 'new' } })
    expect(renderToStaticMarkup(replacing)).toContain('Replace a secret')
    expect(find(replacing, 'secret-value')!.props.placeholder).toBe('new value')
  })

  it('sends the set only from a complete form', () => {
    const submit = (over: Partial<SecretsViewProps>) => (find(tree(over), 'secret-form')!.props.onSubmit as (e: { preventDefault: () => void }) => void)({ preventDefault: () => {} })
    const onSet = vi.fn()
    submit({ onSet })
    expect(onSet).toHaveBeenCalledTimes(1)
    submit({ onSet, form: { name: 'warehouse', value: '' } })
    expect(onSet).toHaveBeenCalledTimes(1)
  })

  it('shows the reference to write after a set, and says when it replaced one, without the value', () => {
    const text = html({ form: { name: '', value: '' }, written: { name: 'warehouse', reference: 'secret:dbdatakit:gateway:managed-console-1:warehouse', replaced: false } })
    expect(text).toContain('data-testid="secret-written-reference"')
    expect(text).toContain('warehouse is kept.')
    expect(text).not.toContain('hunter2')
    expect(html({ written: { name: 'erp-login', reference: 'x', replaced: true } })).toContain('was replaced')
  })

  it('asks before removing, and says what removing breaks', () => {
    const asked = vi.fn()
    ;(find(tree({ onAskRemove: asked }), 'secret-remove-erp-login')!.props.onClick as () => void)()
    expect(asked).toHaveBeenCalledWith('erp-login')
    const confirming = html({ confirming: 'erp-login' })
    expect(confirming).toContain('secret-remove-confirm')
    expect(confirming).toContain('cannot connect once it is removed')
    const removed = vi.fn()
    ;(find(tree({ confirming: 'erp-login', onRemove: removed }), 'secret-remove-confirm')!.props.onClick as () => void)()
    expect(removed).toHaveBeenCalledWith('erp-login')
  })

  it('shows the Gateway\'s refusal in its words and turns the buttons off while a call is on its way', () => {
    expect(html({ refusal: 'A secret name is a letter followed by letters.' })).toContain('role="alert"')
    expect(find(tree({ busy: true }), 'secret-set')!.props.disabled).toBe(true)
    expect(find(tree({ busy: true }), 'secret-remove-erp-login')!.props.disabled).toBe(true)
  })
})
