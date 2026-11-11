import { describe, expect, it } from 'vitest'
import { refreshLine, subjectSource } from './knowledge'

describe('subjects read from the lineage graph (plan 27a KN02)', () => {
  it('says what a complete reading did', () => {
    expect(refreshLine({ found: 12, nowGone: 0, complete: true, problems: [] })).toBe('The graph shows 12 subjects; 0 objects are now gone.')
    expect(refreshLine({ found: 1, nowGone: 1, complete: true, problems: [] })).toBe('The graph shows 1 subject; 1 object is now gone.')
  })
  it('says why nothing was marked gone when a server did not answer or none had a lineage', () => {
    expect(refreshLine({ found: 3, nowGone: 0, complete: false, problems: ['Main Build: It did not answer.', 'Runner: 502'] })).toBe('The graph shows 3 subjects. Nothing was marked gone: Main Build: It did not answer. (and 1 more).')
    expect(refreshLine({ found: 0, nowGone: 0, complete: false, problems: [] })).toContain('no server has a lineage to answer with')
  })
  it("tells the graph's own subjects from an administrator's word", () => {
    expect(subjectSource({ type: 'app', name: 'a', narrowingKind: '', narrowingName: '', status: 'exists', source: 'lineage' })).toBe('lineage graph')
    expect(subjectSource({ type: 'app', name: 'a', narrowingKind: '', narrowingName: '', status: 'exists' })).toBe('said by an administrator')
  })
})
