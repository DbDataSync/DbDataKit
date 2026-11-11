import { isValidElement, type ReactElement, type ReactNode } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it, vi } from 'vitest'
import { ApiError } from '@dbdatakit/shell'
import { BLOCKED_NOTE, GRANTED_BY_DEFINITION, GRANTED_BY_FILE, GrantsView, HandOverView, ownerLabel, POLICY_BY_DEFINITION, POLICY_BY_FILE, PolicyView, type HandOverViewProps } from './DataSourcesPage'

const build = { id: 'build-1', name: 'Build one' }

function props(over: Partial<HandOverViewProps> = {}): HandOverViewProps {
  return { clients: [build], managedBy: null, chosen: '', busy: false, done: null, failure: null, onChoose: () => {}, onAdopt: () => {}, ...over }
}

const html = (over?: Partial<HandOverViewProps>) => renderToStaticMarkup(HandOverView(props(over)))

function find(node: ReactNode, testId: string): ReactElement<Record<string, unknown>> | undefined {
  if (Array.isArray(node)) { for (const child of node) { const found = find(child, testId); if (found) return found } return undefined }
  if (!isValidElement(node)) return undefined
  const element = node as ReactElement<Record<string, unknown>>
  if (element.props['data-testid'] === testId) return element
  return find(element.props.children as ReactNode, testId)
}

describe('handing a data source to a paired server', () => {
  it('lists the paired server clients by name and waits for a choice before the button can be pressed', () => {
    expect(html()).toContain('>Build one</option>')
    const disabled = (over: Partial<HandOverViewProps>) => find(HandOverView(props(over)), 'hand-over-button')!.props.disabled
    expect(disabled({})).toBe(true)
    expect(disabled({ chosen: 'build-1' })).toBe(false)
    expect(disabled({ chosen: 'build-1', busy: true })).toBe(true)
  })

  it('hands over what was chosen when the button is pressed', () => {
    const onAdopt = vi.fn()
    ;(find(HandOverView(props({ chosen: 'build-1', onAdopt })), 'hand-over-button')!.props.onClick as () => void)()
    expect(onAdopt).toHaveBeenCalledTimes(1)
    const onChoose = vi.fn()
    ;(find(HandOverView(props({ onChoose })), 'hand-over-client')!.props.onChange as (e: { target: { value: string } }) => void)({ target: { value: 'build-1' } })
    expect(onChoose).toHaveBeenCalledWith('build-1')
  })

  it('offers nothing when no server is paired', () => {
    const text = html({ clients: [] })
    expect(text).toContain('data-testid="hand-over-none"')
    expect(text).not.toContain('hand-over-button')
  })

  it('says what came of it: handed over, already theirs, or the gateway\'s own refusal', () => {
    expect(html({ done: { name: 'erp', managed_by: 'build-1', adopted: true } })).toContain('Handed to Build one.')
    expect(html({ done: { name: 'erp', managed_by: 'build-1', adopted: false } })).toContain('Already theirs.')
    expect(html({ failure: new ApiError(409, 'The data source carries a serving policy (extensions.gateway).') })).toContain('serving policy')
    expect(html({ failure: new TypeError('failed to fetch') })).toContain('did not answer')
  })

  it('names the owner of a data source: by hand, a paired client by name, an unknown one by id', () => {
    expect(ownerLabel(null, [build])).toBe('by hand')
    expect(ownerLabel('build-1', [build])).toBe('Build one')
    expect(ownerLabel('gone-7', [build])).toBe('gone-7')
  })
})

describe('who may use a data source', () => {
  it('shows a published definition’s grants read-only, with the sentence that says where to change them', () => {
    const view = GrantsView({ grants: { groups: ['Finance', 'auditors'], everyone: false, source: 'definition' } })
    const markup = renderToStaticMarkup(view)
    expect(GRANTED_BY_DEFINITION).toBe('Granted by the published definition; change them in the project and publish again')
    expect(markup).toContain(GRANTED_BY_DEFINITION)
    expect(markup).toContain('Finance')
    expect(markup).toContain('auditors')
    expect(markup).not.toMatch(/<(input|button|textarea|select)/)           // nothing here changes them
  })

  it('says everyone, or that no group is granted, and that a hand-made file is the file’s own', () => {
    expect(renderToStaticMarkup(GrantsView({ grants: { groups: [], everyone: true, source: 'definition' } }))).toContain('Everyone who signs in')
    expect(renderToStaticMarkup(GrantsView({ grants: { groups: [], everyone: false, source: 'definition' } }))).toContain('No group is granted')
    const byHand = renderToStaticMarkup(GrantsView({ grants: { groups: ['ops'], everyone: false, source: 'file' } }))
    expect(byHand).toContain('ops')
    expect(byHand).toContain('made by hand')
    expect(byHand).not.toContain(GRANTED_BY_DEFINITION)
    expect(GRANTED_BY_FILE).toContain('made by hand')
  })

  it('shows nothing for a file that did not load', () => {
    expect(renderToStaticMarkup(GrantsView({ grants: null }))).toBe('')
    expect(renderToStaticMarkup(GrantsView({ grants: undefined }))).toBe('')
  })
})

describe('how a data source is served', () => {
  it('shows a published definition’s policy read-only and says a publish replaces it whole', () => {
    const view = PolicyView({ policy: { origin: 'definition', fields: { batch_size: '1000', classifier: 'strict' }, blocked: {} } })
    const text = renderToStaticMarkup(view)
    expect(text).toContain('data-testid="data-source-policy-batch_size"')
    expect(text).toContain('1000')
    expect(text).toContain(POLICY_BY_DEFINITION)
    expect(find(view, 'data-source-policy-origin')?.props['data-origin']).toBe('definition')
    // read-only: no input, no button
    expect(text).not.toContain('<input')
    expect(text).not.toContain('<button')
  })

  it('says a hand-made file’s policy is the file’s, and says when the policy is empty', () => {
    expect(renderToStaticMarkup(PolicyView({ policy: { origin: 'file', fields: { batch_size: '64' }, blocked: {} } }))).toContain(POLICY_BY_FILE)
    expect(renderToStaticMarkup(PolicyView({ policy: { origin: 'definition', fields: {}, blocked: {} } }))).toContain('No serving policy is set')
  })

  it('shows allow_writes and the trusted directories as the file’s, with the sentence that a published definition never carries them', () => {
    const text = renderToStaticMarkup(PolicyView({ policy: { origin: 'file', fields: {}, blocked: { allow_writes: 'true', allowed_directories: '[/data]' } } }))
    expect(text).toContain('data-testid="data-source-policy-blocked-allow_writes"')
    expect(text).toContain('[/data]')
    expect(text).toContain('in the file')
    expect(text).toContain(BLOCKED_NOTE.replace('<name>', '&lt;name&gt;'))
    expect(renderToStaticMarkup(PolicyView({ policy: { origin: 'file', fields: {}, blocked: {} } }))).toContain('data-testid="data-source-policy-blocked-none"')
  })

  it('shows nothing for a file that did not load', () => {
    expect(renderToStaticMarkup(PolicyView({ policy: null }))).toBe('')
    expect(renderToStaticMarkup(PolicyView({ policy: undefined }))).toBe('')
  })
})
