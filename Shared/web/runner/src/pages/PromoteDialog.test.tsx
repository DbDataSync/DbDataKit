import { isValidElement, type ReactElement, type ReactNode } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { emptyPromoteForm, type PromoteAnswer } from '../promote'
import { PromoteDialogView, type PromoteDialogViewProps } from './PromoteDialog'

function props(over: Partial<PromoteDialogViewProps> = {}): PromoteDialogViewProps {
  return {
    name: 'orders', servers: [{ id: 'runner-2', name: 'Prod runner', product: 'dbdatarunner', reachable: true }], channels: ['test', 'prod'], versions: [1, 2], form: { ...emptyPromoteForm, fromChannel: 'test', toChannel: 'test' },
    busy: false, failure: null, result: null, onForm: () => {}, onSend: () => {}, onCancel: () => {}, ...over,
  }
}
const html = (over?: Partial<PromoteDialogViewProps>) => renderToStaticMarkup(PromoteDialogView(props(over)))

function find(node: ReactNode, testId: string): ReactElement<Record<string, unknown>> | undefined {
  if (Array.isArray(node)) { for (const child of node) { const found = find(child, testId); if (found) return found } return undefined }
  if (!isValidElement(node)) return undefined
  const element = node as ReactElement<Record<string, unknown>>
  if (element.props['data-testid'] === testId) return element
  // the modal's shell uses hooks, so a component is not called: what it is given as children is searched
  return find(element.props.children as ReactNode, testId)
}
const prop = (over: Partial<PromoteDialogViewProps>, id: string, name: string) => find(PromoteDialogView(props(over)), id)!.props[name]

const answer: PromoteAnswer = { kind: 'integration', name: 'orders', fromChannel: 'test', toChannel: 'prod', sourceVersion: 2, targetVersion: 1, changed: true, mode: 'stage', signature: 'unsigned', sent: 2, skipped: 0, bound: [] }

describe('promoting a package to another runner', () => {
  it('offers the other runners, the channels and the versions', () => {
    const text = html()
    expect(text).toContain('Prod runner')
    expect(text).toContain('<option value="prod">prod</option>')
    expect(text).toContain('v2')
  })
  it('cannot be sent before a runner is chosen, and says why', () => {
    expect(prop({}, 'runner-promote-go', 'disabled')).toBe(true)
    expect(html()).toContain('Choose the runner it goes to.')
    expect(prop({ form: { ...emptyPromoteForm, to: 'runner-2', fromChannel: 'test', toChannel: 'prod' } }, 'runner-promote-go', 'disabled')).toBe(false)
  })
  it('names a connection line that is not slot=connection', () => {
    const text = html({ form: { ...emptyPromoteForm, to: 'runner-2', fromChannel: 'test', toChannel: 'prod', bindings: 'source' } })
    expect(text).toContain('&quot;source&quot; is not slot=connection.')
    expect(prop({ form: { ...emptyPromoteForm, to: 'runner-2', fromChannel: 'test', toChannel: 'prod', bindings: 'source' } }, 'runner-promote-go', 'disabled')).toBe(true)
  })
  it('says there is no other runner when the console manages none', () => {
    expect(html({ servers: [] })).toContain('The console manages no other runner to promote to.')
  })
  it('shows what the promotion did and offers Close', () => {
    const text = html({ result: answer, form: { ...emptyPromoteForm, to: 'runner-2', fromChannel: 'test', toChannel: 'prod' } })
    expect(text).toContain('Version 2 of orders: Prod runner now holds version 1 in prod (stage). 2 files sent, 0 already there.')
    expect(text).toContain('>Close<')
  })
  it('disables Promote while it goes', () => {
    expect(prop({ busy: true, form: { ...emptyPromoteForm, to: 'runner-2', fromChannel: 'test', toChannel: 'prod' } }, 'runner-promote-go', 'disabled')).toBe(true)
  })
})
