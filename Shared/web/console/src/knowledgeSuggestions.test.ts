import { describe, expect, it } from 'vitest'
import { canWithdraw, previewLine, suggestionLine, type SuggestionPreview, type SuggestionView } from './knowledge'

// Plan 27a KN19 and KN23: what the Knowledge page's Suggestions tab says. The server decides who sees what and what is refused; these are the words and the one rule of the screen (a person withdraws their own, undecided).

const suggestion = (over: Partial<SuggestionView> = {}): SuggestionView => ({
  id: 'abc123', project: 'project', item: 'revenue.md', kind: 'change', title: 'Revenue', state: 'open', reason: 'returns too', suggestedBy: 'maria', via: 'person', owner: 'finance', baseVersion: 'h', moved: false,
  createdAt: '2026-11-12T09:00:00Z', decidedBy: null, note: null, proposed: null, preview: null, ...over,
})

describe('a suggestion in words', () => {
  it('says what it is, who made it and where it stands', () => {
    expect(suggestionLine(suggestion())).toBe('Change to revenue.md, by maria: waiting for the owner.')
    expect(suggestionLine(suggestion({ kind: 'new', item: 'guides/refunds.md', state: 'approved' }))).toBe('New item guides/refunds.md, by maria: approved.')
    expect(suggestionLine(suggestion({ kind: 'verified-query', item: 'queries/revenue.md', state: 'withdrawn' }))).toBe('Verified query queries/revenue.md, by maria: withdrawn.')
  })
  it('says an agent made it, so the reviewer knows what they are reading', () => {
    expect(suggestionLine(suggestion({ via: 'agent:sales-helper' }))).toBe('Change to revenue.md, by maria through the agent sales-helper: waiting for the owner.')
  })
  it('says the item moved on, but only while the suggestion is still waiting', () => {
    expect(suggestionLine(suggestion({ moved: true }))).toContain('the item has changed since, so read both before deciding')
    expect(suggestionLine(suggestion({ moved: true, state: 'needs-another-look' }))).toContain('needs another look; the item has changed since')
    expect(suggestionLine(suggestion({ moved: true, state: 'withdrawn' }))).not.toContain('changed since')
  })
})

describe('who withdraws', () => {
  it('is the person who made it, while it is undecided', () => {
    expect(canWithdraw(suggestion(), 'maria')).toBe(true)
    expect(canWithdraw(suggestion({ state: 'needs-another-look' }), 'maria')).toBe(true)
    expect(canWithdraw(suggestion(), 'olga')).toBe(false)
    expect(canWithdraw(suggestion(), null)).toBe(false)
    expect(canWithdraw(suggestion({ state: 'approved' }), 'maria')).toBe(false)
    expect(canWithdraw(suggestion({ state: 'withdrawn' }), 'maria')).toBe(false)
  })
})

describe('the result a person saw', () => {
  const preview = (over: Partial<SuggestionPreview> = {}): SuggestionPreview => ({ model: 'Sales', model_version: '7', parameters: {}, columns: [{ name: 'region', type: 'string' }], rows: [['EMEA']], row_count: 3, truncated: false, ...over })

  it('says how many rows from which model at which version', () => {
    expect(previewLine(preview())).toBe('3 rows from Sales at version 7.')
    expect(previewLine(preview({ row_count: 1 }))).toBe('1 row from Sales at version 7.')
  })
  it('says the values it was run with and that there were more', () => {
    expect(previewLine(preview({ parameters: { quarter: 'FY27-Q1' }, truncated: true }))).toBe('3 rows or more from Sales at version 7, with quarter = FY27-Q1.')
  })
})
