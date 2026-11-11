import { isValidElement, type ReactElement, type ReactNode } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { ApiError } from '@dbdatakit/shell'
import type { CheckpointChange, TaskCheckpoint, TaskStatus } from '../api'
import { CheckpointsView, type CheckpointsViewProps } from './CheckpointsCard'

const task: TaskStatus = { package: 'sales', channel: 'prod', task: 'orders', version: 3, next_due: null, last_run: null, running: 0, queued: 0, next_attempt_at: null, paused: false, paused_by: null, problem: null, required_labels: [], waiting_reason: null, owner: 'runner' }
const checkpoints: TaskCheckpoint[] = [{ name: 'orders', items: [{ key: '', version: 3, updated: '2026-11-11T10:00:00Z', updated_by: 'run', parts: { at: { type: 'int', value: '30' } } }, { key: 'eu', version: 1, updated: '2026-11-11T09:00:00Z', updated_by: null, parts: { at: { type: 'int', value: '5' } } }] }]
const changes: CheckpointChange[] = [
  { id: 3, part: 'at', type: 'int', old: '20', new: '30', version: 3, run_id: null, actor: 'run', at: '2026-11-11T10:00:00Z' },
  { id: 2, part: 'at', type: 'int', old: '10', new: '20', version: 2, run_id: null, actor: 'run', at: '2026-11-11T09:30:00Z' },
]

function props(over: Partial<CheckpointsViewProps> = {}): CheckpointsViewProps {
  return { task, canAct: true, checkpoints, loading: false, error: null, open: null, history: undefined, historyLoading: false, busy: false, rewound: null, failure: null, onOpen: () => {}, onRewind: () => {}, ...over }
}
const html = (over?: Partial<CheckpointsViewProps>) => renderToStaticMarkup(CheckpointsView(props(over)))

function findAll(node: ReactNode, testId: string, found: ReactElement<Record<string, unknown>>[] = []): ReactElement<Record<string, unknown>>[] {
  if (Array.isArray(node)) { for (const child of node) findAll(child, testId, found); return found }
  if (!isValidElement(node)) return found
  const element = node as ReactElement<Record<string, unknown>>
  if (element.props['data-testid'] === testId) found.push(element)
  findAll(element.props.children as ReactNode, testId, found)
  return found
}

describe('the checkpoints card', () => {
  it('shows every checkpoint item with its value, version and who wrote it', () => {
    const text = html()
    expect(text).toContain('at=30')
    expect(text).toContain('the whole task')
    expect(text).toContain('eu')
    expect(text).toContain('version 3')
    expect((text.match(/data-testid="runner-checkpoint"/g) ?? []).length).toBe(2)
  })
  it('says a task with no checkpoint has none yet, and shows loading and a failed read', () => {
    expect(html({ checkpoints: [] })).toContain('runner-checkpoints-none')
    expect(html({ checkpoints: undefined, loading: true })).toContain('runner-checkpoints-loading')
    expect(html({ checkpoints: undefined, error: new ApiError(403, 'This needs the operator role.', null) })).toContain('This needs the operator role.')
  })
  it('opens the history of the item that was pressed and closes it again', () => {
    const opened: unknown[] = []
    const view = CheckpointsView(props({ onOpen: (o) => opened.push(o) }))
    ;(findAll(view, 'runner-checkpoint-history')[1].props.onClick as () => void)()
    expect(opened).toEqual([{ name: 'orders', key: 'eu' }])
    const closed: unknown[] = []
    const open = CheckpointsView(props({ open: { name: 'orders', key: '' }, history: changes, onOpen: (o) => closed.push(o) }))
    ;(findAll(open, 'runner-checkpoint-history')[0].props.onClick as () => void)()
    expect(closed).toEqual([null])
  })
  it('lists the changes of the open item and offers a rewind before each version', () => {
    const text = html({ open: { name: 'orders', key: '' }, history: changes })
    expect(text).toContain('at 20 -&gt; 30 by run')
    expect(text).toContain('Go back before version 3: at=20')
    expect(text).toContain('Go back before version 2: at=10')
    expect(text).toContain('is audited')
  })
  it('sends the rewind it was pressed for, with the item it belongs to', () => {
    const sent: unknown[] = []
    const view = CheckpointsView(props({ open: { name: 'orders', key: '' }, history: changes, onRewind: (o, id) => sent.push([o, id]) }))
    ;(findAll(view, 'runner-checkpoint-rewind-to')[1].props.onClick as () => void)()
    expect(sent).toEqual([[{ name: 'orders', key: '' }, 2]])
  })
  it('offers no rewind to a person without the operator role, and says so', () => {
    const view = CheckpointsView(props({ canAct: false, open: { name: 'orders', key: '' }, history: changes }))
    expect(findAll(view, 'runner-checkpoint-rewind-to').every((b) => b.props.disabled === true)).toBe(true)
    expect(renderToStaticMarkup(view)).toContain('needs the operator role')
  })
  it('shows what a rewind did, and a refusal in the banner', () => {
    expect(html({ rewound: 'orders (the whole task) is back, as version 4.' })).toContain('is back, as version 4')
    expect(html({ failure: new ApiError(409, 'The checkpoint moved while it was being rewound.', null) })).toContain('moved while it was being rewound')
  })
})
