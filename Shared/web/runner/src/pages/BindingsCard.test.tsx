import { isValidElement, type ReactElement, type ReactNode } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { ApiError } from '@dbdatakit/shell'
import { slotRows } from '../bindings'
import { BindingsCardView, type BindingsCardViewProps } from './BindingsCard'
import { PublishingSettingsView, type PublishingSettingsViewProps } from './PublishingSettings'

const rows = slotRows({
  slots: [{ channel: 'prod', slot: 'source', kind: 'sqlserver', tasks: ['load'], versions: [1] }, { channel: 'prod', slot: 'target', kind: 'postgres', tasks: ['load'], versions: [2] }],
  bindings: [{ channel: 'prod', slot: 'source', bound_to: 'erp', revision: 2 }],
}, 'prod')

function props(over: Partial<BindingsCardViewProps> = {}): BindingsCardViewProps {
  return { channel: 'prod', rows, canEdit: true, edits: {}, known: ['erp', 'warehouse'], busy: false, saved: null, failure: null, onEdit: () => {}, onSave: () => {}, ...over }
}
const html = (over?: Partial<BindingsCardViewProps>) => renderToStaticMarkup(BindingsCardView(props(over)))

function find(node: ReactNode, testId: string): ReactElement<Record<string, unknown>> | undefined {
  if (Array.isArray(node)) { for (const child of node) { const found = find(child, testId); if (found) return found } return undefined }
  if (!isValidElement(node)) return undefined
  const element = node as ReactElement<Record<string, unknown>>
  if (element.props['data-testid'] === testId) return element
  if (typeof element.type === 'function') return find((element.type as (p: unknown) => ReactNode)(element.props), testId)
  return find(element.props.children as ReactNode, testId)
}

describe('the connections of a channel', () => {
  it('lists each slot with its kind, its tasks and what it is bound to, and names the unbound one', () => {
    const text = html()
    expect(text.match(/runner-binding-row/g)).toHaveLength(2)
    expect(text).toContain('data-slot="source" data-bound="erp"')
    expect(text).toContain('sqlserver')
    expect(text).toContain('Not bound yet: target')
    expect(text).toContain('value="warehouse"')   // the known connections are offered
  })
  it('says a channel with no slot declares none', () => {
    const text = html({ rows: [] })
    expect(text).toContain('runner-bindings-empty')
    expect(text).not.toContain('runner-bindings-save')
  })
  it('shows a viewer the bindings without inputs and says why Save is off', () => {
    const text = html({ canEdit: false })
    expect(text).not.toContain('runner-binding-input')
    expect(text).toContain('runner-binding-value')
    expect(text).toContain('needs the admin role')
    expect(find(BindingsCardView(props({ canEdit: false })), 'runner-bindings-save')!.props.disabled).toBe(true)
  })
  it('disables Save until something changed and says so, then enables it', () => {
    expect(find(BindingsCardView(props()), 'runner-bindings-save')!.props.disabled).toBe(true)
    expect(html()).toContain('Change a connection first.')
    expect(find(BindingsCardView(props({ edits: { target: 'warehouse' } })), 'runner-bindings-save')!.props.disabled).toBe(false)
  })
  it('says why a typed value cannot be saved and disables Save while saving', () => {
    expect(html({ edits: { target: 'a b' } })).toContain('without spaces')
    expect(find(BindingsCardView(props({ edits: { target: 'w' }, busy: true })), 'runner-bindings-save')!.props.disabled).toBe(true)
  })
  it('hands the typed slot to the editor and presses Save', () => {
    const typed: string[] = []
    let saved = 0
    const view = BindingsCardView(props({ onEdit: (s, v) => typed.push(`${s}=${v}`), onSave: () => { saved++ }, edits: { target: 'w' } }))
    ;(find(view, 'runner-binding-input')!.props.onChange as (e: { target: { value: string } }) => void)({ target: { value: 'erp2' } })
    ;(find(view, 'runner-bindings-save')!.props.onClick as () => void)()
    expect(typed).toEqual(['source=erp2']); expect(saved).toBe(1)
  })
  it('offers to try a bound slot (not an unbound one, not a viewer) and shows how far the runner got, in its words (UI08)', () => {
    const tried: string[] = []
    const answer = { slot: 'source', bound_to: 'erp', ok: false, step: 'login' as const, message: "The connection 'erp' bound to the slot 'source' needs env:ERP_PASSWORD, which nothing answers.", connected: false }
    const view = BindingsCardView(props({ onTry: (s) => tried.push(s), tested: { source: answer } }))
    const text = renderToStaticMarkup(view)
    expect(text.match(/runner-binding-try/g)).toHaveLength(1)   // source is bound, target is not
    expect(text).toContain('data-step="login"')
    expect(text).toContain('needs env:ERP_PASSWORD')
    ;(find(view, 'runner-binding-try')!.props.onClick as () => void)()
    expect(tried).toEqual(['source'])
    expect(html({ onTry: () => {}, canEdit: false })).not.toContain('runner-binding-try')
    expect(html({ onTry: () => {}, edits: { source: 'other' } })).not.toContain('runner-binding-try')   // a typed change is tried after it is saved
    expect(find(BindingsCardView(props({ onTry: () => {}, trying: 'source' })), 'runner-binding-try')!.props.disabled).toBe(true)
  })
  it('shows what was saved and the runner\'s refusal', () => {
    expect(html({ saved: ['target'] })).toContain('Bound in prod: target.')
    expect(html({ failure: new ApiError(403, 'This needs the publisher role, and for an existing package it must be one this client created, or the admin role.', null) })).toContain('must be one this client created')
  })
})

describe('the require-signatures setting', () => {
  const settings = (over: Partial<PublishingSettingsViewProps> = {}): PublishingSettingsViewProps => ({ requireSignatures: false, loading: false, error: null, canEdit: true, onToggle: () => {}, ...over })
  it('shows the value, says what it does, and offers the switch to an administrator', () => {
    const text = renderToStaticMarkup(PublishingSettingsView(settings()))
    expect(text).toContain('data-value="false"')
    expect(text).toContain('Turn on')
    expect(renderToStaticMarkup(PublishingSettingsView(settings({ requireSignatures: true })))).toContain('every new publish is refused')
    expect(find(PublishingSettingsView(settings()), 'runner-require-signatures-toggle')!.props.disabled).toBe(false)
  })
  it('disables the switch for anyone else and says why; shows loading and the runner\'s refusal', () => {
    const view = PublishingSettingsView(settings({ canEdit: false }))
    expect(find(view, 'runner-require-signatures-toggle')!.props.disabled).toBe(true)
    expect(renderToStaticMarkup(view)).toContain('needs the admin role')
    expect(renderToStaticMarkup(PublishingSettingsView(settings({ requireSignatures: null, loading: true })))).toContain('runner-settings-loading')
    expect(renderToStaticMarkup(PublishingSettingsView(settings({ requireSignatures: null, error: new ApiError(503, 'The runner did not answer', null) })))).toContain('The runner did not answer')
  })
})
