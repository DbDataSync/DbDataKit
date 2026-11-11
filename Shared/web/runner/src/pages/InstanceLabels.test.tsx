import { isValidElement, type ReactElement, type ReactNode } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { ApiError } from '@dbdatakit/shell'
import type { RunnerInstance } from '../api'
import { InstanceLabelsView, type InstanceLabelsViewProps } from './InstanceLabels'

const instance: RunnerInstance = { id: 'i-1', host: 'h', version: '1', started: '', heartbeat: '', this: false, labels: [{ label: 'os:linux', source: 'reported' }, { label: 'gpu', source: 'admin' }], capabilities: {}, running: 0 }
function props(over: Partial<InstanceLabelsViewProps> = {}): InstanceLabelsViewProps {
  return { instance, canEdit: true, draft: ['gpu'], typed: '', busy: false, saved: null, failure: null, onTyped: () => {}, onAdd: () => {}, onRemove: () => {}, onSave: () => {}, onCancel: () => {}, ...over }
}
const html = (over?: Partial<InstanceLabelsViewProps>) => renderToStaticMarkup(InstanceLabelsView(props(over)))

function find(node: ReactNode, testId: string): ReactElement<Record<string, unknown>> | undefined {
  if (Array.isArray(node)) { for (const child of node) { const found = find(child, testId); if (found) return found } return undefined }
  if (!isValidElement(node)) return undefined
  const element = node as ReactElement<Record<string, unknown>>
  if (element.props['data-testid'] === testId) return element
  if (typeof element.type === 'function') return find((element.type as (p: unknown) => ReactNode)(element.props), testId)
  return find(element.props.children as ReactNode, testId)
}
const prop = (over: Partial<InstanceLabelsViewProps>, id: string, name: string) => find(InstanceLabelsView(props(over)), id)!.props[name]

describe('the labels of an instance', () => {
  it('shows the machine\'s labels without a remove and the administrator\'s with one', () => {
    const text = html()
    expect(text).toContain('data-testid="runner-label-reported"')
    expect(text).toContain('data-label="gpu"')
    expect(text.match(/runner-label-remove/g)).toHaveLength(1)
  })
  it('says when an administrator has set none', () => {
    expect(html({ draft: [] })).toContain('No label set by an administrator.')
  })
  it('gives a reader the labels without the box, the removes or a way to save, and says why', () => {
    const text = html({ canEdit: false })
    expect(text).not.toContain('runner-label-input')
    expect(text).not.toContain('runner-label-remove')
    expect(text).toContain('needs the admin role')
    expect(prop({ canEdit: false }, 'runner-labels-save', 'disabled')).toBe(true)
  })
  it('names a typed text that is not a label and keeps Add off', () => {
    expect(html({ typed: 'two words' })).toContain('is not a label')
    expect(prop({ typed: 'two words' }, 'runner-label-add', 'disabled')).toBe(true)
    expect(html({ typed: 'os:linux' })).toContain('reported by the runner itself')
    expect(prop({ typed: 'site:oslo' }, 'runner-label-add', 'disabled')).toBe(false)
  })
  it('says to press Add before Save when a label is typed, and that nothing changed when it is not', () => {
    expect(html({ typed: 'site:oslo' })).toContain('Add the label you typed')
    expect(prop({ typed: 'site:oslo' }, 'runner-labels-save', 'disabled')).toBe(true)
    expect(html()).toContain('Add or remove a label first.')
    expect(prop({ draft: ['gpu', 'site:oslo'] }, 'runner-labels-save', 'disabled')).toBe(false)
  })
  it('does not offer Save for a runner that is gone', () => {
    const gone = html({ instance: { ...instance, live: false }, draft: [] })
    expect(gone).toContain('This runner is gone')
    expect(gone).not.toContain('runner-label-input')
  })
  it('hands what was typed, pressed and removed to the screen', () => {
    const seen: string[] = []
    const view = InstanceLabelsView(props({ typed: 'site:oslo', onTyped: (t) => seen.push(`typed ${t}`), onAdd: () => seen.push('add'), onRemove: (l) => seen.push(`remove ${l}`), onSave: () => seen.push('save'), onCancel: () => seen.push('close'), draft: ['gpu', 'x'] }))
    ;(find(view, 'runner-label-input')!.props.onChange as (e: { target: { value: string } }) => void)({ target: { value: 'y' } })
    ;(find(view, 'runner-label-input')!.props.onKeyDown as (e: { key: string; preventDefault: () => void }) => void)({ key: 'Enter', preventDefault: () => seen.push('prevent') })
    ;(find(view, 'runner-label-add')!.props.onClick as () => void)()
    ;(find(view, 'runner-label-remove')!.props.onClick as () => void)()
    ;(find(view, 'runner-labels-close')!.props.onClick as () => void)()
    expect(seen).toEqual(['typed y', 'prevent', 'add', 'add', 'remove gpu', 'close'])
  })
  it('says what was saved and shows the runner\'s refusal', () => {
    expect(html({ saved: { added: ['site:oslo'], removed: ['x'] } })).toContain('Added site:oslo. Removed x.')
    expect(html({ saved: { added: [], removed: [] } })).toContain('Nothing changed.')
    expect(html({ failure: new ApiError(403, 'This needs the admin role.', null) })).toContain('This needs the admin role.')
  })
})
