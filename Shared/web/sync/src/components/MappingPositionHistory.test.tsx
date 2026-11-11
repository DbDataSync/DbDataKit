import { isValidElement, type ReactElement, type ReactNode } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import type { PositionChange } from '../api/types'
import { PositionHistoryView, type PositionHistoryViewProps } from './MappingPositionHistory'

const changes: PositionChange[] = [
  { id: 3, version: 3, part: 'position', old: '200', new: '300', actor: 'dbdatasync', runId: null, at: '2026-11-11T10:00:00Z' },
  { id: 2, version: 2, part: 'position', old: '100', new: '200', actor: 'ada', runId: null, at: '2026-11-11T09:00:00Z' },
]

function props(over: Partial<PositionHistoryViewProps> = {}): PositionHistoryViewProps {
  return { changes, loading: false, error: null, canRewind: true, busy: false, confirming: null, rewoundTo: null, onConfirm: () => {}, onRewind: () => {}, ...over }
}
const html = (over?: Partial<PositionHistoryViewProps>) => renderToStaticMarkup(PositionHistoryView(props(over)))

function findAll(node: ReactNode, testId: string, found: ReactElement<Record<string, unknown>>[] = []): ReactElement<Record<string, unknown>>[] {
  if (Array.isArray(node)) { for (const child of node) findAll(child, testId, found); return found }
  if (!isValidElement(node)) return found
  const element = node as ReactElement<Record<string, unknown>>
  if (element.props['data-testid'] === testId) found.push(element)
  findAll(element.props.children as ReactNode, testId, found)
  return found
}

describe('the position history of a mapping', () => {
  it('lists each change with its old and new value and who made it', () => {
    const text = html()
    expect(text).toContain('position: 200 → 300')
    expect(text).toContain('ada')
    expect((text.match(/data-testid="position-change"/g) ?? []).length).toBe(2)
  })

  it('offers a rewind to an administrator and says what it needs to anyone else', () => {
    expect((html().match(/data-testid="position-rewind"/g) ?? []).length).toBe(2)
    const viewer = html({ canRewind: false })
    expect(viewer).not.toContain('position-rewind')
    expect(viewer).toContain('needs the admin role')
  })

  it('asks for a confirmation of the one change and rewinds only on the second click', () => {
    const asking = PositionHistoryView(props({ confirming: 2 }))
    expect(findAll(asking, 'position-rewind-confirm')).toHaveLength(1)
    expect(findAll(asking, 'position-rewind')).toHaveLength(1)   // the other row still offers the first click
    let rewound: number | null = null
    const confirm = findAll(PositionHistoryView(props({ confirming: 2, onRewind: (id) => { rewound = id } })), 'position-rewind-confirm')[0]
    ;(confirm.props.onClick as () => void)()
    expect(rewound).toBe(2)
  })

  it('shows loading, an empty history, a refusal and the position after a rewind', () => {
    expect(html({ changes: undefined, loading: true })).toContain('position-history-loading')
    expect(html({ changes: [] })).toContain('position-history-none')
    expect(html({ error: new Error('scheduled by the runner') })).toContain('scheduled by the runner')
    expect(html({ rewoundTo: '200' })).toContain('position-history-rewound')
  })
})
