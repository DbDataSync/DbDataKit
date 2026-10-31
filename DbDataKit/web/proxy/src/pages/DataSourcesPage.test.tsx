import { isValidElement, type ReactElement, type ReactNode } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it, vi } from 'vitest'
import { ApiError } from '@dbdatakit/shell'
import { HandOverView, ownerLabel, type HandOverViewProps } from './DataSourcesPage'

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

  it('says what came of it: handed over, already theirs, or the proxy\'s own refusal', () => {
    expect(html({ done: { name: 'erp', managed_by: 'build-1', adopted: true } })).toContain('Handed to Build one.')
    expect(html({ done: { name: 'erp', managed_by: 'build-1', adopted: false } })).toContain('Already theirs.')
    expect(html({ failure: new ApiError(409, 'The data source carries a serving policy (extensions.proxy).') })).toContain('serving policy')
    expect(html({ failure: new TypeError('failed to fetch') })).toContain('did not answer')
  })

  it('names the owner of a data source: by hand, a paired client by name, an unknown one by id', () => {
    expect(ownerLabel(null, [build])).toBe('by hand')
    expect(ownerLabel('build-1', [build])).toBe('Build one')
    expect(ownerLabel('gone-7', [build])).toBe('gone-7')
  })
})
