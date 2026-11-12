import { describe, expect, it } from 'vitest'
import { ApiError } from '@dbdatakit/shell'
import { columns, dropRefusal, historyLine, importProblems, movesFrom, neighbourFor, pointsLine, summaryLine, type BacklogItem, type BacklogWorkflow } from './backlog'

const workflow = (role: BacklogWorkflow['role'] = 'member'): BacklogWorkflow => ({
  initial: 'backlog',
  role,
  states: [
    { id: 'backlog', title: 'Backlog', category: 'todo', requires: [] },
    { id: 'ready', title: 'Ready', category: 'todo', requires: [] },
    { id: 'in-progress', title: 'In progress', category: 'doing', requires: ['assignee'] },
    { id: 'done', title: 'Done', category: 'done', requires: ['resolution'] },
    { id: 'cancelled', title: 'Cancelled', category: 'done', requires: ['resolution'] },
  ],
  transitions: [
    { from: 'backlog', to: 'ready', role: 'member' },
    { from: 'ready', to: 'in-progress', role: 'member' },
    { from: 'in-progress', to: 'done', role: 'member' },
    { from: '*', to: 'cancelled', role: 'member' },
    { from: 'done', to: 'in-progress', role: 'lead' },
  ],
  itemTypes: ['epic', 'story', 'task', 'bug'], priorities: ['lowest', 'low', 'medium', 'high', 'highest'], linkKinds: [], objectKinds: [],
})

const item = (over: Partial<BacklogItem> = {}): BacklogItem => ({
  id: 'i', key: 'DATA-1', project: 'DATA', type: 'story', title: 'T', description: null, state: 'backlog', priority: 'medium', estimate: null, assignee: null, reporter: 'ada', epic: null, epicKey: null, sprint: null, labels: [],
  resolution: null, createdAt: 't', updatedAt: 't', closedAt: null, version: 1, sortKey: 1024, ...over,
})

describe('the moves the workflow allows', () => {
  it('are the ones the transitions list for the role, with the any-state moves in', () => {
    expect(movesFrom(workflow(), 'backlog')).toEqual(['ready', 'cancelled'])
    expect(movesFrom(workflow(), 'done')).toEqual(['cancelled'])
    expect(movesFrom(workflow('lead'), 'done')).toEqual(['in-progress', 'cancelled'])
    expect(movesFrom(workflow('viewer'), 'backlog')).toEqual([])
  })

  it('say why a card cannot be dropped in a column, in words', () => {
    expect(dropRefusal(workflow(), item(), 'ready')).toBeNull()
    expect(dropRefusal(workflow(), item(), 'backlog')).toBeNull()
    expect(dropRefusal(workflow(), item(), 'done')).toBe('The workflow has no move from Backlog to Done.')
    expect(dropRefusal(workflow(), item({ state: 'done' }), 'in-progress')).toBe('DATA-1 can only be moved from Done to In progress by a lead.')
    expect(dropRefusal(workflow('viewer'), item(), 'ready')).toBe('A viewer cannot move an item.')
  })
})

describe('the board and the backlog', () => {
  it('has a column per state with its items in backlog order', () => {
    const cols = columns(workflow(), [item({ id: 'b', state: 'ready', sortKey: 2048 }), item({ id: 'a', state: 'ready', sortKey: 1024 }), item({ id: 'c', state: 'done' })])
    expect(cols.map((c) => c.state.id)).toEqual(['backlog', 'ready', 'in-progress', 'done', 'cancelled'])
    expect(cols[1].items.map((i) => i.id)).toEqual(['a', 'b'])
    expect(cols[3].items.map((i) => i.id)).toEqual(['c'])
  })

  it('counts points and the items that have no estimate', () => {
    expect(pointsLine([])).toBe('0 items, 0 points')
    expect(pointsLine([item({ estimate: 3 }), item({ estimate: 1.5 }), item()])).toBe('3 items, 4.5 points, 1 not estimated')
    expect(pointsLine([item({ estimate: 1 })])).toBe('1 item, 1 point')
  })

  it('finds the item a card goes before when it moves up or down one place, and nothing at the ends', () => {
    const list = [{ id: 'a' }, { id: 'b' }, { id: 'c' }]
    expect(neighbourFor(list, 'b', 'up')).toBe('a')
    expect(neighbourFor(list, 'a', 'up')).toBeUndefined()
    expect(neighbourFor(list, 'a', 'down')).toBe('c')      // before c is after b
    expect(neighbourFor(list, 'b', 'down')).toBeNull()      // last
    expect(neighbourFor(list, 'c', 'down')).toBeUndefined()
    expect(neighbourFor(list, 'x', 'down')).toBeUndefined()
  })
})

describe('the history and the sprint summary in words', () => {
  const titles = (s: string) => ({ ready: 'Ready', backlog: 'Backlog' })[s] ?? s
  it('say who did what to whom', () => {
    expect(historyLine({ actorName: 'Ada', kind: 'created', field: null, oldValue: null, newValue: 'DATA-1' })).toBe('Ada made DATA-1')
    expect(historyLine({ actorName: 'Bob', kind: 'transition', field: 'state', oldValue: 'backlog', newValue: 'ready' }, titles)).toBe('Bob moved it from Backlog to Ready')
    expect(historyLine({ actorName: 'Bob', kind: 'field', field: 'estimate', oldValue: null, newValue: '3' })).toBe('Bob changed the estimate from nothing to 3')
    expect(historyLine({ actorName: 'Bob', kind: 'link', field: 'object', oldValue: null, newValue: 'build-model:orders' })).toBe('Bob linked build-model:orders')
    expect(historyLine({ actorName: 'Bob', kind: 'sprint', field: null, oldValue: 'Sprint 1', newValue: null })).toBe('Bob took it out of Sprint 1')
    expect(historyLine({ actorName: 'Bob', kind: 'field', field: 'title', oldValue: 'x'.repeat(100), newValue: 'y' })).toContain('...')
  })

  it('says what closing a sprint did', () => {
    expect(summaryLine({ done: 1, moved: 0, pointsDone: 3, pointsMoved: 0, movedTo: null }, null)).toBe('1 finished item (3 points) stay in the sprint; nothing was left open.')
    expect(summaryLine({ done: 2, moved: 1, pointsDone: 5, pointsMoved: 2, movedTo: 'x' }, 'Sprint 2')).toBe('2 finished items (5 points) stay in the sprint; 1 open item (2 points) went to Sprint 2.')
    expect(summaryLine({ done: 0, moved: 2, pointsDone: 0, pointsMoved: 0, movedTo: null }, null)).toContain('back to the backlog')
  })
})

describe('an import that the reader refused', () => {
  it('lists each line with its message and the way to fix it', () => {
    const error = new ApiError(422, 'The file is not a valid work-item/1 file.', { diagnostics: [{ code: 'DBD-BKL-011', line: 5, column: 3, message: '`type` is `storey`.', fix: 'Did you mean story' }, { code: 'x', line: 6, column: 1, message: 'Second.', fix: null }] })
    expect(importProblems(error)).toEqual(['line 5: `type` is `storey`. (Did you mean story)', 'line 6: Second.'])
    expect(importProblems(new Error('Nope'))).toEqual(['Nope'])
  })
})
