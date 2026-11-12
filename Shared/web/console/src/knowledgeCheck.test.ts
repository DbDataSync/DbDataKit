import { describe, expect, it } from 'vitest'
import { checkLine, placeOf, reachLine, type CheckProject } from './knowledge'

// Plan 27a KN10 and KN17: what the Knowledge page's Check tab says about a project before it is published. The server decides (the strict reader, the catalog, the budgets); these are the words.

const diagnostic = (over: Partial<CheckProject['diagnostics'][number]> = {}) => ({ code: 'DBD-KNW-040', severity: 'error', file: 'big.md', line: 7, column: 1, message: 'The agent would be over its budget.', fix: null, ...over })
const project = (over: Partial<CheckProject> = {}): CheckProject => ({ name: 'project', ok: true, items: 3, diagnostics: [], reaches: [], ...over })

describe('the verdict of a check', () => {
  it('says how many items would be published and is silent about warnings when there are none', () => {
    expect(checkLine(project())).toBe('3 items would be published.')
    expect(checkLine(project({ items: 1 }))).toBe('1 item would be published.')
  })
  it('counts the warnings of a project that passes', () => {
    expect(checkLine(project({ diagnostics: [diagnostic({ severity: 'warning', code: 'DBD-KNW-005' })] }))).toBe('3 items would be published, with 1 warning.')
  })
  it('says a publish would be refused and how many errors and warnings there are', () => {
    expect(checkLine(project({ ok: false, diagnostics: [diagnostic(), diagnostic({ file: 'ghost.md' }), diagnostic({ severity: 'warning' })] }))).toBe('A publish would be refused: 2 errors and 1 warning.')
    expect(checkLine(project({ ok: false, diagnostics: [diagnostic()] }))).toBe('A publish would be refused: 1 error.')
  })
})

describe('where a diagnostic is', () => {
  it('names the file and the line, and only the file when the problem is the whole file or project', () => {
    expect(placeOf(diagnostic())).toBe('big.md line 7')
    expect(placeOf(diagnostic({ line: 0 }))).toBe('big.md')
  })
})

describe('what an agent would be given', () => {
  it('says what the project adds to what the agent has, against its budget, and names the items', () => {
    expect(reachLine({ agent: 'helper', adds: 120, used: 900, budget: 25600, pushesOver: false, items: ['revenue.md'] })).toBe('helper: adds 120 tokens to 900 of a budget of 25600 (revenue.md).')
  })
  it('says when it is over', () => {
    expect(reachLine({ agent: 'helper', adds: 30000, used: 900, budget: 25600, pushesOver: true, items: ['big.md', 'small.md'] })).toBe('helper: adds 30000 tokens to 900 of a budget of 25600, which is over (big.md, small.md).')
  })
})
