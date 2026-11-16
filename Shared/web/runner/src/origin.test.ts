import { describe, expect, it } from 'vitest'
import { noteText, workspaceOriginOf, workspaceRunsLink } from './origin'

describe('the workspace a version came from (plan 33 SC20o, SC22l)', () => {
  it('reads the token a Workspace puts in the note, and links to the workspace\'s Runs tab', () => {
    const note = 'From workspace d1 of project orders-set, build 0197, published by grace. [workspace:ws-1/orders-set/d1]'
    const origin = workspaceOriginOf(note)
    expect(origin).toEqual({ instance: 'ws-1', project: 'orders-set', workspace: 'd1' })
    expect(workspaceRunsLink(origin!)).toBe('/servers/ws-1/orders-set/d1?tab=runs')
    expect(noteText(note)).toBe('From workspace d1 of project orders-set, build 0197, published by grace.')
  })
  it('decodes escaped parts, and a note without the token names no origin', () => {
    expect(workspaceOriginOf('[workspace:ws%201/a%2Fb/d]')).toEqual({ instance: 'ws 1', project: 'a/b', workspace: 'd' })
    expect(workspaceOriginOf('dbdatarunner package install (development)')).toBeNull()
    expect(workspaceOriginOf(null)).toBeNull()
    expect(workspaceOriginOf('[workspace:%E0%A4%A/p/w]')).toBeNull()
  })
})
