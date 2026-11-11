import { isValidElement, type ReactElement, type ReactNode } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { ApiError } from '@dbdatakit/shell'
import type { TaskSchedule, TaskStatus } from '../api'
import type { OwnerForm } from '../ownership'
import { TaskPanelView, type TaskPanelViewProps } from './TaskPanel'

const task: TaskStatus = { package: 'sales', channel: 'prod', task: 'refresh-sqlserver-hourly', version: 3, next_due: null, last_run: null, running: 0, queued: 0, next_attempt_at: null, paused: false, paused_by: null, problem: null, required_labels: [], waiting_reason: null, owner: 'old' }
const schedule = (over: Partial<TaskSchedule> = {}): TaskSchedule => ({ task: 'sales/prod/refresh-sqlserver-hourly', owner: 'old', owner_set_by: 'build via Console', owner_set_at: '2026-11-08T10:00:00Z', reason: 'published by `project publish`', handled_up_to: null, paused: false, paused_by: null, paused_at: null, running: 0, queued: 0, version: 3, ...over })
const form = (over: Partial<OwnerForm> = {}): OwnerForm => ({ owner: 'observe', reason: '', handledUpTo: '', ...over })

function props(over: Partial<TaskPanelViewProps> = {}): TaskPanelViewProps {
  return { task, canAct: true, schedule: schedule(), scheduleLoading: false, decisions: undefined, scheduleError: null, form: form(), inputsText: '', busy: false, moved: null, failure: null, onForm: () => {}, onInputs: () => {}, onMove: () => {}, onRun: () => {}, ...over }
}
const html = (over?: Partial<TaskPanelViewProps>) => renderToStaticMarkup(TaskPanelView(props(over)))

function find(node: ReactNode, testId: string): ReactElement<Record<string, unknown>> | undefined {
  if (Array.isArray(node)) { for (const child of node) { const found = find(child, testId); if (found) return found } return undefined }
  if (!isValidElement(node)) return undefined
  const element = node as ReactElement<Record<string, unknown>>
  if (element.props['data-testid'] === testId) return element
  if (typeof element.type === 'function') return find((element.type as (p: unknown) => ReactNode)(element.props), testId)
  return find(element.props.children as ReactNode, testId)
}
const prop = (over: Partial<TaskPanelViewProps>, id: string, name: string) => find(TaskPanelView(props(over)), id)!.props[name]

describe('the task panel', () => {
  it('says who schedules the task, since when, set by whom and why', () => {
    const text = html()
    expect(text).toContain('data-owner="old"')
    expect(text).toContain('The publishing product')
    expect(text).toContain('set by build via Console')
    expect(text).toContain('published by `project publish`')
    expect(text).toContain('No position yet')
  })
  it('says a task never moved is on the default, and shows loading while the schedule is on its way', () => {
    expect(html({ schedule: schedule({ owner_set_by: null, owner: 'runner' }) })).toContain('never moved')
    const loading = html({ schedule: undefined, scheduleLoading: true })
    expect(loading).toContain('runner-task-panel-loading')
    expect(loading).not.toContain('runner-task-schedule')
  })
  it('shows a schedule that could not be read in the banner', () => {
    expect(html({ schedule: undefined, scheduleError: new ApiError(404, 'The runner has no such task', null) })).toContain('The runner has no such task')
  })
  it('disables Move and says why until a reason is given, then enables it', () => {
    expect(prop({}, 'runner-task-owner-move', 'disabled')).toBe(true)
    expect(html()).toContain('Say why')
    expect(prop({ form: form({ reason: 'trial' }) }, 'runner-task-owner-move', 'disabled')).toBe(false)
  })
  it('asks for the position only when handing the task to the runner, and says why Move is off without it', () => {
    const toRunner = { task: { ...task, owner: 'observe' }, schedule: schedule({ owner: 'observe' }), form: form({ owner: 'runner', reason: 'go' }) }
    expect(html(toRunner)).toContain('runner-task-owner-position')
    expect(html(toRunner)).toContain('Give the time')
    expect(prop(toRunner, 'runner-task-owner-move', 'disabled')).toBe(true)
    expect(html()).not.toContain('runner-task-owner-position')
  })
  it('sends the move it was pressed for', () => {
    const sent: unknown[] = []
    const onClick = prop({ form: form({ reason: ' trial ' }), onMove: (b) => sent.push(b) }, 'runner-task-owner-move', 'onClick') as () => void
    onClick()
    expect(sent).toEqual([{ owner: 'observe', reason: 'trial' }])
  })
  it('offers nothing to a person without the operator role, and says so', () => {
    const view = props({ canAct: false, form: form({ reason: 'trial' }) })
    expect(find(TaskPanelView(view), 'runner-task-owner-move')!.props.disabled).toBe(true)
    expect(find(TaskPanelView(view), 'runner-task-run-with-inputs')!.props.disabled).toBe(true)
    expect(renderToStaticMarkup(TaskPanelView(view))).toContain('needs the operator role')
  })
  it('shows what an observed task would have done and whether the product agrees', () => {
    const observed = { task: { ...task, owner: 'observe' }, schedule: schedule({ owner: 'observe' }) }
    expect(html({ ...observed, decisions: [] })).toContain('runner-task-decisions-none')
    const text = html({ ...observed, decisions: [{ source: 'runner', at: '2026-11-08T10:00:00Z', missed: 0, paused: false }, { source: 'old', at: '2026-11-08T10:00:30Z', missed: 0, paused: false }] })
    expect(text).toContain('would have queued 1 time(s), the publishing product queued 1; they agree.')
    expect(html({ ...observed, decisions: [{ source: 'runner', at: '2026-11-08T10:00:00Z', missed: 0, paused: false }] })).toContain('1 do not match')
    expect(html()).not.toContain('runner-task-decisions')    // not for a task that is not observed
  })
  it('runs with the inputs it was given, and names a line that is not one before anything is sent', () => {
    const sent: unknown[] = []
    ;(prop({ inputsText: 'since=2026-01-01\nlimit=5', onRun: (i) => sent.push(i) }, 'runner-task-run-with-inputs', 'onClick') as () => void)()
    expect(sent).toEqual([{ since: '2026-01-01', limit: '5' }])
    expect(prop({ inputsText: 'oops' }, 'runner-task-run-with-inputs', 'disabled')).toBe(true)
    expect(html({ inputsText: 'oops' })).toContain('&quot;oops&quot; is not name=value.')
  })
  it('shows a refusal in the server\'s words and the last move that went through', () => {
    expect(html({ failure: new ApiError(403, 'This needs the operator role.', null) })).toContain('This needs the operator role.')
    expect(html({ moved: 'Now scheduled by The publishing product, observed here.' })).toContain('runner-task-owner-moved')
  })
})
