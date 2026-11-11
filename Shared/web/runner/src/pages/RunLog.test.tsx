import { isValidElement, type ReactElement, type ReactNode } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it, vi } from 'vitest'
import type { RunLogLine } from '../logs'
import { RunLogView, type RunLogViewProps } from './RunLog'

const line = (id: number, over: Partial<RunLogLine> = {}): RunLogLine => ({ id, at: '2026-11-11T10:20:30Z', level: 'info', item: null, message: `line ${id}`, ...over })
const props = (over: Partial<RunLogViewProps> = {}): RunLogViewProps => ({ lines: [line(1), line(2, { level: 'error', item: 'orders#1', message: 'the source refused' })], loading: false, error: null, live: false, more: false, onMore: () => {}, onCopy: () => {}, ...over })
const html = (over?: Partial<RunLogViewProps>) => renderToStaticMarkup(RunLogView(props(over)))

function find(node: ReactNode, testId: string): ReactElement<Record<string, unknown>> | undefined {
  if (Array.isArray(node)) { for (const child of node) { const found = find(child, testId); if (found) return found } return undefined }
  if (!isValidElement(node)) return undefined
  const element = node as ReactElement<Record<string, unknown>>
  if (element.props['data-testid'] === testId) return element
  if (typeof element.type === 'function') return find((element.type as (p: unknown) => ReactNode)(element.props), testId)
  return find(element.props.children as ReactNode, testId)
}

describe('the output of a run', () => {
  it('lists the lines in order, names the item a line is about and marks an error', () => {
    const text = html()
    expect(text.indexOf('line 1')).toBeLessThan(text.indexOf('the source refused'))
    expect(text).toContain('[orders#1] the source refused')
    expect(text).toContain('data-level="error"')
    expect(text).toContain('runner-run-log-copy')
  })

  it('says a run that wrote nothing wrote nothing, and a run that is going wrote nothing yet', () => {
    expect(html({ lines: [] })).toContain('The run wrote no output.')
    expect(html({ lines: [], live: true })).toContain('The run wrote no output yet.')
    expect(html({ lines: [], live: true })).not.toContain('runner-run-log-copy')
  })

  it('shows the reason when the output cannot be read, and offers more only when a page was full', () => {
    expect(html({ lines: [], error: 'The runner does not answer.' })).toContain('The runner does not answer.')
    expect(html()).not.toContain('runner-run-log-more')
    expect(html({ more: true })).toContain('runner-run-log-more')
  })

  it('asks for more and copies when the buttons are pressed', () => {
    const onMore = vi.fn(); const onCopy = vi.fn()
    const view = RunLogView(props({ more: true, onMore, onCopy }))
    ;(find(view, 'runner-run-log-more')!.props.onClick as () => void)()
    ;(find(view, 'runner-run-log-copy')!.props.onClick as () => void)()
    expect([onMore.mock.calls.length, onCopy.mock.calls.length]).toEqual([1, 1])
  })

  it('says it is read again while the run is going', () => {
    expect(html({ live: true })).toContain('running, read again every few seconds')
  })
})
